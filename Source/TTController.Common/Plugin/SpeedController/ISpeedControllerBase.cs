﻿using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;

namespace TTController.Common.Plugin
{
    public interface ISpeedControllerBase : IPlugin, IDisposable
    {
        bool IsEnabled(ICacheProvider cache);
        IEnumerable<Identifier> UsedSensors { get; }
        IDictionary<PortIdentifier, byte> GenerateSpeeds(List<PortIdentifier> ports, ICacheProvider cache);
    }
}
