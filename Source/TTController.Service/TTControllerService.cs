﻿using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using TTController.Common;
using TTController.Service.Config.Data;
using TTController.Service.Hardware.Sensor;
using TTController.Service.Manager;
using TTController.Service.Utils;
using System.Threading.Tasks;
using System.Threading;
using TTController.Common.Plugin;
using Microsoft.Extensions.Configuration;

namespace TTController.Service
{
    public class TTControllerService : BackgroundService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly IConfiguration _configuration;

        private DeviceManager _deviceManager;
        private ConfigManager _configManager;
        private SensorManager _sensorManager;
        private TimerManager _timerManager;
        private EffectManager _effectManager;
        private SpeedControllerManager _speedControllerManager;
        private DataCache _cache;

        public TTControllerService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public bool Initialize()
        {
            Logger.Info($"{new string('=', 64)}");
            Logger.Info("Initializing...");
            PluginLoader.Load(Path.Combine(AppContext.BaseDirectory, "Plugins"));

            _configManager = new ConfigManager(_configuration.GetValue<string>("ProfileConfigFile"));
            if (!_configManager.LoadOrCreateConfig())
                return false;

            _cache = new DataCache();

            var alpha = Math.Exp(-_configManager.CurrentConfig.SensorTimerInterval / (double)_configManager.CurrentConfig.DeviceSpeedTimerInterval);
            var providerFactory = new MovingAverageSensorValueProviderFactory(alpha);
            var sensorConfigs = _configManager.CurrentConfig.SensorConfigs
                .SelectMany(x => x.Sensors.Select(s => (Sensor: s, Config: x.Config)))
                .ToDictionary(x => x.Sensor, x => x.Config);

            _sensorManager = new SensorManager(providerFactory, sensorConfigs);
            _effectManager = new EffectManager();
            _speedControllerManager = new SpeedControllerManager();
            _deviceManager = new DeviceManager();

            _sensorManager.EnableSensors(sensorConfigs.Keys);
            foreach (var profile in _configManager.CurrentConfig.Profiles)
            {
                foreach (var effect in profile.Effects)
                {
                    _effectManager.Add(profile.Guid, effect);
                    _sensorManager.EnableSensors(effect.UsedSensors);
                }

                foreach (var speedController in profile.SpeedControllers)
                {
                    _speedControllerManager.Add(profile.Guid, speedController);
                    _sensorManager.EnableSensors(speedController.UsedSensors);
                }
            }

            foreach (var sensor in _sensorManager.EnabledSensors)
                _cache.StoreSensorConfig(sensor, SensorConfig.Default);

            foreach (var controller in _deviceManager.Controllers)
                foreach (var port in controller.Ports)
                    _cache.StorePortConfig(port, PortConfig.Default);

            foreach (var (ports, config) in _configManager.CurrentConfig.PortConfigs)
                foreach (var port in ports)
                    _cache.StorePortConfig(port, config);

            foreach (var (sensors, config) in _configManager.CurrentConfig.SensorConfigs)
                foreach (var sensor in sensors)
                    _cache.StoreSensorConfig(sensor, config);

            ApplyComputerStateProfile(ComputerStateType.Boot);

            _timerManager = new TimerManager();
            _timerManager.RegisterTimer(_configManager.CurrentConfig.SensorTimerInterval, SensorTimerCallback);
            _timerManager.RegisterTimer(_configManager.CurrentConfig.DeviceSpeedTimerInterval, DeviceSpeedTimerCallback);
            _timerManager.RegisterTimer(_configManager.CurrentConfig.DeviceRgbTimerInterval, DeviceRgbTimerCallback);
            if (LogManager.Configuration.LoggingRules.Any(r => r.IsLoggingEnabledForLevel(LogLevel.Debug)))
                _timerManager.RegisterTimer(_configManager.CurrentConfig.LoggingTimerInterval, LoggingTimerCallback);

            _timerManager.Start();

            Logger.Info("Initializing done!");
            Logger.Info($"{new string('=', 64)}");
            return true;
        }

