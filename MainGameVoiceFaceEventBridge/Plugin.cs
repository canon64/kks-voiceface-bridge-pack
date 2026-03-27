using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace MainGameVoiceFaceEventBridge
{
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("KoikatsuSunshine")]
    internal sealed class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.kks.maingame.voicefaceeventbridge";
        public const string PluginName = "MainGameVoiceFaceEventBridge";
        public const string Version = "0.3.1";

        internal static Plugin Instance { get; private set; }
        internal static new ManualLogSource Logger { get; private set; }
        internal static string PluginDir { get; private set; }
        internal static string LogFilePath { get; private set; }
        internal static PluginSettings Settings { get; private set; }
        internal static HSceneProc CurrentProc { get; private set; }

        private static readonly object FileLogLock = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly FieldInfo LstFemaleField = AccessTools.Field(typeof(HSceneProc), "lstFemale");
        private static readonly FieldInfo FaceField = AccessTools.Field(typeof(HSceneProc), "face");
        private static readonly FieldInfo Face1Field = AccessTools.Field(typeof(HSceneProc), "face1");
        private static readonly FieldInfo LstUseAnimInfoField = AccessTools.Field(typeof(HSceneProc), "lstUseAnimInfo");

        private readonly Queue<ExternalVoiceFaceCommand> _commandQueue = new Queue<ExternalVoiceFaceCommand>();
        private readonly object _queueLock = new object();
        private readonly System.Random _random = new System.Random();
        private readonly Dictionary<string, float> _blockLogCooldownByKey = new Dictionary<string, float>();

        private readonly List<Tuple<float, Action>> _delayedActions = new List<Tuple<float, Action>>();

        private Harmony _harmony;
        private ExternalPipeServer _pipeServer;
        private ExternalVoicePlayer _externalVoicePlayer;
        private float _nextProcProbeTime;
        private float _blockGameVoiceUntil;
        private bool _voiceProcStopOverridden;
        private bool _voiceProcStopOriginal;

        private ConfigEntry<bool> _cfgEnabled;
        private ConfigEntry<KeyboardShortcut> _cfgReloadKey;
        private ConfigEntry<float> _cfgPlaybackVolume;
        private ConfigEntry<float> _cfgFemalePlaybackVolume;
        private ConfigEntry<float> _cfgExternalPlaybackPitch;

        // ClothesDetection
        private ConfigEntry<string> _cfgTopKeywords;
        private ConfigEntry<string> _cfgBottomKeywords;
        private ConfigEntry<string> _cfgBraKeywords;
        private ConfigEntry<string> _cfgShortsKeywords;
        private ConfigEntry<string> _cfgGlovesKeywords;
        private ConfigEntry<string> _cfgPanthoseKeywords;
        private ConfigEntry<string> _cfgSocksKeywords;
        private ConfigEntry<string> _cfgShoesKeywords;
        private ConfigEntry<string> _cfgRemoveKeywords;
        private ConfigEntry<string> _cfgShiftKeywords;
        private ConfigEntry<string> _cfgPutOnKeywords;
        private ConfigEntry<string> _cfgRemoveAllKeywords;
        private ConfigEntry<string> _cfgPutOnAllKeywords;
        private ConfigEntry<string> _cfgCoordPattern;

        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;
            PluginDir = Path.GetDirectoryName(Info.Location);
            LogFilePath = Path.Combine(PluginDir, PluginName + ".log");

            Directory.CreateDirectory(PluginDir);
            File.WriteAllText(
                LogFilePath,
                $"[{DateTime.Now:HH:mm:ss}] === {PluginName} {Version} started ==={Environment.NewLine}",
                Utf8NoBom);

            BindConfigEntries();
            Settings = SettingsStore.LoadOrCreate(PluginDir, Log, LogWarn, LogError);
            ApplyConfigEntryOverrides();
            _externalVoicePlayer = new ExternalVoicePlayer(Log, LogWarn, LogError);
            LogGuardSettings("awake");

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(typeof(Plugin));
            VoiceGuardHooks.Apply(_harmony, Log, LogWarn, LogError);

            StartOrRestartPipeServer(forceRestart: true);
            Log("awake complete");
        }

        private void OnDestroy()
        {
            StopPipeServer();

            try
            {
                _harmony?.UnpatchSelf();
                _harmony = null;
            }
            catch (Exception ex)
            {
                LogWarn("unpatch failed: " + ex.Message);
            }

            if (_externalVoicePlayer != null)
            {
                _externalVoicePlayer.Dispose();
                _externalVoicePlayer = null;
            }

            CurrentProc = null;
        }

        private void Update()
        {
            if (Settings != null && Settings.EnableCtrlRReload && IsReloadKeyDown())
            {
                ReloadSettings();
            }

            bool wasExternalPlaying = _externalVoicePlayer != null && _externalVoicePlayer.IsPlaying;
            _externalVoicePlayer?.Update();
            bool isExternalPlaying = _externalVoicePlayer != null && _externalVoicePlayer.IsPlaying;
            if (wasExternalPlaying && !isExternalPlaying)
            {
                RestoreVoiceProcStopIfNeeded();
            }

            if (!ShouldBlockGameVoiceEvents())
            {
                RestoreVoiceProcStopIfNeeded();
            }

            if (Settings == null || !Settings.Enabled)
            {
                return;
            }

            ProcessDelayedActions();
            DrainIncomingCommands(8);
        }

        private bool IsReloadKeyDown()
        {
            KeyboardShortcut reloadKey = ResolveReloadKey();
            return reloadKey.IsDown();
        }

        private void BindConfigEntries()
        {
            _cfgEnabled = Config.Bind(
                "General",
                "Enabled",
                true,
                "プラグイン機能を有効化する。");

            _cfgReloadKey = Config.Bind(
                "Input",
                "ReloadKey",
                new KeyboardShortcut(KeyCode.R, KeyCode.LeftControl),
                "設定再読込キー。");

            _cfgPlaybackVolume = Config.Bind(
                "Audio",
                "PlaybackVolume",
                1.0f,
                new ConfigDescription(
                    "外部音声の基本音量（0.0 - 1.0）。",
                    new AcceptableValueRange<float>(0f, 1f)));

            _cfgFemalePlaybackVolume = Config.Bind(
                "Audio",
                "FemalePlaybackVolume",
                -1.0f,
                new ConfigDescription(
                    "女の子読み上げ時の音量（-1でPlaybackVolumeを使用）。",
                    new AcceptableValueRange<float>(-1f, 1f)));

            _cfgExternalPlaybackPitch = Config.Bind(
                "Audio",
                "ExternalPlaybackPitch",
                1.0f,
                new ConfigDescription(
                    "外部音声再生ピッチ（0.1 - 3.0）。",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            _cfgEnabled.SettingChanged += (_, __) =>
            {
                ApplyConfigEntryOverrides();
                StartOrRestartPipeServer(forceRestart: false);
            };
            _cfgPlaybackVolume.SettingChanged += (_, __) => ApplyConfigEntryOverrides();
            _cfgFemalePlaybackVolume.SettingChanged += (_, __) => ApplyConfigEntryOverrides();
            _cfgExternalPlaybackPitch.SettingChanged += (_, __) => ApplyConfigEntryOverrides();

            const string cdSec = "ClothesDetection";
            _cfgTopKeywords      = Config.Bind(cdSec, "TopKeywords",       "上着,ジャケット,トップス",                          "トップス系ワード（カンマ区切り）");
            _cfgBottomKeywords   = Config.Bind(cdSec, "BottomKeywords",    "スカート,ホットパンツ,ミニスカ,ボトムス,ズボン,パンツ", "ボトムス系ワード");
            _cfgBraKeywords      = Config.Bind(cdSec, "BraKeywords",       "ブラ",                                               "ブラ系ワード");
            _cfgShortsKeywords   = Config.Bind(cdSec, "ShortsKeywords",    "パンティー,パンティ",                                 "パンティ系ワード");
            _cfgGlovesKeywords   = Config.Bind(cdSec, "GlovesKeywords",    "グローブ,手袋",                                      "手袋系ワード");
            _cfgPanthoseKeywords = Config.Bind(cdSec, "PanthoseKeywords",  "ガーターベルト,パンスト,ガーター",                    "パンスト系ワード");
            _cfgSocksKeywords    = Config.Bind(cdSec, "SocksKeywords",     "ストッキング,ニーハイ,靴下",                          "靴下系ワード");
            _cfgShoesKeywords    = Config.Bind(cdSec, "ShoesKeywords",     "ハイヒール,スニーカー,サンダル,ヒール,靴",            "靴系ワード");
            _cfgRemoveKeywords   = Config.Bind(cdSec, "RemoveKeywords",    "脱ぐね,脱いじゃう",                                  "脱衣トリガーワード");
            _cfgShiftKeywords    = Config.Bind(cdSec, "ShiftKeywords",     "ずらすね,半脱ぎにするね",                             "ずらしトリガーワード");
            _cfgPutOnKeywords    = Config.Bind(cdSec, "PutOnKeywords",     "着るね,付けるね",                                    "着用トリガーワード");
            _cfgRemoveAllKeywords= Config.Bind(cdSec, "RemoveAllKeywords", "全裸になるね,全部脱ぐね,全部脱いじゃう",              "全脱ぎトリガーワード");
            _cfgPutOnAllKeywords = Config.Bind(cdSec, "PutOnAllKeywords",  "全部着るね",                                         "全着用トリガーワード");
            _cfgCoordPattern     = Config.Bind(cdSec, "CoordPattern",      "に着替えるね",                                       "着替えトリガーパターン");
        }

        private void ApplyConfigEntryOverrides()
        {
            if (Settings == null)
            {
                return;
            }

            Settings.Enabled = _cfgEnabled != null ? _cfgEnabled.Value : Settings.Enabled;
            Settings.PlaybackVolume = _cfgPlaybackVolume != null ? Mathf.Clamp01(_cfgPlaybackVolume.Value) : Settings.PlaybackVolume;
            Settings.FemalePlaybackVolume = _cfgFemalePlaybackVolume != null
                ? Mathf.Clamp(_cfgFemalePlaybackVolume.Value, -1f, 1f)
                : Settings.FemalePlaybackVolume;
            Settings.ExternalPlaybackPitch = _cfgExternalPlaybackPitch != null
                ? Mathf.Clamp(_cfgExternalPlaybackPitch.Value, 0.1f, 3f)
                : Settings.ExternalPlaybackPitch;
        }

        private KeyboardShortcut ResolveReloadKey()
        {
            if (_cfgReloadKey != null)
            {
                return _cfgReloadKey.Value;
            }

            return new KeyboardShortcut(KeyCode.R, KeyCode.LeftControl);
        }

        private void ReloadSettings()
        {
            string oldPipe = Settings?.PipeName ?? string.Empty;
            Settings = SettingsStore.LoadOrCreate(PluginDir, Log, LogWarn, LogError);
            ApplyConfigEntryOverrides();
            LogGuardSettings("reload");

            bool pipeChanged = !string.Equals(
                oldPipe,
                Settings?.PipeName ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            StartOrRestartPipeServer(forceRestart: pipeChanged);
            Log("settings reloaded by Ctrl+R");
        }

        private void StartOrRestartPipeServer(bool forceRestart)
        {
            PluginSettings s = Settings;
            if (s == null || !s.Enabled || !s.EnablePipeServer)
            {
                StopPipeServer();
                return;
            }

            string pipeName = CommandParser.NormalizePipeName(s.PipeName);
            if (_pipeServer != null && !forceRestart && _pipeServer.IsForPipe(pipeName))
            {
                return;
            }

            StopPipeServer();

            _pipeServer = new ExternalPipeServer(
                pipeName,
                OnPipeLineReceived,
                Log,
                LogWarn,
                LogError);

            _pipeServer.Start();
            Log("[pipe] listening name=" + pipeName);
        }

        private void StopPipeServer()
        {
            if (_pipeServer == null)
            {
                return;
            }

            _pipeServer.Stop();
            _pipeServer = null;
        }

        private void OnPipeLineReceived(string line)
        {
            if (!CommandParser.TryParseIncoming(line, Settings, out var command, out var reason))
            {
                if (Settings != null && Settings.VerboseLog)
                {
                    LogWarn("[pipe] ignore command: " + reason);
                }
                return;
            }

            EnqueueCommand(command);
        }

        private void EnqueueCommand(ExternalVoiceFaceCommand command)
        {
            int capacity = Math.Max(1, Settings?.MaxQueuedCommands ?? 1);
            lock (_queueLock)
            {
                while (_commandQueue.Count >= capacity)
                {
                    _commandQueue.Dequeue();
                    if (Settings != null && Settings.VerboseLog)
                    {
                        LogWarn("[pipe] queue overflow, oldest command dropped");
                    }
                }

                _commandQueue.Enqueue(command);
            }
        }

        private void DrainIncomingCommands(int maxPerFrame)
        {
            for (int i = 0; i < maxPerFrame; i++)
            {
                ExternalVoiceFaceCommand command;
                lock (_queueLock)
                {
                    if (_commandQueue.Count <= 0)
                    {
                        return;
                    }

                    command = _commandQueue.Dequeue();
                }

                HandleCommand(command);
            }
        }

        private void HandleCommand(ExternalVoiceFaceCommand command)
        {
            var settings = Settings;
            if (command == null || settings == null)
            {
                return;
            }

            if (command.IsStop())
            {
                _externalVoicePlayer?.Stop("external stop");
                _blockGameVoiceUntil = 0f;
                RestoreVoiceProcStopIfNeeded();
                return;
            }

            if (string.Equals(command.type, "coord", StringComparison.OrdinalIgnoreCase))
            {
                HandleCoordCommand(command);
                return;
            }

            if (string.Equals(command.type, "clothes", StringComparison.OrdinalIgnoreCase))
            {
                HandleClothesCommand(command);
                return;
            }

            if (string.Equals(command.type, "response_text", StringComparison.OrdinalIgnoreCase))
            {
                HandleResponseTextCommand(command);
                return;
            }

            if (string.Equals(command.type, "pose", StringComparison.OrdinalIgnoreCase))
            {
                HandlePoseCommand(command);
                return;
            }

            HSceneProc proc = FindCurrentProc();
            if (proc == null)
            {
                if (settings.IgnoreCommandOutsideHScene)
                {
                    if (settings.VerboseLog)
                    {
                        LogWarn("[cmd] dropped because H scene is inactive");
                    }
                    return;
                }

                LogWarn("[cmd] HSceneProc not found");
                return;
            }

            int requestedMain = command.ResolveMain(settings.TargetMainIndex);
            int main = ClampMainIndex(proc, requestedMain);
            ChaControl female = ResolveFemale(proc, main);
            if (female == null)
            {
                LogWarn("[cmd] female not found main=" + main);
                return;
            }

            bool keepCurrentFaceMode;
            int face = ResolveFace(command, settings, out keepCurrentFaceMode);
            int voiceKind = command.ResolveVoiceKind(settings.DefaultVoiceKind);
            int action = command.ResolveAction(settings.DefaultAction);
            string audioPath = NormalizeAudioPath(command.ResolveAudioPath());
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                BeginExternalVoiceGuard(proc, female, main);

                if (!keepCurrentFaceMode)
                {
                    TryApplyFace(proc, main, female, face, voiceKind, action);
                }

                bool interruptCurrent = command.ResolveInterrupt(settings.DefaultInterruptCurrent);
                bool deleteAfterPlay = command.ResolveDeleteAfterPlay(settings.DeleteAudioAfterPlayback);
                float defaultVolume = ResolveExternalAudioDefaultVolume(settings);
                float volume = command.ResolveVolume(defaultVolume);
                float playbackPitch = command.ResolvePitch(settings.ExternalPlaybackPitch);

                bool playedAudio = _externalVoicePlayer != null && _externalVoicePlayer.Play(
                    audioPath,
                    female,
                    interruptCurrent,
                    deleteAfterPlay,
                    volume,
                    playbackPitch);

                if (playedAudio)
                {
                    Log(
                        "[cmd] played audio"
                        + " main=" + main
                        + " face=" + face
                        + " keepCurrentFace=" + keepCurrentFaceMode
                        + " voiceKind=" + voiceKind
                        + " action=" + action
                        + " volume=" + volume
                        + " pitch=" + playbackPitch
                        + " audioPath=" + audioPath);
                }
                else
                {
                    LogWarn("[cmd] audio play failed path=" + audioPath);
                    _blockGameVoiceUntil = 0f;
                    RestoreVoiceProcStopIfNeeded();
                }

                return;
            }

            if (proc.voice == null)
            {
                LogWarn("[cmd] HVoiceCtrl is not ready");
                return;
            }

            int voiceNo = ResolveVoiceNo(proc, main);
            if (voiceNo < 0)
            {
                LogWarn("[cmd] voiceNo not found main=" + main);
                return;
            }

            string assetBundle = command.ResolveAssetBundle(settings.DefaultAssetBundle);
            string assetName = command.ResolveAssetName(settings.DefaultAssetName);
            if (string.IsNullOrWhiteSpace(assetBundle) || string.IsNullOrWhiteSpace(assetName))
            {
                LogWarn("[cmd] assetBundle or assetName is empty");
                return;
            }

            int eyeNeck = command.ResolveEyeNeck(settings.DefaultEyeNeck);
            float pitch = command.ResolvePitch(settings.DefaultPitch);
            float fadeTime = command.ResolveFadeTime(settings.DefaultFadeTime);

            var voiceSetting = new Illusion.Game.Utils.Voice.Setting
            {
                assetBundleName = assetBundle,
                assetName = assetName,
                no = voiceNo,
                pitch = pitch,
                fadeTime = fadeTime,
                voiceTrans = female.transform,
                settingNo = -1,
                isAsync = true,
                isPlayEndDelete = true
            };

            bool result = proc.voice.PlayVoice(
                female,
                voiceSetting,
                face,
                eyeNeck,
                voiceKind,
                action,
                main);

            if (result)
            {
                Log(
                    "[cmd] played"
                    + " main=" + main
                    + " voiceNo=" + voiceNo
                    + " face=" + face
                    + " keepCurrentFace=" + keepCurrentFaceMode
                    + " eyeneck=" + eyeNeck
                    + " voiceKind=" + voiceKind
                    + " action=" + action
                    + " asset=" + assetName);
            }
            else
            {
                LogWarn("[cmd] PlayVoice returned false asset=" + assetName);
            }
        }

        internal bool ShouldBlockGameVoiceEvents()
        {
            var s = Settings;
            if (s == null || !s.Enabled || !s.BlockGameVoiceWhileExternalPlaying)
            {
                return false;
            }

            if (_externalVoicePlayer != null && _externalVoicePlayer.IsPlaying)
            {
                return true;
            }

            return Time.unscaledTime < _blockGameVoiceUntil;
        }

        internal void OnGameVoiceEventBlocked(string point)
        {
            var s = Settings;
            if (s == null || !s.VerboseLog || string.IsNullOrWhiteSpace(point))
            {
                return;
            }

            float now = Time.unscaledTime;
            if (_blockLogCooldownByKey.TryGetValue(point, out var nextAllowed) && now < nextAllowed)
            {
                return;
            }

            _blockLogCooldownByKey[point] = now + 1f;
            Log("[block] " + point);
        }

        private void BeginExternalVoiceGuard(HSceneProc proc, ChaControl female, int main)
        {
            var s = Settings;
            if (s != null)
            {
                float until = Time.unscaledTime + s.ExternalPlayPreBlockSeconds;
                if (until > _blockGameVoiceUntil)
                {
                    _blockGameVoiceUntil = until;
                }
            }

            if (s == null || !s.StopGameVoiceBeforeExternalPlay)
            {
                return;
            }

            TryStopGameVoice(proc, female, main);
        }

        private void TryStopGameVoice(HSceneProc proc, ChaControl female, int main)
        {
            try
            {
                int targets = 0;
                int stoppedTargets = 0;
                int residualBefore = 0;
                int residualAfter = 0;

                if (proc != null && proc.flags != null && proc.flags.transVoiceMouth != null)
                {
                    for (int i = 0; i < proc.flags.transVoiceMouth.Length; i++)
                    {
                        Transform voiceTrans = proc.flags.transVoiceMouth[i];
                        if (voiceTrans == null)
                        {
                            continue;
                        }

                        targets++;
                        bool wasPlaying = Manager.Voice.IsPlay(voiceTrans);
                        if (wasPlaying)
                        {
                            residualBefore++;
                        }

                        int voiceNo = -1;
                        if (proc.flags.lstHeroine != null && i >= 0 && i < proc.flags.lstHeroine.Count && proc.flags.lstHeroine[i] != null)
                        {
                            voiceNo = proc.flags.lstHeroine[i].voiceNo;
                        }

                        if (voiceNo >= 0)
                        {
                            Manager.Voice.Stop(voiceNo, voiceTrans);
                        }

                        Manager.Voice.Stop(voiceTrans);

                        if (Manager.Voice.IsPlay(voiceTrans))
                        {
                            if (voiceNo >= 0)
                            {
                                Manager.Voice.Stop(voiceNo, voiceTrans);
                            }
                            Manager.Voice.Stop(voiceTrans);
                        }

                        if (!Manager.Voice.IsPlay(voiceTrans))
                        {
                            stoppedTargets++;
                        }
                        else
                        {
                            residualAfter++;
                        }
                    }
                }

                if (female != null)
                {
                    Manager.Voice.Stop(female.transform);
                    ClearLipSyncSource(female);
                }

                if (proc != null && proc.voice != null)
                {
                    if (!_voiceProcStopOverridden)
                    {
                        _voiceProcStopOriginal = proc.voice.isVoicePrcoStop;
                        _voiceProcStopOverridden = true;
                    }

                    proc.voice.isVoicePrcoStop = true;
                }

                if (residualAfter > 0)
                {
                    Manager.Voice.StopAll(false);
                }

                bool anyPlayingAfter = Manager.Voice.IsPlay();
                Log(
                    "[guard] force-stop game voice before external play"
                    + " main=" + main
                    + " targets=" + targets
                    + " residualBefore=" + residualBefore
                    + " stoppedTargets=" + stoppedTargets
                    + " residualAfter=" + residualAfter
                    + " anyPlayingAfter=" + anyPlayingAfter);
            }
            catch (Exception ex)
            {
                LogWarn("[guard] stop game voice failed: " + ex.Message);
            }
        }

        private static void ClearLipSyncSource(ChaControl female)
        {
            if (female == null)
            {
                return;
            }

            try
            {
                female.SetLipSync(null);
            }
            catch
            {
            }
        }

        private static float ResolveExternalAudioDefaultVolume(PluginSettings settings)
        {
            if (settings == null)
            {
                return 1f;
            }

            if (settings.FemalePlaybackVolume >= 0f)
            {
                return Mathf.Clamp01(settings.FemalePlaybackVolume);
            }

            return Mathf.Clamp01(settings.PlaybackVolume);
        }

        private void LogGuardSettings(string reason)
        {
            var s = Settings;
            if (s == null)
            {
                return;
            }

            Log(
                "[guard] settings"
                + " reason=" + reason
                + " stopBefore=" + s.StopGameVoiceBeforeExternalPlay
                + " blockWhilePlay=" + s.BlockGameVoiceWhileExternalPlaying
                + " preBlockSec=" + s.ExternalPlayPreBlockSeconds
                + " vol=" + s.PlaybackVolume
                + " femaleVol=" + s.FemalePlaybackVolume
                + " extPitch=" + s.ExternalPlaybackPitch);
        }

        private void RestoreVoiceProcStopIfNeeded()
        {
            if (!_voiceProcStopOverridden)
            {
                return;
            }

            try
            {
                HSceneProc proc = CurrentProc ?? FindCurrentProc();
                if (proc != null && proc.voice != null)
                {
                    proc.voice.isVoicePrcoStop = _voiceProcStopOriginal;
                }
            }
            catch (Exception ex)
            {
                LogWarn("[guard] restore isVoicePrcoStop failed: " + ex.Message);
            }
            finally
            {
                _voiceProcStopOverridden = false;
                _voiceProcStopOriginal = false;
            }
        }

        // ----------------------------------------------------------------
        // 遅延アクション処理
        // ----------------------------------------------------------------

        private void ProcessDelayedActions()
        {
            float now = Time.unscaledTime;
            for (int i = _delayedActions.Count - 1; i >= 0; i--)
            {
                if (now >= _delayedActions[i].Item1)
                {
                    try { _delayedActions[i].Item2(); }
                    catch (Exception ex) { LogWarn("[delayed] 実行失敗: " + ex.Message); }
                    _delayedActions.RemoveAt(i);
                }
            }
        }

        // ----------------------------------------------------------------
        // response_text コマンド: 生テキストをパースして着替え/着衣を遅延実行
        // ----------------------------------------------------------------

        private void HandleResponseTextCommand(ExternalVoiceFaceCommand command)
        {
            string text = (command.text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text)) return;

            Log($"[response_text] received len={text.Length} preview={text.Substring(0, Math.Min(60, text.Length))}");

            float executeAt = Time.unscaledTime + Mathf.Max(0f, command.delaySeconds);
            int main = command.ResolveMain(Settings?.TargetMainIndex ?? 0);

            string coordName = ParseCoordFromText(text);
            if (!string.IsNullOrEmpty(coordName))
            {
                Log($"[response_text] coord matched: '{coordName}', scheduled delay={command.delaySeconds:F1}s");
                string cn = coordName;
                int m = main;
                _delayedActions.Add(Tuple.Create(executeAt, (Action)(() =>
                {
                    HandleCoordCommand(new ExternalVoiceFaceCommand { type = "coord", coordName = cn, main = m });
                })));
            }
            else
            {
                Log("[response_text] no coord keyword matched");
            }

            ClothesItem[] clothesItems = ParseClothesFromText(text);
            if (clothesItems != null && clothesItems.Length > 0)
            {
                Log($"[response_text] clothes matched {clothesItems.Length} items, scheduled delay={command.delaySeconds:F1}s");
                ClothesItem[] ci = clothesItems;
                int m = main;
                _delayedActions.Add(Tuple.Create(executeAt, (Action)(() =>
                {
                    HandleClothesCommand(new ExternalVoiceFaceCommand { type = "clothes", clothesItems = ci, main = m });
                })));
            }
            else
            {
                Log("[response_text] no clothes keywords matched");
            }
        }

        private string ParseCoordFromText(string text)
        {
            string[] patterns = SplitKeywords(_cfgCoordPattern?.Value ?? "に着替えるね");
            foreach (string pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;

                int idx = text.IndexOf(pattern, StringComparison.Ordinal);
                if (idx <= 0) continue;

                string before = text.Substring(0, idx);
                int nameStart = 0;
                for (int i = before.Length - 1; i >= 0; i--)
                {
                    char c = before[i];
                    if (c == '、' || c == '。' || c == '！' || c == '♡' || c == ' ' || c == '　' || c == '\n' || c == '\r')
                    {
                        nameStart = i + 1;
                        break;
                    }
                }
                string name = before.Substring(nameStart).Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return null;
        }

        private ClothesItem[] ParseClothesFromText(string text)
        {
            if (ContainsAny(text, SplitKeywords(_cfgRemoveAllKeywords?.Value ?? "全裸になるね,全部脱ぐね,全部脱いじゃう")))
            {
                var all = new ClothesItem[8];
                for (int i = 0; i < 8; i++) all[i] = new ClothesItem { kind = i, state = 3 };
                return all;
            }
            if (ContainsAny(text, SplitKeywords(_cfgPutOnAllKeywords?.Value ?? "全部着るね")))
            {
                var all = new ClothesItem[8];
                for (int i = 0; i < 8; i++) all[i] = new ClothesItem { kind = i, state = 0 };
                return all;
            }

            string[][] partKeywords = {
                SplitKeywords(_cfgTopKeywords?.Value      ?? "上着,ジャケット,トップス"),
                SplitKeywords(_cfgBottomKeywords?.Value   ?? "スカート,ホットパンツ,ミニスカ,ボトムス,ズボン,パンツ"),
                SplitKeywords(_cfgBraKeywords?.Value      ?? "ブラ"),
                SplitKeywords(_cfgShortsKeywords?.Value   ?? "パンティー,パンティ"),
                SplitKeywords(_cfgGlovesKeywords?.Value   ?? "グローブ,手袋"),
                SplitKeywords(_cfgPanthoseKeywords?.Value ?? "ガーターベルト,パンスト,ガーター"),
                SplitKeywords(_cfgSocksKeywords?.Value    ?? "ストッキング,ニーハイ,靴下"),
                SplitKeywords(_cfgShoesKeywords?.Value    ?? "ハイヒール,スニーカー,サンダル,ヒール,靴"),
            };

            string[] removeKws = SplitKeywords(_cfgRemoveKeywords?.Value ?? "脱ぐね,脱いじゃう");
            string[] shiftKws  = SplitKeywords(_cfgShiftKeywords?.Value  ?? "ずらすね,半脱ぎにするね");
            string[] putOnKws  = SplitKeywords(_cfgPutOnKeywords?.Value  ?? "着るね,付けるね");

            var results = new Dictionary<int, ClothesItem>();
            var actionGroups = new[] {
                Tuple.Create(removeKws,  3),
                Tuple.Create(shiftKws,  -1),
                Tuple.Create(putOnKws,   0),
            };

            foreach (var group in actionGroups)
            {
                string[] actionKws = group.Item1;
                int state = group.Item2;
                foreach (string actionKw in actionKws)
                {
                    int aIdx = 0;
                    while (true)
                    {
                        int pos = text.IndexOf(actionKw, aIdx, StringComparison.Ordinal);
                        if (pos < 0) break;
                        aIdx = pos + 1;

                        int ctxStart = Math.Max(0, pos - 25);
                        string ctx = text.Substring(ctxStart, pos - ctxStart + actionKw.Length);

                        for (int kind = 0; kind < partKeywords.Length; kind++)
                        {
                            if (results.ContainsKey(kind)) continue;
                            foreach (string kw in partKeywords[kind])
                            {
                                if (ctx.IndexOf(kw, StringComparison.Ordinal) >= 0)
                                {
                                    results[kind] = new ClothesItem { kind = kind, state = state };
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            if (results.Count == 0) return null;
            var arr = new ClothesItem[results.Count];
            int j = 0;
            foreach (var v in results.Values) arr[j++] = v;
            return arr;
        }

        private static string[] SplitKeywords(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return new string[0];
            string[] parts = csv.Split(',');
            var list = new List<string>();
            foreach (string p in parts)
            {
                string t = p.Trim();
                if (!string.IsNullOrEmpty(t)) list.Add(t);
            }
            return list.ToArray();
        }

        private static bool ContainsAny(string text, string[] keywords)
        {
            foreach (string kw in keywords)
                if (text.IndexOf(kw, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // ----------------------------------------------------------------
        // pose コマンド: nameAnimation で体位を切り替える
        // ----------------------------------------------------------------

        private void HandlePoseCommand(ExternalVoiceFaceCommand command)
        {
            string name = (command.poseName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
            {
                LogWarn("[pose] poseName is empty");
                return;
            }

            HSceneProc proc = CurrentProc;
            if (proc == null)
            {
                LogWarn("[pose] HSceneProc not available");
                return;
            }

            var lists = LstUseAnimInfoField?.GetValue(proc) as List<HSceneProc.AnimationListInfo>[];
            if (lists == null)
            {
                LogWarn("[pose] lstUseAnimInfo not found");
                return;
            }

            bool filterMode = command.poseMode >= 0 && System.Enum.IsDefined(typeof(HFlag.EMode), command.poseMode);
            HFlag.EMode targetMode = filterMode ? (HFlag.EMode)command.poseMode : HFlag.EMode.none;

            int bestScore = int.MinValue;
            HSceneProc.AnimationListInfo best = null;

            for (int i = 0; i < lists.Length; i++)
            {
                var list = lists[i];
                if (list == null) continue;
                for (int j = 0; j < list.Count; j++)
                {
                    var c = list[j];
                    if (c == null) continue;
                    if (filterMode && c.mode != targetMode) continue;

                    int score = 0;
                    if (filterMode) score += 500;

                    if (string.Equals(c.nameAnimation, name, StringComparison.OrdinalIgnoreCase))
                        score += 1000;
                    else if (!string.IsNullOrWhiteSpace(c.nameAnimation) &&
                             c.nameAnimation.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 400;
                    else
                        continue;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = c;
                    }
                }
            }

            if (best == null)
            {
                LogWarn($"[pose] not found: name={name} mode={(filterMode ? command.poseMode.ToString() : "any")}");
                return;
            }

            proc.flags.selectAnimationListInfo = best;
            proc.flags.click = HFlag.ClickKind.actionChange;
            Log($"[pose] → id={best.id} mode={best.mode} name={best.nameAnimation}");
        }

        // ----------------------------------------------------------------
        // clothes コマンド: 各部位の着衣状態を変える
        // ----------------------------------------------------------------

        private void HandleClothesCommand(ExternalVoiceFaceCommand command)
        {
            if (command.clothesItems == null || command.clothesItems.Length == 0)
            {
                LogWarn("[clothes] clothesItems が空です");
                return;
            }

            ChaControl female = null;
            HSceneProc proc = FindCurrentProc();
            if (proc != null)
            {
                int main = ClampMainIndex(proc, command.ResolveMain(Settings?.TargetMainIndex ?? 0));
                female = ResolveFemale(proc, main);
            }
            if (female == null)
            {
                female = UnityEngine.Object.FindObjectsOfType<ChaControl>()
                    .FirstOrDefault(c => c != null && c.sex == 1 && c.visibleAll);
            }
            if (female == null)
            {
                LogWarn("[clothes] ChaControl が見つかりません");
                return;
            }

            foreach (var item in command.clothesItems)
            {
                if (item.kind < 0 || item.kind > 8) continue;
                try
                {
                    if (item.state < 0)
                    {
                        female.SetClothesStateNext(item.kind);
                        Log($"[clothes] SetClothesStateNext kind={item.kind}");
                    }
                    else
                    {
                        female.SetClothesState(item.kind, (byte)item.state);
                        Log($"[clothes] SetClothesState kind={item.kind} state={item.state}");
                    }
                }
                catch (Exception ex)
                {
                    LogWarn($"[clothes] 失敗 kind={item.kind}: {ex.Message}");
                }
            }
        }

        // ----------------------------------------------------------------
        // coord コマンド: 衣装名で検索して着替える
        // ----------------------------------------------------------------

        private static readonly string[] CoordTypeNames = { "plain", "swim", "pajamas", "bathing" };

        private void HandleCoordCommand(ExternalVoiceFaceCommand command)
        {
            string coordName = (command.coordName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(coordName))
            {
                LogWarn("[coord] coordName が空です");
                return;
            }

            // ChaControl を取得: HScene → フォールバック
            ChaControl female = null;
            HSceneProc proc = FindCurrentProc();
            if (proc != null)
            {
                int main = ClampMainIndex(proc, command.ResolveMain(Settings?.TargetMainIndex ?? 0));
                female = ResolveFemale(proc, main);
            }
            if (female == null)
            {
                female = UnityEngine.Object.FindObjectsOfType<ChaControl>()
                    .FirstOrDefault(c => c != null && c.sex == 1 && c.visibleAll);
            }
            if (female == null)
            {
                LogWarn("[coord] ChaControl が見つかりません");
                return;
            }

            int coordIndex = TryFindCoordIndexByName(female, coordName);
            if (coordIndex < 0)
            {
                LogWarn($"[coord] '{coordName}' に一致するコーデが見つかりません");
                return;
            }

            try
            {
                female.ChangeCoordinateTypeAndReload((ChaFileDefine.CoordinateType)coordIndex, true);
                Log($"[coord] 着替え完了: '{coordName}' → slot={coordIndex}");
            }
            catch (Exception ex)
            {
                LogWarn($"[coord] ChangeCoordinateTypeAndReload 失敗: {ex.Message}");
            }
        }

        private static int TryFindCoordIndexByName(ChaControl female, string name)
        {
            string nameLower = name.ToLowerInvariant();
            var coordSlots = female.chaFile?.coordinate;
            if (coordSlots == null) return -1;

            // MoreOutfitsController のリフレクション準備
            MonoBehaviour moCtrl = null;
            MethodInfo moGetName = null;
            try
            {
                moCtrl = female.gameObject.GetComponents<MonoBehaviour>()
                    .FirstOrDefault(c => c.GetType().Name == "MoreOutfitsController");
                if (moCtrl != null)
                    moGetName = moCtrl.GetType().GetMethod("GetCoodinateName",
                        BindingFlags.Public | BindingFlags.Instance);
            }
            catch { }

            // 完全一致を優先、次に部分一致
            int partialMatch = -1;
            for (int i = 0; i < coordSlots.Length; i++)
            {
                string slotName = GetCoordSlotName(i, moCtrl, moGetName).ToLowerInvariant();
                if (slotName == nameLower) return i;
                if (partialMatch < 0 && (slotName.Contains(nameLower) || nameLower.Contains(slotName)))
                    partialMatch = i;
            }
            return partialMatch;
        }

        private static string GetCoordSlotName(int index, MonoBehaviour moCtrl, MethodInfo moGetName)
        {
            if (index < CoordTypeNames.Length) return CoordTypeNames[index];
            if (moCtrl != null && moGetName != null)
            {
                try { return (moGetName.Invoke(moCtrl, new object[] { index }) as string) ?? index.ToString(); }
                catch { }
            }
            return index.ToString();
        }

        private string NormalizeAudioPath(string incomingPath)
        {
            if (string.IsNullOrWhiteSpace(incomingPath))
            {
                return string.Empty;
            }

            try
            {
                string path = incomingPath.Trim().Trim('"');
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(PluginDir ?? string.Empty, path);
                }

                return Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                LogWarn("normalize audio path failed: " + ex.Message);
                return string.Empty;
            }
        }

        private bool TryApplyFace(HSceneProc proc, int main, ChaControl female, int face, int voiceKind, int action)
        {
            if (face < 0)
            {
                return true;
            }

            object faceCtrl = GetFaceCtrlByFemaleIndex(proc, main);
            if (faceCtrl == null)
            {
                LogWarn("[cmd] faceCtrl not found main=" + main);
                return false;
            }

            try
            {
                var setFaceMethod = AccessTools.Method(faceCtrl.GetType(), "SetFace", new[] { typeof(int), typeof(ChaControl), typeof(int), typeof(int) });
                if (setFaceMethod == null)
                {
                    LogWarn("[cmd] SetFace method not found on " + faceCtrl.GetType().FullName);
                    return false;
                }

                object resultObj = setFaceMethod.Invoke(faceCtrl, new object[] { face, female, voiceKind, action });
                bool ok = resultObj is bool && (bool)resultObj;
                if (!ok)
                {
                    LogWarn("[cmd] SetFace failed face=" + face + " main=" + main);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogWarn("[cmd] SetFace exception: " + ex.Message);
                return false;
            }
        }

        private static object GetFaceCtrlByFemaleIndex(HSceneProc proc, int femaleIndex)
        {
            if (proc == null)
            {
                return null;
            }

            object face1 = Face1Field?.GetValue(proc);
            if (femaleIndex == 1 && face1 != null)
            {
                return face1;
            }

            return FaceField?.GetValue(proc);
        }

        private int ResolveFace(
            ExternalVoiceFaceCommand command,
            PluginSettings settings,
            out bool keepCurrentFaceMode)
        {
            keepCurrentFaceMode = command.ResolveKeepCurrentFace(settings.KeepCurrentFaceByDefault);
            if (keepCurrentFaceMode)
            {
                return -1;
            }

            int directFace = command.ResolveFace(-1);
            if (directFace >= 0)
            {
                return directFace;
            }

            int randomFromCommand = TrySelectRandomFace(command.faces);
            if (randomFromCommand >= 0)
            {
                return randomFromCommand;
            }

            if (settings.DefaultFace >= 0)
            {
                return settings.DefaultFace;
            }

            int randomFromSettings = TrySelectRandomFace(settings.RandomFaceCandidates);
            if (randomFromSettings >= 0)
            {
                return randomFromSettings;
            }

            return 0;
        }

        private int TrySelectRandomFace(int[] source)
        {
            if (source == null || source.Length == 0)
            {
                return -1;
            }

            int[] valid = new int[source.Length];
            int count = 0;
            for (int i = 0; i < source.Length; i++)
            {
                int face = source[i];
                if (face < 0)
                {
                    continue;
                }

                valid[count] = face;
                count++;
            }

            if (count <= 0)
            {
                return -1;
            }

            lock (_random)
            {
                return valid[_random.Next(count)];
            }
        }

        private HSceneProc FindCurrentProc()
        {
            if (CurrentProc != null)
            {
                return CurrentProc;
            }

            float now = Time.unscaledTime;
            if (now < _nextProcProbeTime)
            {
                return null;
            }

            _nextProcProbeTime = now + 1f;
            CurrentProc = UnityEngine.Object.FindObjectOfType<HSceneProc>();
            return CurrentProc;
        }

        private static int ClampMainIndex(HSceneProc proc, int index)
        {
            if (proc == null || proc.flags == null || proc.flags.lstHeroine == null || proc.flags.lstHeroine.Count <= 0)
            {
                return index < 0 ? 0 : index;
            }

            if (index < 0)
            {
                return 0;
            }

            if (index >= proc.flags.lstHeroine.Count)
            {
                return proc.flags.lstHeroine.Count - 1;
            }

            return index;
        }

        private static ChaControl ResolveFemale(HSceneProc proc, int main)
        {
            if (proc == null || LstFemaleField == null)
            {
                return null;
            }

            try
            {
                IList females = LstFemaleField.GetValue(proc) as IList;
                if (females == null || females.Count <= 0)
                {
                    return null;
                }

                int index = main;
                if (index < 0)
                {
                    index = 0;
                }
                if (index >= females.Count)
                {
                    index = females.Count - 1;
                }

                return females[index] as ChaControl;
            }
            catch (Exception ex)
            {
                LogWarn("resolve female failed: " + ex.Message);
                return null;
            }
        }

        private static int ResolveVoiceNo(HSceneProc proc, int main)
        {
            if (proc == null || proc.flags == null || proc.flags.lstHeroine == null)
            {
                return -1;
            }

            if (main < 0 || main >= proc.flags.lstHeroine.Count || proc.flags.lstHeroine[main] == null)
            {
                return -1;
            }

            return proc.flags.lstHeroine[main].voiceNo;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "CreateListAnimationFileName")]
        private static void CreateListAnimationFileNamePostfix(HSceneProc __instance)
        {
            CurrentProc = __instance;
            DumpPoseList(__instance);
        }

        private static readonly string[] PoseModeNames =
        {
            "aibu", "houshi", "sonyu", "masturbation",
            "peeping", "lesbian", "houshi3P", "sonyu3P", "houshi3PMMF", "sonyu3PMMF"
        };

        private static void DumpPoseList(HSceneProc proc)
        {
            try
            {
                var lists = LstUseAnimInfoField?.GetValue(proc) as List<HSceneProc.AnimationListInfo>[];
                if (lists == null)
                {
                    LogWarn("[pose-dump] lstUseAnimInfo is null");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("[");
                bool first = true;

                for (int i = 0; i < lists.Length; i++)
                {
                    var list = lists[i];
                    if (list == null) continue;
                    string modeName = i < PoseModeNames.Length ? PoseModeNames[i] : i.ToString();

                    for (int j = 0; j < list.Count; j++)
                    {
                        var info = list[j];
                        if (info == null) continue;
                        if (!first) sb.AppendLine(",");
                        first = false;
                        string name = (info.nameAnimation ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                        sb.Append($"  {{\"id\":{info.id},\"mode\":\"{modeName}\",\"modeInt\":{i},\"nameAnimation\":\"{name}\"}}");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("]");

                string path = Path.Combine(PluginDir, "pose_list.json");
                File.WriteAllText(path, sb.ToString(), Utf8NoBom);
                Log($"[pose-dump] wrote {lists.Sum(l => l?.Count ?? 0)} entries → {path}");
            }
            catch (Exception ex)
            {
                LogWarn("[pose-dump] failed: " + ex.Message);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "OnDestroy")]
        private static void HSceneOnDestroyPostfix(HSceneProc __instance)
        {
            if (CurrentProc == __instance)
            {
                if (Instance != null)
                {
                    Instance._blockGameVoiceUntil = 0f;
                    Instance.RestoreVoiceProcStopIfNeeded();
                }
                CurrentProc = null;
                Log("released HSceneProc at OnDestroy");
            }
        }

        internal static void Log(string message)
        {
            Logger?.LogInfo(message);
            AppendFileLog(message);
        }

        internal static void LogWarn(string message)
        {
            Logger?.LogWarning(message);
            AppendFileLog("[WARN] " + message);
        }

        internal static void LogError(string message)
        {
            Logger?.LogError(message);
            AppendFileLog("[ERROR] " + message);
        }

        private static void AppendFileLog(string message)
        {
            if (string.IsNullOrEmpty(LogFilePath))
            {
                return;
            }

            try
            {
                lock (FileLogLock)
                {
                    File.AppendAllText(
                        LogFilePath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}",
                        Utf8NoBom);
                }
            }
            catch
            {
            }
        }
    }
}
