﻿using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TTController.Service.Utils;

namespace TTController.Service.Config.Converter
{
    public abstract class AbstractPluginConverter<TPlugin, TConfig> : JsonConverter<TPlugin>
    {
        public override TPlugin ReadJson(JsonReader reader, Type objectType, TPlugin existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var o = JToken.ReadFrom(reader) as JObject;

            var typeProperty = o.GetValue("Type");
            if(typeProperty == null)
                throw new JsonReaderException("Missing required property: \"Type\"");

            var configProperty = o.GetValue("Config");

            var pluginTypeName = typeProperty.ToString();
            var configTypeName = $"{pluginTypeName}Config";

            Type pluginType;
            try
            {
                pluginType = typeof(TPlugin).FindImplementations()
                    .First(t => string.CompareOrdinal(t.Name, pluginTypeName) == 0);
            }
            catch
            {
                throw new JsonReaderException($"Invalid plugin name \"{pluginTypeName}\"");
            }

            var configType = pluginType.BaseType.GetGenericArguments().First();
            var configJson = configProperty != null ? configProperty.ToString() : "";
            var config = (TConfig) JsonConvert.DeserializeObject(configJson, configType);

            return (TPlugin) Activator.CreateInstance(pluginType, config);
        }

        public override void WriteJson(JsonWriter writer, TPlugin value, JsonSerializer serializer)
        {
            var pluginType = value.GetType();
            var config = pluginType.GetProperty("Config")?.GetValue(value, null);

            var o = new JObject
            {
                {"Type", JToken.FromObject(pluginType.Name)}
            };

            if (config != null)
                o.Add("Config", JToken.FromObject(config));

            o.WriteTo(writer);
        }
    }
}
