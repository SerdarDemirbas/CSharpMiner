﻿/*  Copyright (C) 2014 Colton Manville
    This file is part of CSharpMiner.

    CSharpMiner is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    CSharpMiner is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with CSharpMiner.  If not, see <http://www.gnu.org/licenses/>.*/

using CSharpMiner;
using CSharpMiner.Helpers;
using CSharpMiner.ModuleLoading;
using CSharpMiner.Pools;
using CSharpMiner.Stratum;
using DeviceLoader;
using MiningDevice;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace DeviceManager
{
    [DataContract]
    public abstract class WorkManagerBase : IMiningDeviceManager
    {
        [DataMember(Name = "pools", IsRequired=true)]
        [MiningSetting( Description = "A collection of pools to connect to. This connects to the first pool and only uses the other pools if the first one fails. It does not automatically go back to the first pool if it becomes available again.", Optional = false)]
        public StratumPool[] Pools { get; set; }

        [DataMember(Name = "devices")]
        [MiningSetting(Description="A collection of MiningDevice or DeviceLoader JSON objects.", Optional=false, 
           ExampleValue=@"[
    {
		    '__type' : 'ZeusDeviceLoader:#DeviceLoader',
		    'ports' : ['/dev/ttyUSB0', '/dev/ttyUSB1', '/dev/ttyUSB2', '/dev/ttyUSB3', '/dev/ttyUSB4', '/dev/ttyUSB5', 'COM1' ],
		    'cores' : 6,
		    'clock' : 328
    },
    {
		    '__type' : 'ZeusDevice:#MiningDevice',
		    'port' : '/dev/ttyUSB9',
		    'cores' : 6,
		    'clock' : 382
    }
]")]
        public IMiningDevice[] MiningDevices { get; set; }

        [IgnoreDataMember]
        public IPool ActivePool { get; private set; }

        [IgnoreDataMember]
        public int ActivePoolId { get; private set; }

        [IgnoreDataMember]
        public IEnumerable<IMiningDevice> LoadedDevices
        {
            get { return loadedDevices; }
        }

        private bool working = false;
        private bool started = false;
        protected List<IMiningDevice> loadedDevices = null;
        protected List<IHotplugLoader> hotplugLoaders = null;

        protected IPoolWork currentWork = null;
        protected IPoolWork nextWork = null;

        private int deviceId = 0;

        protected abstract void StartWork(IPoolWork work, IMiningDevice device, bool restartAll, bool requested);
        protected abstract void NoWork(IPoolWork oldWork, IMiningDevice device, bool requested);
        protected abstract void SetUpDevice(IMiningDevice d);

        bool boundPools = false;

        public void NewWork(IPool pool, IPoolWork newWork, bool forceStart)
        {
            if (started && ActivePool != null)
            {
                if (newWork != null)
                {
                    // Pool asked us to toss out our old work or we don't have any work yet
                    if (forceStart || currentWork == null)
                    {
                        currentWork = newWork;
                        nextWork = newWork;

                        working = true;
                        StartWork(newWork, null, true, false);
                    }
                    else // We can keep the old work
                    {
                        if (!working)
                        {
                            currentWork = newWork;
                            nextWork = newWork;

                            working = true;
                            StartWork(newWork, null, false, false);
                        }
                        else
                        {
                            nextWork = newWork;
                        }
                    }
                }
            }
        }

        public void SubmitWork(IMiningDevice device, IPoolWork work, string nonce)
        {
            if (started && this.ActivePool != null && currentWork != null && this.ActivePool.Connected)
            {
                Task.Factory.StartNew(() =>
                    {
                        this.ActivePool.SubmitWork(work, new Object[] {device, nonce});
                    });

                StartWorkOnDevice(work, device, false);
            }
            else if(this.ActivePool != null && !this.ActivePool.Connected && !this.ActivePool.Connecting)
            {
                //Attempt to connect to another pool
                this.AttemptPoolReconnect();
            }
        }

        public void StartWorkOnDevice(IPoolWork work, IMiningDevice device, bool requested)
        {
            if (nextWork.JobId != currentWork.JobId)
            {
                // Start working on the last thing the server sent us
                currentWork = nextWork;

                StartWork(nextWork, device, false, requested);
            }
            else
            {
                working = false;
                NoWork(work, device, requested);
            }
        }

        public void RequestWork(IMiningDevice device)
        {
            LogHelper.ConsoleLogAsync(string.Format("Device {0} requested new work.", device.Id));
            StartWorkOnDevice(this.currentWork, device, true);
        }

        public void Start()
        {
            if(!boundPools)
            {
                foreach(StratumPool pool in this.Pools)
                {
                    pool.Disconnected += this.PoolDisconnected;
                    pool.NewWorkRecieved += this.NewWork;
                }

                boundPools = true;
            }

            // TODO: Make this throw an exception so that the system doesn't infinate loop
            Task.Factory.StartNew(() =>
            {
                loadedDevices = new List<IMiningDevice>();
                hotplugLoaders = new List<IHotplugLoader>();

                foreach(IMiningDevice d in this.MiningDevices)
                {
                    LoadDevice(d);
                }

                if(Pools.Length > 0)
                {
                    this.ActivePool = Pools[0];
                    this.ActivePoolId = 0;
                    this.ActivePool.Start();
                }

                started = true;
            });
        }

        private void LoadDevice(IMiningDevice d)
        {
            IDeviceLoader loader = d as IDeviceLoader;

            if (loader != null)
            {
                foreach (IMiningDevice device in loader.LoadDevices())
                {
                    LoadDevice(device);
                }
            }
            else
            {
                IHotplugLoader hotplugLoader = d as IHotplugLoader;

                if (hotplugLoader != null)
                {
                    hotplugLoader.StartListening(this.AddNewDevice);
                    hotplugLoaders.Add(hotplugLoader);
                }
                else
                {
                    d.Id = deviceId;
                    deviceId++;

                    d.ValidNonce += this.SubmitWork;
                    d.WorkRequested += this.RequestWork;
                    d.InvalidNonce += this.InvalidNonce;

                    d.Load();
                    loadedDevices.Add(d);

                    this.SetUpDevice(d);
                }
            }
        }

        public void AddNewDevice(IMiningDevice d)
        {
            Task.Factory.StartNew(() =>
                {
                    LoadDevice(d);
                });
        }

        public void Stop()
        {
            if(boundPools)
            {
                foreach(StratumPool pool in this.Pools)
                {
                    pool.Disconnected -= this.PoolDisconnected;
                    pool.NewWorkRecieved -= this.NewWork;
                }
            }

            if (this.started)
            {
                this.started = false;

                if (this.hotplugLoaders != null)
                {
                    foreach (IHotplugLoader hotplugLoader in hotplugLoaders)
                    {
                        if (hotplugLoader != null)
                        {
                            hotplugLoader.StopListening();
                        }
                    }
                }

                if (this.loadedDevices != null)
                {
                    foreach (IMiningDevice d in this.loadedDevices)
                    {
                        if (d != null)
                        {
                            d.WorkRequested -= this.RequestWork;
                            d.ValidNonce -= this.SubmitWork;
                            d.InvalidNonce -= this.InvalidNonce;

                            d.Unload();
                        }
                    }
                }

                if (this.ActivePool != null)
                {
                    this.ActivePool.Stop();
                    this.ActivePool = null;
                }
            }
        }

        public void InvalidNonce(IMiningDevice device, IPoolWork work)
        {
            throw new NotImplementedException();
        }

        public void PoolDisconnected(IPool pool)
        {
            StratumPool stratumPool = pool as StratumPool;

            if (stratumPool != null)
            {
                LogHelper.ConsoleLogErrorAsync(string.Format("Disconnected from pool {0}", stratumPool.Url));
                LogHelper.LogErrorAsync(string.Format("Disconnected from pool {0}", stratumPool.Url));
            }

            if(stratumPool == this.ActivePool)
                AttemptPoolReconnect();
        }

        public void AttemptPoolReconnect()
        {
            // TODO: Handle when all pools are unable to be reached
            if (this.started && this.ActivePool != null && !this.ActivePool.Connecting)
            {
                this.ActivePool.Stop();
                this.ActivePool = null;

                if (this.ActivePoolId + 1 < this.Pools.Length)
                {
                    this.ActivePoolId++;
                }
                else
                {
                    this.ActivePoolId = 0;
                }

                this.ActivePool = this.Pools[this.ActivePoolId];
                LogHelper.ConsoleLog(string.Format("Attempting to connect to pool {0}", this.ActivePool.Url));
                this.ActivePool.Start();
            }
        }

        public void AddNewPool(StratumPool pool)
        {
            throw new NotImplementedException();
        }

        public void RemovePool(int poolIndex)
        {
            throw new NotImplementedException();
        }

        public void RemoveDevice(int deviceIndex)
        {
            throw new NotImplementedException();
        }
    }
}
