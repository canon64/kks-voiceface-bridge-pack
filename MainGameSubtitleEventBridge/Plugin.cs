using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using MainGameSubtitleCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace MainGameSubtitleEventBridge
{
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInDependency("com.kks.maingame.subtitlecore", BepInDependency.DependencyFlags.HardDependency)]
    [BepInProcess("KoikatsuSunshine")]
    internal sealed class Plugin : BaseUnityPlugin
    {
        [DataContract]
        private sealed class SubtitleIncomingRequest
        {
            [DataMember(Name = "text")] public string Text;
            [DataMember(Name = "hold_seconds")] public float? HoldSeconds;
            [DataMember(Name = "backend")] public string Backend;
            [DataMember(Name = "display_mode")] public string DisplayMode;
            [DataMember(Name = "speaker_gender")] public string SpeakerGender;
            [DataMember(Name = "speaker")] public string Speaker;
        }

        private sealed class HttpRequestData
        {
            public string Method;
            public string Path;
            public Dictionary<string, string> Headers;
            public byte[] Body;
        }

        private enum SpeakerKind
        {
            Unknown = 0,
            Male = 1,
            Female = 2
        }

        public const string GUID = "com.kks.maingame.subtitleeventbridge";
        public const string PluginName = "MainGameSubtitleEventBridge";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Logger { get; private set; }
        internal static PluginSettings Settings { get; private set; }

        private static readonly object FileLogLock = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly DataContractJsonSerializer IncomingSerializer =
            new DataContractJsonSerializer(typeof(SubtitleIncomingRequest));
        private static readonly Regex ColorTagRegex =
            new Regex(@"<color\s*=\s*['""]?(?<value>[^>""']+)['""]?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string _pluginDir;
        private static string _logFilePath;
        private TcpListener _listener;
        private Thread _serverThread;
        private volatile bool _running;

        private readonly Queue<SpeakerKind> _chimeQueue = new Queue<SpeakerKind>();
        private readonly object _chimeQueueLock = new object();
        private NotificationAudioPlayer _audioPlayer;

        // チャイム設定はSettingsのJSONから読む

        private void Awake()
        {
            Logger = base.Logger;
            _pluginDir = Path.GetDirectoryName(Info.Location);
            _logFilePath = Path.Combine(_pluginDir, PluginName + ".log");

            Directory.CreateDirectory(_pluginDir);
            File.WriteAllText(
                _logFilePath,
                $"[{DateTime.Now:HH:mm:ss}] === {PluginName} {Version} started ==={Environment.NewLine}",
                Utf8NoBom);

            _audioPlayer = new NotificationAudioPlayer(_pluginDir, "chime", LogWarn, LogError);

            ReloadSettings(forceRestartServer: false);
            StartServer();
            Log(
                $"chime config: enabled={Settings.EnableChime}, volume={Settings.ChimeVolume:0.00}, " +
                $"male={Settings.MaleChimeFile}, female={Settings.FemaleChimeFile}");
            Log("awake complete");
        }

        private void OnDestroy()
        {
            StopServer();
            _audioPlayer?.Dispose();
            _audioPlayer = null;
        }

        private void Update()
        {
            if (Settings != null && Settings.EnableCtrlRReload && IsCtrlRDown())
            {
                ReloadSettings(forceRestartServer: true);
                Log("settings reloaded by Ctrl+R");
            }

            DrainQueuedChimes(8);
        }

        private static bool IsCtrlRDown()
        {
            return (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && Input.GetKeyDown(KeyCode.R);
        }


        private void ReloadSettings(bool forceRestartServer)
        {
            Settings = SettingsStore.LoadOrCreate(_pluginDir, Log, LogWarn, LogError);
            Log(
                $"settings loaded: enabled={Settings.Enabled}, listen={Settings.ListenHost}:{Settings.ListenPort}, " +
                $"endpoint={Settings.EndpointPath}");
            if (forceRestartServer)
            {
                StopServer();
                StartServer();
            }
        }

        private void StartServer()
        {
            if (Settings == null || !Settings.Enabled)
            {
                StopServer();
                return;
            }

            if (_listener != null)
            {
                return;
            }

            try
            {
                IPAddress ip = ParseListenAddress(Settings.ListenHost);
                _listener = new TcpListener(ip, Settings.ListenPort);
                _listener.Start(100);
                _running = true;
                _serverThread = new Thread(ServerLoop)
                {
                    IsBackground = true,
                    Name = "MainGameSubtitleEventBridge.Server"
                };
                _serverThread.Start();
                Log($"[server] listening {ip}:{Settings.ListenPort}");
            }
            catch (Exception ex)
            {
                LogError("[server] start failed: " + ex.Message);
                StopServer();
            }
        }

        private void StopServer()
        {
            _running = false;

            try
            {
                _listener?.Stop();
            }
            catch
            {
            }
            _listener = null;

            if (_serverThread != null)
            {
                try
                {
                    if (!_serverThread.Join(1500))
                    {
                        _serverThread.Interrupt();
                    }
                }
                catch
                {
                }
                _serverThread = null;
            }
        }

        private void ServerLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    HandleClient(client);
                }
                catch (SocketException)
                {
                    if (_running)
                    {
                        LogWarn("[server] socket exception in accept");
                    }
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        LogWarn("[server] loop error: " + ex.Message);
                    }
                }
                finally
                {
                    try
                    {
                        client?.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;

            using (NetworkStream stream = client.GetStream())
            {
                if (!TryReadHttpRequest(stream, Settings.MaxBodyBytes, out HttpRequestData req, out string readError))
                {
                    WriteHttpResponse(stream, 400, "Bad Request", JsonError(readError));
                    return;
                }

                if (!string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    WriteHttpResponse(stream, 405, "Method Not Allowed", JsonError("POST required"));
                    return;
                }

                if (!PathMatches(req.Path, Settings.EndpointPath))
                {
                    WriteHttpResponse(stream, 404, "Not Found", JsonError("endpoint not found"));
                    return;
                }

                if (!ValidateToken(req.Headers, Settings.AuthToken))
                {
                    WriteHttpResponse(stream, 401, "Unauthorized", JsonError("invalid token"));
                    return;
                }

                if (!TryBuildSubtitleRequest(req.Body, out SubtitleRequest subtitleRequest, out SpeakerKind speakerKind, out string reason))
                {
                    WriteHttpResponse(stream, 400, "Bad Request", JsonError(reason));
                    return;
                }

                bool queued = SubtitleApi.TryShow(subtitleRequest, out string apiReason);
                if (!queued)
                {
                    WriteHttpResponse(stream, 503, "Service Unavailable", JsonError(apiReason));
                    LogWarn("[recv] queue failed: " + apiReason);
                    return;
                }

                WriteHttpResponse(stream, 200, "OK", "{\"ok\":true,\"error\":\"\"}");
                EnqueueChime(speakerKind);
                if (Settings.VerboseLog)
                {
                    Log(
                        $"[recv] queued text_len={subtitleRequest.Text?.Length ?? 0}, " +
                        $"speaker={speakerKind}");
                }
            }
        }

        private bool TryBuildSubtitleRequest(
            byte[] bodyBytes,
            out SubtitleRequest request,
            out SpeakerKind speakerKind,
            out string reason)
        {
            request = null;
            speakerKind = SpeakerKind.Unknown;
            reason = "invalid body";

            string textBody = Encoding.UTF8.GetString(bodyBytes ?? Array.Empty<byte>()).Trim();
            if (string.IsNullOrWhiteSpace(textBody))
            {
                reason = "empty body";
                return false;
            }

            SubtitleIncomingRequest incoming = null;
            if (textBody.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    using (var ms = new MemoryStream(bodyBytes))
                    {
                        incoming = IncomingSerializer.ReadObject(ms) as SubtitleIncomingRequest;
                    }
                }
                catch (Exception ex)
                {
                    reason = "json parse failed: " + ex.Message;
                    return false;
                }
            }
            else if (!Settings.AcceptPlainTextBody)
            {
                reason = "plain text body disabled";
                return false;
            }

            string text = incoming != null ? (incoming.Text ?? string.Empty).Trim() : textBody;
            if (string.IsNullOrWhiteSpace(text))
            {
                reason = "text is empty";
                return false;
            }

            float hold = Mathf.Clamp(incoming?.HoldSeconds ?? Settings.DefaultHoldSeconds, 0.1f, 600f);
            string backendText = (incoming?.Backend ?? Settings.DefaultBackend ?? "Auto").Trim();
            string displayMode = (incoming?.DisplayMode ?? Settings.DefaultDisplayMode ?? "Normal").Trim();
            speakerKind = ResolveSpeakerKind(incoming, text, displayMode);

            SubtitleBackend backend = SubtitleBackend.Auto;
            if (!Enum.TryParse(backendText, ignoreCase: true, out backend))
            {
                backend = SubtitleBackend.Auto;
            }

            request = new SubtitleRequest
            {
                Text = text,
                HoldSeconds = hold,
                Backend = backend,
                DisplayMode = displayMode
            };
            reason = null;
            return true;
        }

        private static SpeakerKind ResolveSpeakerKind(SubtitleIncomingRequest incoming, string text, string displayMode)
        {
            if (TryParseSpeakerKindFromHint(incoming?.SpeakerGender, out SpeakerKind byGenderHint))
            {
                return byGenderHint;
            }

            if (TryParseSpeakerKindFromHint(incoming?.Speaker, out SpeakerKind bySpeakerHint))
            {
                return bySpeakerHint;
            }

            if (TryParseSpeakerKindFromHint(displayMode, out SpeakerKind byDisplayModeHint))
            {
                return byDisplayModeHint;
            }

            if (TryParseSpeakerKindFromTextColor(text, out SpeakerKind byColor))
            {
                return byColor;
            }

            return SpeakerKind.Unknown;
        }

        private static bool TryParseSpeakerKindFromHint(string hint, out SpeakerKind result)
        {
            result = SpeakerKind.Unknown;
            string normalized = (hint ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            if (normalized.Contains("female") ||
                normalized.Contains("woman") ||
                normalized.Contains("girl") ||
                normalized.Contains("stackfemale") ||
                normalized.Contains("pink") ||
                normalized.Contains("女"))
            {
                result = SpeakerKind.Female;
                return true;
            }

            if (normalized.Contains("male") ||
                normalized.Contains("man") ||
                normalized.Contains("boy") ||
                normalized.Contains("stackmale") ||
                normalized.Contains("cyan") ||
                normalized.Contains("aqua") ||
                normalized.Contains("水色") ||
                normalized.Contains("男"))
            {
                result = SpeakerKind.Male;
                return true;
            }

            return false;
        }

        private static bool TryParseSpeakerKindFromTextColor(string text, out SpeakerKind result)
        {
            result = SpeakerKind.Unknown;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            MatchCollection matches = ColorTagRegex.Matches(text);
            for (int i = 0; i < matches.Count; i++)
            {
                Match match = matches[i];
                if (match == null || !match.Success)
                {
                    continue;
                }

                Group group = match.Groups["value"];
                if (group == null || !group.Success)
                {
                    continue;
                }

                string token = NormalizeColorToken(group.Value);
                if (IsMaleColorToken(token))
                {
                    result = SpeakerKind.Male;
                    return true;
                }

                if (IsFemaleColorToken(token))
                {
                    result = SpeakerKind.Female;
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeColorToken(string token)
        {
            string value = (token ?? string.Empty).Trim().Trim('"', '\'').ToLowerInvariant();
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (!value.StartsWith("#"))
            {
                return value;
            }

            if (value.Length == 9)
            {
                return value.Substring(0, 7);
            }

            if (value.Length == 4)
            {
                char r = value[1];
                char g = value[2];
                char b = value[3];
                return "#" + r + r + g + g + b + b;
            }

            if (value.Length == 5)
            {
                char r = value[1];
                char g = value[2];
                char b = value[3];
                return "#" + r + r + g + g + b + b;
            }

            return value;
        }

        private static bool IsMaleColorToken(string token)
        {
            return token == "cyan" ||
                token == "aqua" ||
                token == "#00ffff" ||
                token == "#00bfff" ||
                token == "#87cefa";
        }

        private static bool IsFemaleColorToken(string token)
        {
            return token == "pink" ||
                token == "hotpink" ||
                token == "#ffc0cb" ||
                token == "#ff69b4" ||
                token == "#ffb6c1";
        }

        private void EnqueueChime(SpeakerKind speakerKind)
        {
            if (Settings == null || !Settings.EnableChime)
            {
                return;
            }

            lock (_chimeQueueLock)
            {
                if (_chimeQueue.Count >= 128)
                {
                    _chimeQueue.Dequeue();
                }

                _chimeQueue.Enqueue(speakerKind);
            }
        }

        private void DrainQueuedChimes(int maxPerFrame)
        {
            if (Settings == null || !Settings.EnableChime || _audioPlayer == null)
            {
                lock (_chimeQueueLock)
                {
                    if (_chimeQueue.Count > 0)
                    {
                        _chimeQueue.Clear();
                    }
                }

                return;
            }

            for (int i = 0; i < maxPerFrame; i++)
            {
                SpeakerKind speakerKind;
                lock (_chimeQueueLock)
                {
                    if (_chimeQueue.Count <= 0)
                    {
                        return;
                    }

                    speakerKind = _chimeQueue.Dequeue();
                }

                string fileName = ResolveChimeFileName(speakerKind);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                float vol = Settings?.ChimeVolume ?? 0.5f;
                if (!_audioPlayer.PlayOneShot(fileName, vol, out string reason))
                {
                    LogWarn($"[chime] play failed speaker={speakerKind}, file={fileName}, reason={reason}");
                }
                else if (Settings != null && Settings.VerboseLog)
                {
                    Log($"[chime] played speaker={speakerKind}, file={fileName}, volume={vol:0.00}");
                }
            }
        }

        private string ResolveChimeFileName(SpeakerKind speakerKind)
        {
            string male = NormalizeChimeFileName(Settings?.MaleChimeFile);
            string female = NormalizeChimeFileName(Settings?.FemaleChimeFile);

            switch (speakerKind)
            {
                case SpeakerKind.Male:
                    return string.IsNullOrEmpty(male) ? female : male;
                case SpeakerKind.Female:
                    return string.IsNullOrEmpty(female) ? male : female;
                default:
                    return string.IsNullOrEmpty(female) ? male : female;
            }
        }

        private static string NormalizeChimeFileName(string input)
        {
            string name = (input ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            return name;
        }

        private static bool TryReadHttpRequest(
            NetworkStream stream,
            int maxBodyBytes,
            out HttpRequestData request,
            out string error)
        {
            request = null;
            error = null;
            byte[] headerAndMaybeBody = ReadUntilHeaderEnd(stream, 16 * 1024, out int headerEndIndex, out error);
            if (headerAndMaybeBody == null)
            {
                return false;
            }

            string headerText = Encoding.ASCII.GetString(headerAndMaybeBody, 0, headerEndIndex);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
            {
                error = "missing request line";
                return false;
            }

            string[] firstParts = lines[0].Split(' ');
            if (firstParts.Length < 2)
            {
                error = "invalid request line";
                return false;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                int sep = line.IndexOf(':');
                if (sep <= 0)
                {
                    continue;
                }

                string name = line.Substring(0, sep).Trim();
                string value = line.Substring(sep + 1).Trim();
                headers[name] = value;
            }

            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out string clText) && !int.TryParse(clText, out contentLength))
            {
                error = "invalid content-length";
                return false;
            }
            if (contentLength < 0)
            {
                error = "negative content-length";
                return false;
            }
            if (contentLength > maxBodyBytes)
            {
                error = "body too large";
                return false;
            }

            byte[] body = new byte[contentLength];
            int initialBodyOffset = headerEndIndex + 4;
            int preloadedBodyCount = Math.Max(0, headerAndMaybeBody.Length - initialBodyOffset);
            if (preloadedBodyCount > 0)
            {
                int copy = Math.Min(preloadedBodyCount, contentLength);
                Buffer.BlockCopy(headerAndMaybeBody, initialBodyOffset, body, 0, copy);
                preloadedBodyCount = copy;
            }

            int remaining = contentLength - preloadedBodyCount;
            int writeOffset = preloadedBodyCount;
            while (remaining > 0)
            {
                int read = stream.Read(body, writeOffset, remaining);
                if (read <= 0)
                {
                    error = "unexpected EOF";
                    return false;
                }
                writeOffset += read;
                remaining -= read;
            }

            request = new HttpRequestData
            {
                Method = firstParts[0].Trim(),
                Path = firstParts[1].Trim(),
                Headers = headers,
                Body = body
            };
            return true;
        }

        private static byte[] ReadUntilHeaderEnd(
            NetworkStream stream,
            int maxHeaderBytes,
            out int headerEndIndex,
            out string error)
        {
            headerEndIndex = -1;
            error = null;
            byte[] readBuffer = new byte[2048];
            using (var ms = new MemoryStream())
            {
                while (ms.Length < maxHeaderBytes)
                {
                    int read = stream.Read(readBuffer, 0, readBuffer.Length);
                    if (read <= 0)
                    {
                        error = "connection closed";
                        return null;
                    }

                    ms.Write(readBuffer, 0, read);
                    byte[] data = ms.GetBuffer();
                    int len = (int)ms.Length;
                    int idx = IndexOf(data, len, new byte[] { 13, 10, 13, 10 });
                    if (idx >= 0)
                    {
                        headerEndIndex = idx;
                        byte[] result = new byte[len];
                        Buffer.BlockCopy(data, 0, result, 0, len);
                        return result;
                    }
                }
            }

            error = "headers too large";
            return null;
        }

        private static int IndexOf(byte[] haystack, int haystackLen, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || haystackLen < needle.Length)
            {
                return -1;
            }

            int max = haystackLen - needle.Length;
            for (int i = 0; i <= max; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool PathMatches(string rawPath, string expectedPath)
        {
            string path = (rawPath ?? string.Empty).Trim();
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    path = new Uri(path).AbsolutePath;
                }
                catch
                {
                    path = rawPath;
                }
            }

            int q = path.IndexOf('?');
            if (q >= 0)
            {
                path = path.Substring(0, q);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                path = "/";
            }

            string expected = string.IsNullOrWhiteSpace(expectedPath) ? "/" : expectedPath;
            if (!expected.StartsWith("/"))
            {
                expected = "/" + expected;
            }

            return string.Equals(path, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ValidateToken(Dictionary<string, string> headers, string expectedToken)
        {
            string expected = (expectedToken ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(expected))
            {
                return true;
            }

            if (headers == null)
            {
                return false;
            }

            if (!headers.TryGetValue("X-Auth-Token", out string actual))
            {
                return false;
            }

            return string.Equals((actual ?? string.Empty).Trim(), expected, StringComparison.Ordinal);
        }

        private static string JsonError(string error)
        {
            string escaped = (error ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "{\"ok\":false,\"error\":\"" + escaped + "\"}";
        }

        private static void WriteHttpResponse(
            NetworkStream stream,
            int statusCode,
            string reason,
            string jsonBody)
        {
            byte[] body = Encoding.UTF8.GetBytes((jsonBody ?? "{}") + "\n");
            string header =
                $"HTTP/1.1 {statusCode} {reason}\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n" +
                "\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static IPAddress ParseListenAddress(string host)
        {
            string text = (host ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text) || text == "0.0.0.0" || text == "*" || text == "+")
            {
                return IPAddress.Any;
            }

            if (string.Equals(text, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return IPAddress.Loopback;
            }

            if (IPAddress.TryParse(text, out IPAddress ip))
            {
                return ip;
            }

            return IPAddress.Any;
        }

        private static void Log(string message)
        {
            Logger?.LogInfo(message);
            WriteFile("INFO", message);
        }

        private static void LogWarn(string message)
        {
            Logger?.LogWarning(message);
            WriteFile("WARN", message);
        }

        private static void LogError(string message)
        {
            Logger?.LogError(message);
            WriteFile("ERROR", message);
        }

        private static void WriteFile(string level, string message)
        {
            try
            {
                lock (FileLogLock)
                {
                    string path = _logFilePath;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        string dir = Path.Combine(Path.GetDirectoryName(Paths.PluginPath), PluginName);
                        Directory.CreateDirectory(dir);
                        path = Path.Combine(dir, PluginName + ".log");
                    }

                    File.AppendAllText(
                        path,
                        $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}",
                        Utf8NoBom);
                }
            }
            catch
            {
            }
        }
    }
}

