using System;
using System.IO;
using UnityEngine;

namespace MainGameVoiceFaceEventBridge
{
    internal sealed class ExternalVoicePlayer : IDisposable
    {
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarn;
        private readonly Action<string> _logError;

        private GameObject _host;
        private AudioSource _source;
        private AudioClip _currentClip;
        private string _currentPath;
        private bool _deleteAfterPlayback;
        private ChaControl _lipSyncBoundFemale;
        private bool _hasMouthFixedSnapshot;
        private bool _mouthFixedBeforeExternalPlay;

        internal bool IsPlaying
        {
            get
            {
                return (_source != null && _source.isPlaying) || _currentClip != null;
            }
        }

        internal ExternalVoicePlayer(Action<string> logInfo, Action<string> logWarn, Action<string> logError)
        {
            _logInfo = logInfo;
            _logWarn = logWarn;
            _logError = logError;
        }

        internal bool Play(
            string absolutePath,
            ChaControl female,
            bool interruptCurrent,
            bool deleteAfterPlay,
            float volume,
            float pitch)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return false;
            }

            EnsureSource();

            if (_source.isPlaying)
            {
                if (!interruptCurrent)
                {
                    return false;
                }

                StopInternal("interrupt", writeLog: true);
            }

            if (!WavFileLoader.TryLoadClip(absolutePath, out var clip, out var error))
            {
                _logWarn?.Invoke("[audio] load failed: " + error + " path=" + absolutePath);
                return false;
            }

            ReleaseCurrentClip(deleteFile: true);

            _currentClip = clip;
            _currentPath = absolutePath;
            _deleteAfterPlayback = deleteAfterPlay;

            _source.clip = clip;
            _source.volume = Mathf.Clamp01(volume);
            _source.pitch = Mathf.Clamp(pitch, 0.1f, 3f);
            _source.loop = false;
            _source.spatialBlend = 0f;
            _source.Play();

            TryBindLipSync(female);
            _logInfo?.Invoke("[audio] play path=" + absolutePath);
            return true;
        }

        internal void Stop(string reason)
        {
            StopInternal(reason, writeLog: true);
        }

        internal void Update()
        {
            if (_source == null || _currentClip == null)
            {
                return;
            }

            if (_source.isPlaying)
            {
                RefreshLipSyncBinding();
                return;
            }

            ReleaseCurrentClip(deleteFile: true);
            _logInfo?.Invoke("[audio] completed");
        }

        private void StopInternal(string reason, bool writeLog)
        {
            bool hadSomething = (_source != null && _source.isPlaying) || _currentClip != null;
            if (_source != null)
            {
                _source.Stop();
            }

            ReleaseCurrentClip(deleteFile: true);

            if (hadSomething && writeLog)
            {
                _logInfo?.Invoke("[audio] stop reason=" + reason);
            }
        }

        private void TryBindLipSync(ChaControl female)
        {
            if (female == null || _source == null)
            {
                return;
            }

            try
            {
                female.SetLipSync(_source);
                _lipSyncBoundFemale = female;
                TryDisableMouthFixedDuringExternalPlay(female);
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("[audio] SetLipSync failed: " + ex.Message);
            }
        }

        private void RefreshLipSyncBinding()
        {
            if (_lipSyncBoundFemale == null || _source == null || !_source.isPlaying)
            {
                return;
            }

            try
            {
                _lipSyncBoundFemale.SetLipSync(_source);
                TryDisableMouthFixedDuringExternalPlay(_lipSyncBoundFemale);
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("[audio] refresh lip sync failed: " + ex.Message);
            }
        }

        private void TryDisableMouthFixedDuringExternalPlay(ChaControl female)
        {
            if (female == null)
            {
                return;
            }

            try
            {
                bool isFixed = female.GetMouthFixed();
                if (!_hasMouthFixedSnapshot)
                {
                    _hasMouthFixedSnapshot = true;
                    _mouthFixedBeforeExternalPlay = isFixed;
                }

                if (isFixed)
                {
                    female.ChangeMouthFixed(false);
                }
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("[audio] mouth-fixed disable failed: " + ex.Message);
            }
        }

        private void TryRestoreMouthFixedAfterExternalPlay(ChaControl female)
        {
            if (!_hasMouthFixedSnapshot || female == null)
            {
                _hasMouthFixedSnapshot = false;
                _mouthFixedBeforeExternalPlay = false;
                return;
            }

            try
            {
                female.ChangeMouthFixed(_mouthFixedBeforeExternalPlay);
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("[audio] mouth-fixed restore failed: " + ex.Message);
            }
            finally
            {
                _hasMouthFixedSnapshot = false;
                _mouthFixedBeforeExternalPlay = false;
            }
        }

        private void TryClearLipSyncBinding()
        {
            if (_lipSyncBoundFemale == null)
            {
                _hasMouthFixedSnapshot = false;
                _mouthFixedBeforeExternalPlay = false;
                return;
            }

            ChaControl female = _lipSyncBoundFemale;
            try
            {
                female.SetLipSync(null);
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("[audio] clear lip sync failed: " + ex.Message);
            }
            finally
            {
                TryRestoreMouthFixedAfterExternalPlay(female);
                _lipSyncBoundFemale = null;
            }
        }

        private void EnsureSource()
        {
            if (_source != null)
            {
                return;
            }

            _host = new GameObject("MainGameVoiceFaceEventBridge.ExternalVoicePlayer");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _source = _host.AddComponent<AudioSource>();
            _source.playOnAwake = false;
        }

        private void ReleaseCurrentClip(bool deleteFile)
        {
            string finishedPath = _currentPath;
            bool shouldDelete = deleteFile && _deleteAfterPlayback;
            TryClearLipSyncBinding();

            if (_source != null)
            {
                _source.clip = null;
            }

            if (_currentClip != null)
            {
                UnityEngine.Object.Destroy(_currentClip);
                _currentClip = null;
            }

            _currentPath = null;
            _deleteAfterPlayback = false;

            if (shouldDelete && !string.IsNullOrEmpty(finishedPath))
            {
                TryDeleteAudioFile(finishedPath);
            }
        }

        private void TryDeleteAudioFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logInfo?.Invoke("[audio] deleted file: " + path);
                }
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[audio] delete failed: " + ex.Message + " path=" + path);
            }
        }

        public void Dispose()
        {
            StopInternal("dispose", writeLog: false);

            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
                _host = null;
            }

            _source = null;
        }
    }
}
