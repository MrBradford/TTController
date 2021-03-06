﻿using System;
using System.Collections.Generic;
using TTController.Common;
using TTController.Common.Plugin;

namespace TTController.Service.Config
{
    public class ProfileConfig
    {
        public string Name { get; private set; } = "Default";
        public List<PortIdentifier> Ports { get; private set; } = new List<PortIdentifier>();

        public List<ISpeedControllerBase> SpeedControllers { get; private set; } = new List<ISpeedControllerBase>();
        public List<IEffectBase> Effects { get; private set; } = new List<IEffectBase>();
    }
}
