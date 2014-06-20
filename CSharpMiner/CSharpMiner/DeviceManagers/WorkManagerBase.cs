﻿using CSharpMiner;
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
    public delegate void SubmitMinerWorkDelegate(PoolWork work, string nonce);

    [DataContract]
    public abstract class WorkManagerBase : IMiningDeviceManager
    {
        [DataMember(Name = "pools")]
        public Pool[] Pools { get; set; }

        [DataMember(Name = "devices")]
        public IMiningDevice[] MiningDevices { get; private set; }

        [IgnoreDataMember]
        public Pool ActivePool { get; private set; }

        [IgnoreDataMember]
        public int ActivePoolId { get; private set; }

        protected Stack workStack = null;
        bool working = false;
        bool started = false;
        protected List<IMiningDevice> loadedDevices = null;
        protected List<IHotplugLoader> hotplugLoaders = null;

        protected abstract void StartWork(PoolWork work);
        protected abstract void NoWork(PoolWork oldWork);

        public void NewWork(object[] poolWorkData)
        {
            if (started && ActivePool != null && workStack != null)
            {
                PoolWork newWork = new PoolWork(poolWorkData, ActivePool.Extranonce1, "00000000");

                // Pool asked us to toss out our old work
                if (poolWorkData[8].Equals(true))
                {
                    Program.DebugConsoleLog("New block!");

                    workStack.Clear();

                    working = true;
                    StartWork(newWork);
                }
                else // We can keep the old work
                {
                    if (!working)
                    {
                        working = true;
                        StartWork(newWork);
                    }
                    else
                    {
                        workStack.Push(newWork);
                    }
                }
            }
        }

        public void SubmitWork(PoolWork work, string nonce)
        {
            if (started && this.ActivePool != null && workStack != null)
            {
                this.ActivePool.SubmitWork(work.JobId, work.Extranonce2, work.Timestamp, nonce);

                if (workStack.Count != 0)
                {
                    // Start working on the last thing the server sent us
                    StartWork(workStack.Pop() as PoolWork);
                }
                else
                {
                    working = false;
                    NoWork(work);
                }
            }
        }

        public void Start()
        {
            workStack = Stack.Synchronized(new Stack());

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
                    this.ActivePool.Start(this.NewWork, this.PoolDisconnected);
                }

                started = true;
            });
        }

        private void LoadDevice(IMiningDevice d)
        {
            IDeviceLoader loader = d as IDeviceLoader;

            if (d != null)
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
                    d.Load(this.SubmitWork);
                    loadedDevices.Add(d);
                }
            }
        }

        private void AddNewDevice(IMiningDevice d)
        {
            Task.Factory.StartNew(() =>
                {
                    LoadDevice(d);
                });
        }

        public void Stop()
        {
            if (this.started)
            {
                this.started = false;

                if (this.hotplugLoaders != null)
                {
                    foreach (IHotplugLoader hotplugLoader in hotplugLoaders)
                    {
                        hotplugLoader.StopListening();
                    }
                }

                if (this.loadedDevices != null)
                {
                    foreach (IMiningDevice d in this.loadedDevices)
                    {
                        d.Unload();
                    }
                }

                if (this.ActivePool != null)
                {
                    this.ActivePool.Stop();
                    this.ActivePool.Thread.Join();

                    this.ActivePool = null;
                }
            }
        }

        public void PoolDisconnected()
        {
            // TODO: Handle when all pools are unable to be reached
            if(this.started && this.ActivePool != null)
            {
                this.ActivePool = null;

                if(this.ActivePoolId + 1 < this.Pools.Length)
                {
                    this.ActivePoolId++;
                }
                else
                {
                    this.ActivePoolId = 0;
                }

                this.ActivePool = this.Pools[this.ActivePoolId];
                this.ActivePool.Start(this.NewWork, this.PoolDisconnected);
            }
        }
    }
}