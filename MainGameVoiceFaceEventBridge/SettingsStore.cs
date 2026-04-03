using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace MainGameVoiceFaceEventBridge
{
    internal static class SettingsStore
    {
        internal const string ConfigJsonFileName = "config.json";
        internal const string LegacySettingsFileName = "VoiceFaceEventBridgeSettings.json";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static PluginSettings LoadOrCreate(
            string pluginDir,
            Action<string> logInfo,
            Action<string> logWarn,
            Action<string> logError)
        {
            string defaultPath = GetDefaultPath(pluginDir);
            string legacyPath = Path.Combine(pluginDir, LegacySettingsFileName);
            try
            {
                if (!File.Exists(defaultPath))
                {
                    var created = new PluginSettings();
                    created.Normalize();
                    Save(defaultPath, created);
                    if (File.Exists(legacyPath))
                    {
                        logWarn?.Invoke("legacy settings ignored: " + legacyPath);
                    }
                    logInfo?.Invoke("settings created: " + defaultPath);
                    return created;
                }

                string json = File.ReadAllText(defaultPath, Encoding.UTF8);
                PluginSettings parsed = Deserialize(json);
                if (parsed == null)
                {
                    logWarn?.Invoke("settings parse failed, fallback to default");
                    parsed = new PluginSettings();
                }
                else if (!ContainsJsonProperty(json, "EnableVideoPlaybackByResponseText"))
                {
                    parsed.EnableVideoPlaybackByResponseText = true;
                    logInfo?.Invoke("settings default applied: EnableVideoPlaybackByResponseText=true (missing key)");
                }

                parsed.Normalize();
                Save(defaultPath, parsed);
                return parsed;
            }
            catch (Exception ex)
            {
                logError?.Invoke("settings load failed: " + ex.Message);
                var fallback = new PluginSettings();
                fallback.Normalize();
                return fallback;
            }
        }

        internal static string GetDefaultPath(string pluginDir)
        {
            return Path.Combine(pluginDir, ConfigJsonFileName);
        }

        internal static void SaveToDefault(string pluginDir, PluginSettings settings)
        {
            Save(GetDefaultPath(pluginDir), settings);
        }

        internal static void Save(string path, PluginSettings settings)
        {
            if (settings == null)
            {
                settings = new PluginSettings();
            }

            settings.Normalize();
            var serializer = new DataContractJsonSerializer(typeof(PluginSettings));
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, settings);
                string json = Encoding.UTF8.GetString(ms.ToArray());
                File.WriteAllText(path, json, Utf8NoBom);
            }
        }

        private static PluginSettings Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var serializer = new DataContractJsonSerializer(typeof(PluginSettings));
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var ms = new MemoryStream(bytes))
            {
                return serializer.ReadObject(ms) as PluginSettings;
            }
        }

        private static bool ContainsJsonProperty(string json, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            string pattern = "\"" + propertyName + "\"";
            return json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
