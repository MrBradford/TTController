﻿using NLog;
using OpenHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;

namespace TTController.Service.Hardware
{
    public sealed class OpenHardwareMonitorFacade : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly Computer _computer;
        private readonly List<ISensor> _sensors;

        public IReadOnlyList<ISensor> Sensors => _sensors;

        public OpenHardwareMonitorFacade()
        {
            Logger.Info("Initializing Open Hardware Monitor...");

            _sensors = new List<ISensor>();
            _computer = new Computer()
            {
                CPUEnabled = true,
                GPUEnabled = true,
                HDDEnabled = true
            };

            _computer.Open();
            _computer.Accept(new SensorVisitor(sensor =>
            {
                _sensors.Add(sensor);
                sensor.ValuesTimeWindow = TimeSpan.Zero;

                Logger.Trace("Valid sensor identifier: {0}", sensor.Identifier);
            }));

            Logger.Debug("Detected {0} sensors", _sensors.Count);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            Logger.Info("Finalizing Open Hardware Monitor...");

            _computer?.Close();
            _sensors.Clear();
        }
    }
}
