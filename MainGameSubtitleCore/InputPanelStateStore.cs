using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace MainGameSubtitleCore
{
    [DataContract]
    internal sealed class InputPanelState
    {
        [DataMember] public List<string> History = new List<string>();
        [DataMember] public List<string> Presets = new List<string>();

        internal void Normalize(int historyMax, int presetMax)
        {
            History = NormalizeList(History, historyMax);
            Presets = NormalizeList(Presets, presetMax);
        }

        private static List<string> NormalizeList(List<string> source, int max)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (source != null)
            {
                foreach (string raw in source)
                {
                    string text = (raw ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    if (seen.Contains(text))
                    {
                        continue;
                    }

                    result.Add(text);
                    seen.Add(text);
                    if (result.Count >= max)
                    {
                        break;
                    }
                }
            }

            return result;
        }
    }

    internal static class InputPanelStateStore
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static InputPanelState LoadOrCreate(
            string path,
            int historyMax,
            int presetMax,
            Action<string> logInfo,
            Action<string> logWarn,
            Action<string> logError)
        {
            try
            {
                if (!File.Exists(path))
                {
                    var created = new InputPanelState();
                    created.Normalize(historyMax, presetMax);
                    Save(path, created);
                    logInfo?.Invoke("input panel state created: " + path);
                    return created;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                InputPanelState parsed = Deserialize(json);
                if (parsed == null)
                {
                    logWarn?.Invoke("input panel state parse failed, fallback to empty");
                    parsed = new InputPanelState();
                }

                parsed.Normalize(historyMax, presetMax);
                Save(path, parsed);
                return parsed;
            }
            catch (Exception ex)
            {
                logError?.Invoke("input panel state load failed: " + ex.Message);
                var fallback = new InputPanelState();
                fallback.Normalize(historyMax, presetMax);
                return fallback;
            }
        }

        internal static void Save(string path, InputPanelState state)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var serializer = new DataContractJsonSerializer(typeof(InputPanelState));
                using (var ms = new MemoryStream())
                {
                    serializer.WriteObject(ms, state ?? new InputPanelState());
                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    File.WriteAllText(path, json, Utf8NoBom);
                }
            }
            catch
            {
            }
        }

        private static InputPanelState Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var serializer = new DataContractJsonSerializer(typeof(InputPanelState));
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var ms = new MemoryStream(bytes))
            {
                return serializer.ReadObject(ms) as InputPanelState;
            }
        }
    }
}
