using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace MainGameSubtitleEventBridge
{
    internal sealed class NotificationAudioPlayer : IDisposable
    {
        private readonly string _waveDir;
        private readonly Action<string> _logWarn;
        private readonly Action<string> _logError;
        private readonly Dictionary<string, AudioClip> _clipCache = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] PreferredExtensions = { ".wav", ".mp3", ".ogg" };

        private GameObject _hostObject;
        private AudioSource _audioSource;

        internal NotificationAudioPlayer(
            string pluginDir,
            string waveDirName,
            Action<string> logWarn,
            Action<string> logError)
        {
            _logWarn = logWarn;
            _logError = logError;

            string baseDir = pluginDir ?? string.Empty;
            string folderName = string.IsNullOrWhiteSpace(waveDirName) ? "wave" : waveDirName.Trim();
            _waveDir = Path.Combine(baseDir, folderName);

            try
            {
                Directory.CreateDirectory(_waveDir);
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[chime] failed to create wave dir: " + ex.Message);
            }
        }

        internal bool PlayOneShot(string fileName, float volume, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                reason = "empty file name";
                return false;
            }

            EnsureAudioSource();
            if (_audioSource == null)
            {
                reason = "audio source unavailable";
                return false;
            }

            if (!TryResolveFilePath(fileName, out string fullPath, out reason))
            {
                return false;
            }

            if (!_clipCache.TryGetValue(fullPath, out AudioClip clip) || clip == null)
            {
                if (!TryLoadClip(fullPath, out clip, out reason))
                {
                    return false;
                }

                _clipCache[fullPath] = clip;
            }

            try
            {
                _audioSource.PlayOneShot(clip, Mathf.Clamp01(volume));
                return true;
            }
            catch (Exception ex)
            {
                reason = "play failed: " + ex.Message;
                return false;
            }
        }

        private bool TryLoadClip(string fullPath, out AudioClip clip, out string reason)
        {
            clip = null;
            reason = string.Empty;

            string extension = Path.GetExtension(fullPath)?.ToLowerInvariant() ?? string.Empty;
            if (extension == ".wav")
            {
                return WavFileLoader.TryLoadClip(fullPath, out clip, out reason);
            }

            if (extension == ".mp3" || extension == ".ogg")
            {
                return TryLoadCompressedClipBlocking(fullPath, out clip, out reason);
            }

            reason = "unsupported extension: " + extension;
            return false;
        }

        private static bool TryLoadCompressedClipBlocking(string fullPath, out AudioClip clip, out string reason)
        {
            clip = null;
            reason = string.Empty;

            AudioType audioType = ResolveAudioType(fullPath);
            string uri = ToFileUri(fullPath);
            using (var req = UnityWebRequestMultimedia.GetAudioClip(uri, audioType))
            {
                var op = req.SendWebRequest();
                int spinGuard = 0;
                while (!op.isDone)
                {
                    Thread.Sleep(1);
                    spinGuard++;
                    if (spinGuard > 10000)
                    {
                        reason = "audio load timeout";
                        return false;
                    }
                }

#if UNITY_2020_2_OR_NEWER
                if (req.result != UnityWebRequest.Result.Success)
                {
                    reason = "audio load failed: " + req.error;
                    return false;
                }
#else
                if (req.isNetworkError || req.isHttpError)
                {
                    reason = "audio load failed: " + req.error;
                    return false;
                }
#endif

                clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null)
                {
                    reason = "audio clip is null";
                    return false;
                }

                clip.name = Path.GetFileNameWithoutExtension(fullPath);
                return true;
            }
        }

        private static AudioType ResolveAudioType(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
            if (ext == ".mp3")
            {
                return AudioType.MPEG;
            }

            if (ext == ".ogg")
            {
                return AudioType.OGGVORBIS;
            }

            if (ext == ".wav")
            {
                return AudioType.WAV;
            }

            return AudioType.UNKNOWN;
        }

        private static string ToFileUri(string path)
        {
            string full = Path.GetFullPath(path ?? string.Empty);
            string normalized = full.Replace('\\', '/');
            return "file:///" + normalized;
        }

        public void Dispose()
        {
            foreach (var kv in _clipCache)
            {
                AudioClip clip = kv.Value;
                if (clip != null)
                {
                    UnityEngine.Object.Destroy(clip);
                }
            }

            _clipCache.Clear();

            if (_hostObject != null)
            {
                UnityEngine.Object.Destroy(_hostObject);
                _hostObject = null;
                _audioSource = null;
            }
        }

        private void EnsureAudioSource()
        {
            if (_audioSource != null)
            {
                return;
            }

            try
            {
                _hostObject = new GameObject("MainGameSubtitleEventBridge.ChimeAudio");
                _hostObject.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_hostObject);

                _audioSource = _hostObject.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
                _audioSource.loop = false;
                _audioSource.spatialBlend = 0f;
                _audioSource.volume = 1f;
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[chime] create AudioSource failed: " + ex.Message);
                _hostObject = null;
                _audioSource = null;
            }
        }

        private bool TryResolveFilePath(string fileName, out string fullPath, out string reason)
        {
            fullPath = string.Empty;
            reason = string.Empty;

            try
            {
                string candidate = (fileName ?? string.Empty).Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    reason = "empty file name";
                    return false;
                }
                bool rooted = Path.IsPathRooted(candidate);
                string basePath = rooted ? candidate : Path.Combine(_waveDir, candidate);
                string normalizedBase = Path.GetFullPath(basePath);
                if (File.Exists(normalizedBase))
                {
                    fullPath = normalizedBase;
                    return true;
                }

                string ext = Path.GetExtension(normalizedBase);
                var tried = new List<string>();
                if (string.IsNullOrWhiteSpace(ext))
                {
                    for (int i = 0; i < PreferredExtensions.Length; i++)
                    {
                        string probe = normalizedBase + PreferredExtensions[i];
                        tried.Add(probe);
                        if (File.Exists(probe))
                        {
                            fullPath = probe;
                            return true;
                        }
                    }
                }
                else
                {
                    string baseNoExt = normalizedBase.Substring(0, normalizedBase.Length - ext.Length);
                    for (int i = 0; i < PreferredExtensions.Length; i++)
                    {
                        string probe = baseNoExt + PreferredExtensions[i];
                        if (string.Equals(probe, normalizedBase, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        tried.Add(probe);
                        if (File.Exists(probe))
                        {
                            fullPath = probe;
                            return true;
                        }
                    }
                }

                reason = "file not found: " + normalizedBase;
                if (tried.Count > 0)
                {
                    reason += " (tried: " + string.Join(", ", tried.ToArray()) + ")";
                }

                return false;
            }
            catch (Exception ex)
            {
                reason = "path resolve failed: " + ex.Message;
                _logWarn?.Invoke("[chime] " + reason);
                return false;
            }
        }
    }
}