        private void ApplyComputerStateProfile(ComputerStateType state)
        {
            //TODO: check if boot profiles were saved before

            Logger.Info("Applying {0} profiles", state);
            lock (_deviceManager)
            {
                var dirtyControllers = new HashSet<IControllerProxy>();
                foreach (var profile in _configManager.CurrentConfig.ComputerStateProfiles.Where(p => p.StateType == state))
                {
                    foreach (var port in profile.Ports)
                    {
                        var controller = _deviceManager.GetController(port);
                        if (controller == null)
                            continue;

                        if (profile.Speed.HasValue)
                            controller.SetSpeed(port.Id, profile.Speed.Value);

                        var effectByte = controller.GetEffectByte(profile.EffectType);
                        if (effectByte.HasValue && profile.EffectColors != null)
                            controller.SetRgb(port.Id, effectByte.Value, profile.EffectColors);

                        if (state == ComputerStateType.Boot && (profile.Speed.HasValue || effectByte.HasValue))
                            dirtyControllers.Add(controller);
                    }
                }

                foreach (var controller in dirtyControllers)
                    controller.SaveProfile();
            }
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!Initialize())
                    throw new Exception("Service failed to start!");
            }
            catch (Exception e)
            {
                Logger.Fatal(e);
                throw;
            }

            return base.StartAsync(cancellationToken);
        }

        public override Task StopAsync(CancellationToken cancellationToken) => base.StopAsync(cancellationToken);
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Delay(-1, stoppingToken);

        public override void Dispose()
        {
            Logger.Info($"{new string('=', 64)}");
            Logger.Info("Finalizing...");

            _timerManager?.Dispose();

            if (_deviceManager != null)
                ApplyComputerStateProfile(ComputerStateType.Shutdown);

            _sensorManager?.Dispose();
            _deviceManager?.Dispose();
            _effectManager?.Dispose();
            _speedControllerManager?.Dispose();
            _configManager?.Dispose();
            _cache?.Clear();

            _timerManager = null;
            _deviceManager = null;
            _sensorManager = null;
            _deviceManager = null;
            _effectManager = null;
            _speedControllerManager = null;
            _configManager = null;
            _cache = null;

            base.Dispose();

            Logger.Info("Finalizing done!");
            Logger.Info($"{new string('=', 64)}");
        }

        #region Timer Callbacks
        private bool SensorTimerCallback()
        {
            _sensorManager.Update();
            _sensorManager.Accept(_cache.AsWriteOnly());
            return true;
        }

        private bool DeviceSpeedTimerCallback()
        {
            var criticalState = _sensorManager.EnabledSensors.Any(s => {
                var value = _cache.GetSensorValue(s);
                if (float.IsNaN(value))
                    return false;

                var config = _cache.GetSensorConfig(s);
                if (config == null || config.CriticalValue == null)
                    return false;

                return value > config.CriticalValue;
            });

            foreach (var profile in _configManager.CurrentConfig.Profiles)
            {
                lock (_deviceManager)
                {
                    foreach (var port in profile.Ports)
                    {
                        var controller = _deviceManager.GetController(port);
                        var data = controller?.GetPortData(port.Id);
                        _cache.StorePortData(port, data);
                    }
                }

                IDictionary<PortIdentifier, byte> speedMap;
                if (criticalState)
                {
                    speedMap = profile.Ports.ToDictionary(p => p, _ => (byte)100);
                }
                else
                {
                    var speedControllers = _speedControllerManager.GetSpeedControllers(profile.Guid);
                    var speedController = speedControllers?.FirstOrDefault(c => c.IsEnabled(_cache.AsReadOnly()));
                    if (speedController == null)
                        continue;

                    try
                    {
                        speedMap = speedController.GenerateSpeeds(profile.Ports, _cache.AsReadOnly());
                    }
                    catch (Exception e)
                    {
                        Logger.Fatal("{0} failed with {1}", speedController.GetType().Name, e);
                        speedMap = profile.Ports.ToDictionary(p => p, _ => (byte)100);
                    }
                }

                if (speedMap == null)
                    continue;

                lock (_deviceManager)
                {
                    foreach (var (port, speed) in speedMap)
                    {
                        var controller = _deviceManager.GetController(port);
                        if (controller == null)
                            continue;

                        controller.SetSpeed(port.Id, speed);
                    }
                }
            }

            return true;
        }

        public bool DeviceRgbTimerCallback()
        {
            void ApplyConfig(IDictionary<PortIdentifier, List<LedColor>> colorMap)
            {
                foreach (var port in colorMap.Keys.ToList())
                {
                    var config = _cache.GetPortConfig(port);
                    if (config == null)
                        continue;

                    var colors = colorMap[port];

                    if (config.LedRotation > 0)
                        colors = colors.Skip(config.LedRotation).Concat(colors.Take(config.LedRotation)).ToList();
                    if (config.LedReverse)
                        colors.Reverse();

                    switch (config.LedCountHandling)
                    {
                        case LedCountHandling.Lerp:
                            {
                                if (config.LedCount == colors.Count)
                                    break;

                                var newColors = new List<LedColor>();
                                var gradient = new LedColorGradient(colors, config.LedCount - 1);

                                for (var i = 0; i < config.LedCount; i++)
                                    newColors.Add(gradient.GetColor(i));

                                colors = newColors;
                                break;
                            }
                        case LedCountHandling.Nearest:
                            {
                                if (config.LedCount == colors.Count)
                                    break;

                                var newColors = new List<LedColor>();
                                for (var i = 0; i < config.LedCount; i++)
                                {
                                    var idx = (int)Math.Round((i / (config.LedCount - 1d)) * (colors.Count - 1d));
                                    newColors.Add(colors[idx]);
                                }

                                colors = newColors;
                                break;
                            }
                        case LedCountHandling.Wrap:
                            if (config.LedCount < colors.Count)
                                break;

                            var remainder = colors.Count % config.LedCount;
                            colors = colors.Skip(colors.Count - remainder)
                                .Concat(colors.Take(colors.Count - remainder).Skip(colors.Count - config.LedCount))
                                .ToList();
                            break;
                        case LedCountHandling.Trim:
                            if (config.LedCount < colors.Count)
                                colors.RemoveRange(config.LedCount, colors.Count - config.LedCount);
                            break;
                        case LedCountHandling.Copy:
                            while (config.LedCount > colors.Count)
                                colors.AddRange(colors.Take(config.LedCount - colors.Count).ToList());
                            break;
                        case LedCountHandling.DoNothing:
                        default:
                            break;
                    }

                    colorMap[port] = colors;
                }
            }

            foreach (var profile in _configManager.CurrentConfig.Profiles)
            {
                var effects = _effectManager.GetEffects(profile.Guid);
                var effect = effects?.FirstOrDefault(e => e.IsEnabled(_cache.AsReadOnly()));
                if (effect == null)
                    continue;

                IDictionary<PortIdentifier, List<LedColor>> colorMap;
                string effectType;

                try
                {
                    colorMap = effect.GenerateColors(profile.Ports, _cache.AsReadOnly());
                    effectType = effect.EffectType;
                }
                catch (Exception e)
                {
                    Logger.Fatal("{0} failed with {1}", effect.GetType().Name, e);
                    colorMap = profile.Ports.ToDictionary(p => p, _ => new List<LedColor>() { new LedColor(255, 0, 0) });
                    effectType = "Full";
                }

                if (colorMap == null)
                    continue;

                ApplyConfig(colorMap);

                lock (_deviceManager)
                {
                    foreach (var (port, colors) in colorMap)
                    {
                        if (colors == null)
                            continue;

                        var controller = _deviceManager.GetController(port);
                        var effectByte = controller?.GetEffectByte(effectType);
                        if (effectByte == null)
                            continue;

                        controller.SetRgb(port.Id, effectByte.Value, colors);
                    }
                }
            }

            return true;
        }

        public bool LoggingTimerCallback()
        {
            foreach (var profile in _configManager.CurrentConfig.Profiles)
            {
                foreach (var port in profile.Ports)
                {
                    var data = _cache.GetPortData(port);
                    if (data == null)
                        continue;

                    Logger.Debug("Port {0} data: {1}", port, data);
                }
            }

            lock (_sensorManager)
            {
                foreach (var identifier in _sensorManager.EnabledSensors)
                {
                    var value = _sensorManager.GetSensorValue(identifier);
                    if (float.IsNaN(value))
                        continue;
                    Logger.Debug("Sensor \"{0}\" value: {1}", identifier, value);
                }
            }

            return true;
        }
        #endregion
    }
}
