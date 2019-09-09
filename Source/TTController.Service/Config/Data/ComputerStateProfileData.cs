﻿using System.Collections.Generic;
using System.ComponentModel;
using TTController.Common;

namespace TTController.Service.Config.Data
{
    public enum ComputerStateType
    {
        Boot,
        Shutdown
    }

    public class ComputerStateProfileData
    {
        [DefaultValue(ComputerStateType.Shutdown)] public ComputerStateType StateType { get; private set; } = ComputerStateType.Shutdown;
        public List<PortIdentifier> Ports { get; private set; } = new List<PortIdentifier>();
        [DefaultValue(null)] public byte? Speed { get; private set; } = null;
        [DefaultValue(null)] public string EffectType { get; private set; } = null;
        public List<LedColor> EffectColors { get; private set; } = new List<LedColor>();
    }
}
