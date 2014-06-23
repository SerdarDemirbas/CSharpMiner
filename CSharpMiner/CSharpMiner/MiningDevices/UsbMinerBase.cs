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

using CSharpMiner.Helpers;
using CSharpMiner.Interfaces;
using CSharpMiner.ModuleLoading;
using System;
using System.IO.Ports;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace CSharpMiner.MiningDevice
{
    [DataContract]
    public abstract class UsbMinerBase : IMiningDevice, IUSBDeviceSettings
    {
        protected const int defaultPollTime = 10;

        [IgnoreDataMember]
        public int Id { get; set; }

        [DataMember(Name = "port")]
        [MiningSetting(ExampleValue = "dev/ttyUSB0", Optional = false, Description = "The port the device is connected to. Linux /dev/tty* and Windows COM*")]
        public string Port { get; set; }

        private int _cores = 1;
        [DataMember(Name = "cores")]
        [MiningSetting(ExampleValue = "6", Optional = false, Description = "Core count. The meaning of this setting is manufacturer specific.")]
        public int Cores 
        {
            get
            {
                return _cores;
            }
            set
            {
                _cores = value;

                HashRate = this.GetTheoreticalHashrate() * Cores;
            }
        }

        [DataMember(Name = "timeout")]
        [MiningSetting(ExampleValue = "60", Optional = true, Description = "Number of seconds to wait without response before restarting the device.")]
        public int WatchdogTimeout { get; set; }

        private int _pollFrequency;
        [DataMember(Name = "poll")]
        [MiningSetting(ExampleValue = "50", Optional = true, Description = "Milliseconds the thread waits before looking for incoming data. A larger value will decrease the processor usage but shares won't be submitted right away.")]
        public int PollFrequency
        {
            get
            {
                if (_pollFrequency == 0)
                    _pollFrequency = defaultPollTime;

                return _pollFrequency;
            }
            set
            {
                if (value <= 0)
                {
                    _pollFrequency = defaultPollTime;
                }
                else
                {
                    _pollFrequency = value;
                }
            }
        }

        [IgnoreDataMember]
        public int HashRate { get; protected set; }

        [IgnoreDataMember]
        public int HardwareErrors { get; set; }

        private System.Timers.Timer _timer = null;
        [IgnoreDataMember]
        public System.Timers.Timer WorkRequestTimer
        {
            get 
            {
                if (_timer == null)
                    _timer = new System.Timers.Timer();

                return _timer;
            }
        }

        [IgnoreDataMember]
        public int Accepted { get; set; }

        [IgnoreDataMember]
        public int Rejected { get; set; }

        int _acceptedWork = 0;
        [IgnoreDataMember]
        public int AcceptedWorkUnits
        {
            get
            {
                return _acceptedWork;
            }

            set
            {
                _acceptedWork = value;
                _acceptedHashRateValid = false;
            }
        }

        int _rejectedWork = 0;
        [IgnoreDataMember]
        public int RejectedWorkUnits
        {
            get
            {
                return _rejectedWork;
            }

            set
            {
                _rejectedWork = value;
                _rejectedHashRateValid = false;
            }
        }

        int _discardedWork = 0;
        [IgnoreDataMember]
        public int DiscardedWorkUnits
        {
            get
            {
                return _discardedWork;
            }

            set
            {
                _discardedWork = value;
                _discardedHashRateValid = false;
            }
        }

        [IgnoreDataMember]
        public abstract string Name { get;}

        private double _acceptedHashRate = 0;
        private bool _acceptedHashRateValid = false;
        [IgnoreDataMember]
        public double AcceptedHashRate
        {
            get 
            { 
                if(!_acceptedHashRateValid)
                {
                    _acceptedHashRate = ComputeHashRate(AcceptedWorkUnits);
                    _acceptedHashRateValid = true;
                }

                return _acceptedHashRate;
            }
        }

        private double _rejectedHashRate = 0;
        private bool _rejectedHashRateValid = false;
        [IgnoreDataMember]
        public double RejectedHashRate
        {
            get
            {
                if (!_rejectedHashRateValid)
                {
                    _rejectedHashRate = ComputeHashRate(RejectedWorkUnits);
                    _rejectedHashRateValid = true;
                }

                return _rejectedHashRate;
            }
        }

        private double _discardedHashRate = 0;
        private bool _discardedHashRateValid = false;
        [IgnoreDataMember]
        public double DiscardedHashRate
        {
            get
            {
                if (!_discardedHashRateValid)
                {
                    _discardedHashRate = ComputeHashRate(DiscardedWorkUnits);
                    _discardedHashRateValid = true;
                }

                return _discardedHashRate;
            }
        }

        private double ComputeHashRate(int workUnits)
        {
            return 65535 * workUnits / start.Subtract(DateTime.Now).TotalSeconds; // Expected hashes per work unit * work units / sec = hashes per sec
        }

        public event Action<IMiningDevice, IPoolWork, string> ValidNonce;
        public event Action<IMiningDevice> WorkRequested;
        public event Action<IMiningDevice, IPoolWork> InvalidNonce;

        protected Thread listenerThread = null;
        protected SerialPort usbPort = null;
        protected IPoolWork pendingWork = null;

        private System.Timers.Timer watchdogTimer = null;

        private DateTime start;

        private bool continueRunning = true;

        public void Load()
        {
            start = DateTime.Now;

            Accepted = 0;
            Rejected = 0;
            HardwareErrors = 0;

            AcceptedWorkUnits = 0;
            RejectedWorkUnits = 0;
            DiscardedWorkUnits = 0;

            HashRate = GetTheoreticalHashrate();

            WorkRequestTimer.Stop();

            if(WatchdogTimeout <= 0)
            {
                WatchdogTimeout = 60; // Default to one minute if not set
            }

            watchdogTimer = new System.Timers.Timer(WatchdogTimeout * 1000);
            watchdogTimer.Elapsed += this.WatchdogExpired;
            watchdogTimer.AutoReset = true;

            Task.Factory.StartNew(this.Connect);
        }

        protected void RestartWorkRequestTimer()
        {
            WorkRequestTimer.Stop();
            WorkRequestTimer.Start();
        }

        private void WorkRequestTimerExpired(object sender, System.Timers.ElapsedEventArgs e)
        {
            RequestWork();
        }

        private void WatchdogExpired(object sender, System.Timers.ElapsedEventArgs e)
        {
            if(this.WorkRequested != null)
            {
                LogHelper.ConsoleLogErrorAsync(string.Format("Device {0} hasn't responded for {1} sec. Restarting.", this.Name, (double)WatchdogTimeout / 1000));
                RequestWork();
            }
        }
        
        protected void RequestWork()
        {
            if (this.WorkRequested != null)
            {
                Task.Factory.StartNew(() =>
                    {
                        this.WorkRequested(this);
                    });
            }
        }

        protected void RestartWatchdogTimer()
        {
            if(watchdogTimer != null)
            {
                watchdogTimer.Stop();
                watchdogTimer.Start();
            }
        }

        private void Connect()
        {
            RestartWatchdogTimer();

            try
            {
                string[] portNames = SerialPort.GetPortNames();

                if (!portNames.Contains(Port))
                {
                    Exception e = new SerialConnectionException(string.Format("{0} is not a valid USB port.", (Port != null ? Port : "null")));

                    LogHelper.LogErrorSecondary(e);

                    throw e;
                }

                try
                {
                    continueRunning = true;
                    usbPort = new SerialPort(Port, GetBaud());
                    //usbPort.DataReceived += DataReceived; // This works on .NET in windows but not in Mono
                    usbPort.Open();
                }
                catch (Exception e)
                {
                    LogHelper.ConsoleLogErrorAsync(string.Format("Error connecting to {0}.", Port));
                    throw new SerialConnectionException(string.Format("Error connecting to {0}: {1}", Port, e), e);
                }

                LogHelper.ConsoleLogAsync(string.Format("Successfully connected to {0}.", Port), LogVerbosity.Verbose);

                if (this.pendingWork != null)
                {
                    Task.Factory.StartNew(() =>
                        {
                            this.StartWork(pendingWork);
                            pendingWork = null;
                        });
                }

                if (this.listenerThread == null)
                {
                    this.listenerThread = new Thread(new ThreadStart(() =>
                        {
                            try
                            {
                                while (this.continueRunning)
                                {
                                    if (usbPort.BytesToRead > 0)
                                    {
                                        DataReceived(usbPort, null);
                                    }

                                    Thread.Sleep(PollFrequency);
                                }
                            }
                            catch (Exception e)
                            {
                                LogHelper.LogErrorAsync(e);

                                Task.Factory.StartNew(() =>
                                    {
                                        this.Restart();
                                    });
                            }
                        }));
                    this.listenerThread.Start();
                }
            }
            catch (Exception e)
            {
                LogHelper.LogErrorAsync(e);
                this.Restart();
            }
        }

        private void Restart()
        {
            this.Unload();
            this.Load();
        }

        protected void SubmitWork(IPoolWork work, string nonce)
        {
            this.OnValidNonce(work, nonce);
        }

        public void Unload()
        {
            if (continueRunning)
            {
                if(this.watchdogTimer != null)
                {
                    this.watchdogTimer.Stop();
                }

                continueRunning = false;

                if (usbPort != null && usbPort.IsOpen)
                    usbPort.Close();

                if(listenerThread != null)
                    listenerThread.Join();

                listenerThread = null;
            }
        }
        
        protected void OnValidNonce(IPoolWork work, string nonce)
        {
            this.RestartWatchdogTimer();

            if (this.ValidNonce != null)
            {
                Task.Factory.StartNew(() =>
                    {
                        this.ValidNonce(this, work, nonce);
                    });
            }
        }

        protected void OnInvalidNonce(IPoolWork work)
        {
            if (this.InvalidNonce != null)
            {
                Task.Factory.StartNew(() =>
                    {
                        this.InvalidNonce(this, work);
                    });
            }
        }

        public void Dispose()
        {
            this.Unload();
        }

        public abstract void StartWork(IPoolWork work);
        public abstract int GetBaud();
        protected abstract void DataReceived(object sender, SerialDataReceivedEventArgs e);
        protected abstract int GetTheoreticalHashrate();
        public abstract void WorkRejected(IPoolWork work);
    }
}
