using ActionGame;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MainGameSubtitleCore
{
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("KoikatsuSunshine")]
    internal sealed class Plugin : BaseUnityPlugin, ISubtitleRequestSink
    {
        private enum DisplayBackendMode
        {
            Auto,
            InformationUI,
            Overlay
        }

        private enum InputPanelSection
        {
            None,
            History,
            Preset
        }

        private struct InputPanelSkin
        {
            public Font Font;
            public Sprite PanelSprite;
            public Image.Type PanelType;
            public Color PanelColor;
            public Material PanelMaterial;
            public Sprite ButtonSprite;
            public Image.Type ButtonType;
            public Color ButtonColor;
            public Material ButtonMaterial;
            public ColorBlock ButtonColors;
        }

        [Serializable]
        private sealed class ForwardTextPayload
        {
            public string text;
            public string source;
            public string event_id;
            public long sent_at_unix_ms;
        }

        public const string GUID = "com.kks.maingame.subtitlecore";
        public const string PluginName = "MainGameSubtitleCore";
        public const string Version = "0.4.1";

        internal static new ManualLogSource Logger { get; private set; }

        private static readonly object FileLogLock = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo WaitTimeField = typeof(InformationUI).GetField("_waitTime", PrivateInstance);
        private static readonly Regex OverlayColorValueTagRegex =
            new Regex(@"<color\s*=\s*['""]?(?<value>[^>""']+)['""]?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Type HSceneProcType = Type.GetType("HSceneProc, Assembly-CSharp");
        private static readonly Type VRHSceneType = Type.GetType("VRHScene, Assembly-CSharp");

        private static string _pluginDir;
        private static string _logFilePath;
        private static string _inputPanelStatePath;
        private static PluginSettings _settings;
        private static DisplayBackendMode _displayBackend = DisplayBackendMode.Auto;
        private static float _overlayUntil;
        private static float _overlayStartedAt;
        private static string _overlayText = string.Empty;
        private static string _overlayUpperText = string.Empty;
        private static float _overlayUpperUntil;
        private static GUIStyle _overlayStyle;
        private static GUIStyle _overlayShadowStyle;
        private static Color _overlayTextColor = Color.white;
        private static Color _overlayShadowColor = Color.black;
        private static Color _overlayIntroHighlightColor = Color.white;
        private static Font _overlayResolvedFont;
        private static string _overlayResolvedFontSource = string.Empty;
        private static bool _femaleKeepVisibleUntilNext = false;
        private static float _femaleMaxHoldSeconds = 30f;
        // Template clone often carries scene-specific components and breaks click routing.
        // Keep skin sampling from templates, but build runtime controls explicitly.
        private const bool EnableTemplateControlClone = false;
        private static bool _inputPanelVisible;
        private static bool _focusInputNextFrame;
        private static bool _isHSceneCached;
        private static float _hSceneCachedUntil;

        private readonly Queue<SubtitleRequest> _externalRequests = new Queue<SubtitleRequest>();
        private readonly object _externalRequestsLock = new object();
        private readonly List<string> _inputHistory = new List<string>();
        private readonly List<string> _inputPresets = new List<string>();
        private Canvas _inputCanvas;
        private RectTransform _inputPanelRect;
        private InputField _inputField;
        private RectTransform _inputPanelTemplate;
        private RectTransform _inputSectionRoot;
        private RectTransform _historySectionRoot;
        private RectTransform _presetSectionRoot;
        private LayoutElement _historySectionLayoutElement;
        private LayoutElement _presetSectionLayoutElement;
        private RectTransform _historyContent;
        private RectTransform _presetContent;
        private Text _historyHeaderText;
        private Text _presetHeaderText;
        private Button _historyToggleButton;
        private Button _presetToggleButton;
        private Button _buttonTemplate;
        private Vector2 _buttonTemplateSize = new Vector2(176f, 60f);
        private InputField _inputFieldTemplate;
        private Vector2 _inputFieldTemplateSize = new Vector2(192f, 40f);
        private InputPanelSection _activeInputSection;
        private bool _inputPanelSkinResolved;
        private bool _inputPanelTemplateReady;
        private InputPanelSkin _inputPanelSkin;
        private EventSystem _eventSystem;
        private ConfigEntry<int> _cfgOverlayFontSize;
        private ConfigEntry<float> _cfgDefaultHoldSeconds;
        private ConfigEntry<bool> _cfgFemaleKeepVisibleUntilNext;
        private ConfigEntry<float> _cfgFemaleMaxHoldSeconds;

        private void Awake()
        {
            Logger = base.Logger;
            _pluginDir = Path.GetDirectoryName(Info.Location);
            _logFilePath = Path.Combine(_pluginDir, PluginName + ".log");
            _inputPanelStatePath = Path.Combine(_pluginDir, "SubtitleInputPanelState.json");

            Directory.CreateDirectory(_pluginDir);
            File.WriteAllText(
                _logFilePath,
                $"[{DateTime.Now:HH:mm:ss}] === {PluginName} {Version} started ==={Environment.NewLine}",
                Utf8NoBom);

            ReloadSettings();
            BindRuntimeConfig();
            ApplyRuntimeConfigOverrides();
            ReloadInputPanelState();
            SubtitleApi.Register(this);
            Log("awake complete");
        }

        private void OnDestroy()
        {
            SaveInputPanelState();
            if (_inputCanvas != null)
            {
                UnityEngine.Object.Destroy(_inputCanvas.gameObject);
                _inputCanvas = null;
            }
            if (_eventSystem != null &&
                _eventSystem.gameObject != null &&
                _eventSystem.gameObject.transform.parent == transform &&
                string.Equals(_eventSystem.gameObject.name, "MainGameSubtitleEventSystem", StringComparison.Ordinal))
            {
                UnityEngine.Object.Destroy(_eventSystem.gameObject);
                _eventSystem = null;
            }
            SubtitleApi.Unregister(this);
        }

        private void Update()
        {
            if (_settings != null && _settings.EnableCtrlRReload && IsCtrlRDown())
            {
                ReloadSettings();
                ReloadInputPanelState();
                Log("settings reloaded by Ctrl+R");
            }

            if (_settings == null || !_settings.Enabled)
            {
                _inputPanelVisible = false;
                SetInputPanelVisible(false);
                return;
            }

            HandleInputPanelHotkey();
            UpdateInputPanelRuntime();
            DrainExternalRequests(8);

            if (IsConfiguredKeyDown(_settings.TriggerKey))
            {
                ShowSubtitleCore(
                    _settings.TestText,
                    _settings.HoldSeconds,
                    _displayBackend,
                    _settings.DisplayMode,
                    "hotkey");
            }
        }

        private void OnGUI()
        {
            if (_settings == null || !_settings.Enabled)
            {
                return;
            }

            bool hasOverlay = Time.unscaledTime <= _overlayUntil || Time.unscaledTime <= _overlayUpperUntil;
            if (!hasOverlay)
            {
                return;
            }

            if (hasOverlay)
            {
                EnsureOverlayResources();

                float drawScale = 1f;
                float drawYOffset = 0f;
                float drawAlphaMul = 1f;
                Color drawTextColorBase = _overlayTextColor;
                if (_settings.OverlayIntroEnabled && _settings.OverlayIntroDuration > 0f)
                {
                    float elapsed = Mathf.Max(0f, Time.unscaledTime - _overlayStartedAt);
                    float duration = Mathf.Max(0.01f, _settings.OverlayIntroDuration);
                    if (elapsed < duration)
                    {
                        float t = Mathf.Clamp01(elapsed / duration);
                        float eased = EaseOutBack01(t, _settings.OverlayIntroBackOvershoot);
                        drawScale = Mathf.Lerp(_settings.OverlayIntroScaleFrom, 1f, eased);
                        drawYOffset = Mathf.Lerp(_settings.OverlayIntroYOffset, 0f, t);
                        drawAlphaMul = Mathf.Clamp01(t);

                        float hiDur = Mathf.Max(0f, _settings.OverlayIntroHighlightDuration);
                        if (hiDur > 0f && elapsed < hiDur)
                        {
                            float hiT = Mathf.Clamp01(elapsed / hiDur);
                            drawTextColorBase = Color.Lerp(_overlayIntroHighlightColor, _overlayTextColor, hiT);
                        }
                    }
                }

                int baseFontSize = _settings.OverlayFontSize;
                int drawFontSize = Mathf.Clamp(Mathf.RoundToInt(baseFontSize * Mathf.Max(1f, drawScale)), 12, 160);
                _overlayStyle.fontSize = drawFontSize;
                _overlayShadowStyle.fontSize = drawFontSize;

                Color drawTextColor = drawTextColorBase;
                drawTextColor.a *= drawAlphaMul;
                _overlayStyle.normal.textColor = drawTextColor;

                Color drawShadowColor = _overlayShadowColor;
                drawShadowColor.a *= drawAlphaMul;
                _overlayShadowStyle.normal.textColor = drawShadowColor;

                float marginX = Mathf.Clamp(_settings.OverlayHorizontalMargin, 0f, Screen.width * 0.45f);
                float width = Mathf.Max(200f, Screen.width - (marginX * 2f));
                float mainTextHeight = _overlayStyle.CalcHeight(new GUIContent(_overlayText ?? string.Empty), width);
                bool hasUpper = !string.IsNullOrEmpty(_overlayUpperText) && Time.unscaledTime <= _overlayUpperUntil;
                float upperTextHeight = hasUpper ? _overlayStyle.CalcHeight(new GUIContent(_overlayUpperText), width) : 0f;
                float lineGap = hasUpper ? Mathf.Clamp(_settings.OverlayLineSpacing, 0f, 80f) : 0f;
                float totalHeight = Mathf.Max(32f, mainTextHeight + upperTextHeight + lineGap + 2f);
                float y = Mathf.Max(0f, Screen.height - _settings.OverlayBottomMargin - totalHeight + drawYOffset);
                float x = (Screen.width - width) * 0.5f;

                if (hasUpper)
                {
                    Rect upperRect = new Rect(x, y, width, Mathf.Max(24f, upperTextHeight + 2f));
                    DrawOverlayLine(upperRect, _overlayUpperText);
                    y += upperRect.height + lineGap;
                }

                Rect textRect = new Rect(x, y, width, Mathf.Max(24f, mainTextHeight + 2f));
                DrawOverlayLine(textRect, _overlayText);
            }
        }

        private static readonly Regex ColorTagRegex = new Regex(@"<color=[^>]*>|</color>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string StripColorTags(string text)
        {
            return text == null ? string.Empty : ColorTagRegex.Replace(text, string.Empty);
        }

        private static void DrawOverlayLine(Rect textRect, string text)
        {
            if (_settings == null)
            {
                return;
            }

            if (_settings.OverlayShadowEnabled)
            {
                Rect shadowRect = new Rect(
                    textRect.x + _settings.OverlayShadowOffsetX,
                    textRect.y + _settings.OverlayShadowOffsetY,
                    textRect.width,
                    textRect.height);
                GUI.Label(shadowRect, StripColorTags(text), _overlayShadowStyle);
            }

            GUI.Label(textRect, text, _overlayStyle);
        }

        private static bool IsCtrlRDown()
        {
            return (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && Input.GetKeyDown(KeyCode.R);
        }

        private void HandleInputPanelHotkey()
        {
            if (_settings == null || !_settings.InputPanelEnabled)
            {
                SetInputPanelVisible(false);
                return;
            }

            if (_settings.InputPanelCloseWhenHExits &&
                _inputPanelVisible &&
                _settings.InputPanelShowOnlyInH &&
                !IsHSceneActiveCached())
            {
                SetInputPanelVisible(false);
            }

            bool toggleRequested = Input.GetKeyDown(KeyCode.F7) || IsConfiguredKeyDown(_settings.InputPanelToggleKey);
            if (!toggleRequested)
            {
                return;
            }

            if (_settings.InputPanelShowOnlyInH && !IsHSceneActiveCached())
            {
                if (_settings.VerboseLog)
                {
                    Log("input panel toggle ignored: h scene is not active");
                }
                return;
            }

            SetInputPanelVisible(!_inputPanelVisible);
        }

        private void UpdateInputPanelRuntime()
        {
            if (!_inputPanelVisible)
            {
                return;
            }

            if (_settings == null || !_settings.Enabled || !_settings.InputPanelEnabled)
            {
                SetInputPanelVisible(false);
                return;
            }

            if (_settings.InputPanelCloseWhenHExits &&
                _settings.InputPanelShowOnlyInH &&
                !IsHSceneActiveCached())
            {
                SetInputPanelVisible(false);
                return;
            }

            EnsureInputPanelUi();
            if (_inputCanvas == null)
            {
                return;
            }

            if (!_inputCanvas.enabled)
            {
                _inputCanvas.enabled = true;
            }

            UpdateInputPanelLayout();

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                SetInputPanelVisible(false);
                return;
            }

            if (_settings.InputPanelSendOnEnter &&
                (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                SendInputDraft();
                return;
            }

            if (_focusInputNextFrame)
            {
                FocusInputField(keepNextFrame: false);
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void SetInputPanelVisible(bool visible)
        {
            bool changed = _inputPanelVisible != visible;
            _inputPanelVisible = visible;

            if (!visible)
            {
                _focusInputNextFrame = false;

                if (_inputCanvas != null)
                {
                    _inputCanvas.enabled = false;
                }

                EventSystem current = EventSystem.current;
                if (current != null &&
                    _inputField != null &&
                    current.currentSelectedGameObject == _inputField.gameObject)
                {
                    current.SetSelectedGameObject(null);
                }
            }
            else
            {
                if (_settings == null || !_settings.Enabled || !_settings.InputPanelEnabled)
                {
                    _inputPanelVisible = false;
                    return;
                }

                if (_settings.InputPanelShowOnlyInH && !IsHSceneActiveCached())
                {
                    _inputPanelVisible = false;
                    return;
                }

                EnsureInputPanelUi();
                if (_inputCanvas == null)
                {
                    _inputPanelVisible = false;
                    return;
                }

                UpdateInputPanelLayout();
                RebuildInputLists();
                _inputCanvas.enabled = true;
                SetActiveInputSection(InputPanelSection.None, focusInput: false);
                FocusInputField(keepNextFrame: true);
            }

            if (_settings != null && _settings.VerboseLog && changed)
            {
                Log("input panel visible=" + _inputPanelVisible);
            }
        }

        private void FocusInputField(bool keepNextFrame)
        {
            _focusInputNextFrame = keepNextFrame;
            if (_inputField == null)
            {
                return;
            }

            EventSystem current = EventSystem.current;
            if (current != null)
            {
                current.SetSelectedGameObject(_inputField.gameObject);
            }

            _inputField.Select();
            _inputField.ActivateInputField();
        }

        private void EnsureInputPanelUi()
        {
            if (_inputCanvas != null)
            {
                return;
            }

            EnsureInputPanelSkin();
            EnsureEventSystem();

            GameObject canvasGo = new GameObject(
                "MainGameSubtitleInputCanvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            _inputCanvas = canvasGo.GetComponent<Canvas>();
            _inputCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _inputCanvas.sortingOrder = 4500;

            CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            RectTransform canvasRect = canvasGo.GetComponent<RectTransform>();
            canvasRect.anchorMin = Vector2.zero;
            canvasRect.anchorMax = Vector2.one;
            canvasRect.offsetMin = Vector2.zero;
            canvasRect.offsetMax = Vector2.zero;

            _inputPanelRect = CreateUiRect(
                canvasRect,
                "InputPanel",
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                new Vector2(760f, 140f));

            ApplyInputPanelFrameTemplate();

            VerticalLayoutGroup panelLayout = _inputPanelRect.gameObject.AddComponent<VerticalLayoutGroup>();
            panelLayout.padding = new RectOffset(10, 10, 10, 10);
            panelLayout.spacing = 8f;
            panelLayout.childControlWidth = true;
            panelLayout.childControlHeight = true;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;

            _inputSectionRoot = CreateUiRect(
                _inputPanelRect,
                "InputSection",
                Vector2.zero,
                Vector2.zero,
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                Vector2.zero);
            LayoutElement inputSectionElement = _inputSectionRoot.gameObject.AddComponent<LayoutElement>();
            inputSectionElement.preferredHeight = 44f;

            HorizontalLayoutGroup inputRowLayout = _inputSectionRoot.gameObject.AddComponent<HorizontalLayoutGroup>();
            inputRowLayout.spacing = 8f;
            inputRowLayout.padding = new RectOffset(0, 0, 0, 0);
            inputRowLayout.childAlignment = TextAnchor.MiddleLeft;
            inputRowLayout.childControlWidth = true;
            inputRowLayout.childControlHeight = true;
            inputRowLayout.childForceExpandWidth = false;
            inputRowLayout.childForceExpandHeight = true;

            _inputField = CreateInputField(_inputSectionRoot, "InputField");
            LayoutElement inputFieldElement = GetOrAddLayoutElement(_inputField.gameObject);
            inputFieldElement.flexibleWidth = 1f;
            inputFieldElement.minWidth = 260f;

            Button sendButton = CreateButton(_inputSectionRoot, "SendButton", "送信", SendInputDraft);
            LayoutElement sendButtonLayout = GetOrAddLayoutElement(sendButton.gameObject);
            sendButtonLayout.preferredWidth = 100f;
            sendButtonLayout.preferredHeight = 36f;

            Button presetRegisterButton = CreateButton(_inputSectionRoot, "PresetRegisterButton", "プリセット登録", AddPresetFromDraft);
            LayoutElement presetRegisterLayout = GetOrAddLayoutElement(presetRegisterButton.gameObject);
            presetRegisterLayout.preferredWidth = 156f;
            presetRegisterLayout.preferredHeight = 36f;

            RectTransform toggleRow = CreateUiRect(
                _inputPanelRect,
                "ToggleRow",
                Vector2.zero,
                Vector2.zero,
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                Vector2.zero);
            LayoutElement toggleRowElement = toggleRow.gameObject.AddComponent<LayoutElement>();
            toggleRowElement.preferredHeight = 36f;

            HorizontalLayoutGroup toggleLayout = toggleRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            toggleLayout.spacing = 8f;
            toggleLayout.padding = new RectOffset(0, 0, 0, 0);
            toggleLayout.childAlignment = TextAnchor.MiddleLeft;
            toggleLayout.childControlWidth = true;
            toggleLayout.childControlHeight = true;
            toggleLayout.childForceExpandWidth = false;
            toggleLayout.childForceExpandHeight = true;

            _historyToggleButton = CreateButton(toggleRow, "HistoryToggle", "履歴", () => ToggleInputSection(InputPanelSection.History));
            LayoutElement historyToggleLayout = GetOrAddLayoutElement(_historyToggleButton.gameObject);
            historyToggleLayout.preferredWidth = 130f;
            historyToggleLayout.preferredHeight = 34f;

            _presetToggleButton = CreateButton(toggleRow, "PresetToggle", "プリセット", () => ToggleInputSection(InputPanelSection.Preset));
            LayoutElement presetToggleLayout = GetOrAddLayoutElement(_presetToggleButton.gameObject);
            presetToggleLayout.preferredWidth = 130f;
            presetToggleLayout.preferredHeight = 34f;

            _historySectionRoot = CreateUiRect(
                canvasRect,
                "HistorySection",
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 1f),
                new Vector2(0f, -80f),
                new Vector2(760f, 300f));
            LayoutElement historySectionElement = _historySectionRoot.gameObject.AddComponent<LayoutElement>();
            historySectionElement.preferredHeight = 300f;
            _historySectionLayoutElement = historySectionElement;
            VerticalLayoutGroup historySectionLayout = _historySectionRoot.gameObject.AddComponent<VerticalLayoutGroup>();
            historySectionLayout.padding = new RectOffset(0, 0, 0, 0);
            historySectionLayout.spacing = 6f;
            historySectionLayout.childControlWidth = true;
            historySectionLayout.childControlHeight = true;
            historySectionLayout.childForceExpandWidth = true;
            historySectionLayout.childForceExpandHeight = false;
            BuildListColumn(_historySectionRoot, "履歴", out _historyHeaderText, out _historyContent);

            _presetSectionRoot = CreateUiRect(
                canvasRect,
                "PresetSection",
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 1f),
                new Vector2(0f, -80f),
                new Vector2(760f, 300f));
            LayoutElement presetSectionElement = _presetSectionRoot.gameObject.AddComponent<LayoutElement>();
            presetSectionElement.preferredHeight = 300f;
            _presetSectionLayoutElement = presetSectionElement;
            VerticalLayoutGroup presetSectionLayout = _presetSectionRoot.gameObject.AddComponent<VerticalLayoutGroup>();
            presetSectionLayout.padding = new RectOffset(0, 0, 0, 0);
            presetSectionLayout.spacing = 6f;
            presetSectionLayout.childControlWidth = true;
            presetSectionLayout.childControlHeight = true;
            presetSectionLayout.childForceExpandWidth = true;
            presetSectionLayout.childForceExpandHeight = false;
            BuildListColumn(_presetSectionRoot, "プリセット", out _presetHeaderText, out _presetContent);

            UpdateInputPanelLayout();
            RebuildInputLists();
            SetActiveInputSection(InputPanelSection.None, focusInput: false);
            if (_inputPanelRect != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_inputPanelRect);
            }
            _inputCanvas.enabled = false;
        }

        private void UpdateInputPanelLayout()
        {
            if (_settings == null || _inputPanelRect == null)
            {
                return;
            }

            float width = Mathf.Min(
                Mathf.Clamp(_settings.InputPanelWidth, 420f, 1400f),
                Mathf.Max(420f, Screen.width - 24f));
            if (_activeInputSection == InputPanelSection.None)
            {
                width = Mathf.Min(width, 760f);
            }

            float sectionHeight = 0f;
            switch (_activeInputSection)
            {
                case InputPanelSection.History:
                case InputPanelSection.Preset:
                    sectionHeight = Mathf.Clamp(
                        _settings.InputPanelListHeight + 20f,
                        160f,
                        Mathf.Max(160f, Screen.height - 220f));
                    break;
                default:
                    sectionHeight = 0f;
                    break;
            }

            if (_historySectionLayoutElement != null)
            {
                _historySectionLayoutElement.preferredHeight = _activeInputSection == InputPanelSection.History
                    ? Mathf.Max(120f, sectionHeight)
                    : 0f;
            }

            if (_presetSectionLayoutElement != null)
            {
                _presetSectionLayoutElement.preferredHeight = _activeInputSection == InputPanelSection.Preset
                    ? Mathf.Max(120f, sectionHeight)
                    : 0f;
            }

            float baseHeight = 96f;
            float height = Mathf.Min(baseHeight, Mathf.Max(96f, Screen.height - 24f));

            _inputPanelRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            _inputPanelRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);

            float listWidth = width;
            float listHeight = Mathf.Max(120f, sectionHeight);
            float listY = -(height * 0.5f + 8f);

            if (_historySectionRoot != null)
            {
                _historySectionRoot.anchoredPosition = new Vector2(0f, listY);
                _historySectionRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, listWidth);
                _historySectionRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, listHeight);
            }

            if (_presetSectionRoot != null)
            {
                _presetSectionRoot.anchoredPosition = new Vector2(0f, listY);
                _presetSectionRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, listWidth);
                _presetSectionRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, listHeight);
            }

            int fontSize = Mathf.Clamp(_settings.InputPanelInputFontSize, 14, 48);
            if (_inputField != null)
            {
                if (_inputField.textComponent != null)
                {
                    _inputField.textComponent.fontSize = fontSize;
                }

                Text placeholder = _inputField.placeholder as Text;
                if (placeholder != null)
                {
                    placeholder.fontSize = Mathf.Max(12, fontSize - 2);
                }
            }
        }

        private void ToggleInputSection(InputPanelSection section)
        {
            if (_activeInputSection == section)
            {
                SetActiveInputSection(InputPanelSection.None, focusInput: false);
                if (_settings != null && _settings.VerboseLog)
                {
                    Log("input panel section=none");
                }
                return;
            }

            SetActiveInputSection(section, focusInput: false);
            if (_settings != null && _settings.VerboseLog)
            {
                Log("input panel section=" + section);
            }
        }

        private void SetActiveInputSection(InputPanelSection section, bool focusInput)
        {
            _activeInputSection = section;

            if (_inputSectionRoot != null)
            {
                _inputSectionRoot.gameObject.SetActive(true);
            }

            if (_historySectionRoot != null)
            {
                _historySectionRoot.gameObject.SetActive(section == InputPanelSection.History);
            }

            if (_presetSectionRoot != null)
            {
                _presetSectionRoot.gameObject.SetActive(section == InputPanelSection.Preset);
            }

            UpdateTabButtonVisuals();
            UpdateInputPanelLayout();
            if (_inputPanelRect != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_inputPanelRect);
            }

            if (section == InputPanelSection.History || section == InputPanelSection.Preset)
            {
                RebuildInputLists();
                if (_inputPanelRect != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_inputPanelRect);
                }
            }

            _focusInputNextFrame = focusInput;
        }

        private void UpdateTabButtonVisuals()
        {
            ApplyButtonVisual(_historyToggleButton, _activeInputSection == InputPanelSection.History);
            ApplyButtonVisual(_presetToggleButton, _activeInputSection == InputPanelSection.Preset);
        }

        private void EnsureEventSystem()
        {
            EventSystem current = EventSystem.current;
            if (current != null)
            {
                _eventSystem = current;
                return;
            }

            GameObject eventSystemGo = new GameObject(
                "MainGameSubtitleEventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
            eventSystemGo.transform.SetParent(transform, false);
            _eventSystem = eventSystemGo.GetComponent<EventSystem>();
        }

        private void EnsureInputPanelSkin()
        {
            if (_inputPanelSkinResolved)
            {
                return;
            }

            _inputPanelSkin = CreateFallbackInputPanelSkin();
            _inputPanelTemplateReady = false;
            _inputPanelTemplate = null;
            _buttonTemplate = null;
            _inputFieldTemplate = null;
            RectTransform[] rects = Resources.FindObjectsOfTypeAll<RectTransform>();
            RectTransform panelCandidate = null;
            Button buttonCandidate = null;
            InputField fieldCandidate = null;

            // 1) Strict: use InputName panel that has both btnYes and InputLastName.
            for (int i = 0; i < rects.Length; i++)
            {
                RectTransform rect = rects[i];
                if (rect == null || !rect.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (IsOwnInputUiObject(rect))
                {
                    continue;
                }

                string nameText = rect.name ?? string.Empty;
                if (nameText.IndexOf("InputName", StringComparison.OrdinalIgnoreCase) < 0 ||
                    nameText.IndexOf("InputLastName", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nameText.IndexOf("InputFirstName", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                Button[] buttonsInPanel = rect.GetComponentsInChildren<Button>(true);
                InputField[] fieldsInPanel = rect.GetComponentsInChildren<InputField>(true);
                Button yesButton = null;
                InputField lastNameField = null;

                for (int j = 0; j < buttonsInPanel.Length; j++)
                {
                    Button b = buttonsInPanel[j];
                    if (b == null)
                    {
                        continue;
                    }

                    string bn = b.name ?? string.Empty;
                    if (bn.IndexOf("btnYes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        string.Equals(bn, "Yes", StringComparison.OrdinalIgnoreCase))
                    {
                        yesButton = b;
                        break;
                    }
                }

                for (int j = 0; j < fieldsInPanel.Length; j++)
                {
                    InputField f = fieldsInPanel[j];
                    if (f == null)
                    {
                        continue;
                    }

                    string fn = f.name ?? string.Empty;
                    if (fn.IndexOf("InputLastName", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        lastNameField = f;
                        break;
                    }
                }

                if (yesButton == null || lastNameField == null)
                {
                    continue;
                }

                panelCandidate = rect;
                buttonCandidate = yesButton;
                fieldCandidate = lastNameField;
                break;
            }

            // 2) Fallback: resolve each template by explicit object names only.
            if (buttonCandidate == null)
            {
                Button[] buttons = Resources.FindObjectsOfTypeAll<Button>();
                int bestScore = int.MinValue;
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button button = buttons[i];
                    if (button == null ||
                        !button.gameObject.scene.IsValid() ||
                        IsOwnInputUiObject(button.transform))
                    {
                        continue;
                    }

                    string nameText = button.name ?? string.Empty;
                    bool isBtnYes = string.Equals(nameText, "btnYes", StringComparison.OrdinalIgnoreCase) ||
                                    nameText.IndexOf("btnYes", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isYes = string.Equals(nameText, "Yes", StringComparison.OrdinalIgnoreCase);
                    if (!isBtnYes && !isYes)
                    {
                        continue;
                    }

                    int score = isBtnYes ? 300 : 260;
                    string path = GetTransformPath(button.transform);
                    if (path.IndexOf("InputName", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 60;
                    }
                    if (path.IndexOf("ExitDialog", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 40;
                    }

                    Image image = button.GetComponent<Image>();
                    if (image != null && image.sprite != null)
                    {
                        score += 10;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        buttonCandidate = button;
                    }
                }
            }

            if (fieldCandidate == null)
            {
                InputField[] fields = Resources.FindObjectsOfTypeAll<InputField>();
                int bestScore = int.MinValue;
                for (int i = 0; i < fields.Length; i++)
                {
                    InputField field = fields[i];
                    if (field == null ||
                        !field.gameObject.scene.IsValid() ||
                        IsOwnInputUiObject(field.transform))
                    {
                        continue;
                    }

                    string nameText = field.name ?? string.Empty;
                    int score = int.MinValue;
                    if (string.Equals(nameText, "InputLastName", StringComparison.OrdinalIgnoreCase))
                    {
                        score = 300;
                    }
                    else if (nameText.IndexOf("InputLastName", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score = 260;
                    }
                    else if (string.Equals(nameText, "InputName", StringComparison.OrdinalIgnoreCase))
                    {
                        score = 220;
                    }
                    else if (nameText.IndexOf("InputName", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score = 180;
                    }
                    else
                    {
                        continue;
                    }

                    string path = GetTransformPath(field.transform);
                    if (path.IndexOf("InputName", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 30;
                    }

                    if (field.textComponent != null)
                    {
                        score += 10;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        fieldCandidate = field;
                    }
                }
            }

            if (panelCandidate == null)
            {
                panelCandidate = FindTemplatePanelFromAncestor(fieldCandidate != null ? fieldCandidate.transform : null);
            }
            if (panelCandidate == null)
            {
                panelCandidate = FindTemplatePanelFromAncestor(buttonCandidate != null ? buttonCandidate.transform : null);
            }
            if (panelCandidate == null)
            {
                for (int i = 0; i < rects.Length; i++)
                {
                    RectTransform rect = rects[i];
                    if (rect == null ||
                        !rect.gameObject.scene.IsValid() ||
                        IsOwnInputUiObject(rect))
                    {
                        continue;
                    }

                    string nameText = rect.name ?? string.Empty;
                    if (nameText.IndexOf("InputName", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    if (nameText.IndexOf("InputLastName", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        nameText.IndexOf("InputFirstName", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }
                    if (rect.GetComponent<Image>() == null)
                    {
                        continue;
                    }

                    panelCandidate = rect;
                    break;
                }
            }

            if (panelCandidate != null)
            {
                _inputPanelTemplate = panelCandidate;
                if (_settings != null && _settings.VerboseLog)
                {
                    Log("input panel frame template: " + GetTransformPath(panelCandidate));
                }
            }
            else if (_settings != null && _settings.VerboseLog)
            {
                LogWarn("input panel frame template not found (expected: InputName or ancestor panel)");
            }

            if (buttonCandidate != null)
            {
                _buttonTemplate = buttonCandidate;
                Text candidateLabel = GetPreferredButtonText(buttonCandidate.transform);
                RectTransform candidateRect = buttonCandidate.GetComponent<RectTransform>();
                if (candidateRect != null)
                {
                    Vector2 size = candidateRect.rect.size;
                    if (size.x > 1f && size.y > 1f)
                    {
                        _buttonTemplateSize = size;
                    }
                }

                Image candidateImage = buttonCandidate.GetComponent<Image>();
                if (candidateImage != null && candidateImage.sprite != null)
                {
                    _inputPanelSkin.ButtonSprite = candidateImage.sprite;
                    _inputPanelSkin.ButtonType = candidateImage.type;
                    _inputPanelSkin.ButtonMaterial = candidateImage.material;
                }

                _inputPanelSkin.ButtonColors = buttonCandidate.colors;
                ColorBlock colors = _inputPanelSkin.ButtonColors;
                _inputPanelSkin.ButtonColor = colors.normalColor;

                if (candidateLabel != null && candidateLabel.font != null)
                {
                    _inputPanelSkin.Font = candidateLabel.font;
                }

                if (_settings != null && _settings.VerboseLog)
                {
                    string spriteName = candidateImage != null && candidateImage.sprite != null
                        ? candidateImage.sprite.name
                        : "none";
                    Log("input panel skin source button: " + GetTransformPath(buttonCandidate.transform) + " sprite=" + spriteName);
                }
            }
            else if (_settings != null && _settings.VerboseLog)
            {
                LogWarn("input panel button template not found (expected: btnYes/Yes)");
            }

            if (fieldCandidate != null)
            {
                _inputFieldTemplate = fieldCandidate;
                RectTransform fieldRect = fieldCandidate.GetComponent<RectTransform>();
                if (fieldRect != null)
                {
                    Vector2 size = fieldRect.rect.size;
                    if (size.x > 1f && size.y > 1f)
                    {
                        _inputFieldTemplateSize = size;
                    }
                }

                if (fieldCandidate.textComponent != null && fieldCandidate.textComponent.font != null)
                {
                    _inputPanelSkin.Font = fieldCandidate.textComponent.font;
                }

                if (_settings != null && _settings.VerboseLog)
                {
                    Log("input panel input template: " + GetTransformPath(fieldCandidate.transform));
                }
            }
            else if (_settings != null && _settings.VerboseLog)
            {
                LogWarn("input panel input template not found (expected: InputLastName under InputName)");
            }

            _inputPanelTemplateReady = EnableTemplateControlClone
                ? (_buttonTemplate != null && _inputFieldTemplate != null)
                : true;
            _inputPanelSkinResolved = true;
            if (EnableTemplateControlClone && !_inputPanelTemplateReady && _settings != null && _settings.VerboseLog)
            {
                LogWarn("input panel templates incomplete; retry on next open.");
            }
            else if (!EnableTemplateControlClone && _settings != null && _settings.VerboseLog)
            {
                Log("input panel controls use runtime build (template clone disabled)");
            }
        }

        private static RectTransform FindTemplatePanelFromAncestor(Transform start)
        {
            if (start == null)
            {
                return null;
            }

            RectTransform firstWithImage = null;
            Transform cursor = start;
            while (cursor != null)
            {
                RectTransform rect = cursor as RectTransform;
                if (rect != null && !IsOwnInputUiObject(rect))
                {
                    Image image = rect.GetComponent<Image>();
                    if (image != null)
                    {
                        string nameText = rect.name ?? string.Empty;
                        if (nameText.IndexOf("InputName", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return rect;
                        }

                        if (firstWithImage == null)
                        {
                            firstWithImage = rect;
                        }
                    }
                }

                cursor = cursor.parent;
            }

            return firstWithImage;
        }

        private void ApplyInputPanelFrameTemplate()
        {
            if (_inputPanelRect == null)
            {
                return;
            }

            Image image = _inputPanelRect.GetComponent<Image>();
            if (image == null)
            {
                image = _inputPanelRect.gameObject.AddComponent<Image>();
            }

            if (_inputPanelTemplate != null)
            {
                Image templateImage = _inputPanelTemplate.GetComponent<Image>();
                if (templateImage != null)
                {
                    image.sprite = templateImage.sprite;
                    image.type = templateImage.type;
                    image.material = templateImage.material;
                    image.color = templateImage.color;
                    return;
                }
            }

            image.sprite = _inputPanelSkin.PanelSprite;
            image.type = _inputPanelSkin.PanelType;
            image.material = _inputPanelSkin.PanelMaterial;
            image.color = _inputPanelSkin.PanelColor;
        }

        private static InputPanelSkin CreateFallbackInputPanelSkin()
        {
            var skin = new InputPanelSkin
            {
                Font = Resources.GetBuiltinResource<Font>("Arial.ttf"),
                PanelSprite = null,
                PanelType = Image.Type.Sliced,
                PanelColor = new Color(0.02f, 0.28f, 0.44f, 0.88f),
                PanelMaterial = null,
                ButtonSprite = null,
                ButtonType = Image.Type.Sliced,
                ButtonColor = new Color(0.18f, 0.58f, 0.93f, 0.98f),
                ButtonMaterial = null,
                ButtonColors = ColorBlock.defaultColorBlock
            };

            ColorBlock colors = skin.ButtonColors;
            colors.normalColor = skin.ButtonColor;
            colors.highlightedColor = new Color(0.32f, 0.71f, 0.98f, 1f);
            colors.pressedColor = new Color(0.12f, 0.46f, 0.76f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.25f, 0.25f, 0.25f, 0.5f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            skin.ButtonColors = colors;

            return skin;
        }

        private static bool IsOwnInputUiObject(Transform tr)
        {
            Transform cursor = tr;
            while (cursor != null)
            {
                if (string.Equals(cursor.name, "MainGameSubtitleInputCanvas", StringComparison.Ordinal))
                {
                    return true;
                }

                cursor = cursor.parent;
            }

            return false;
        }

        private static string GetTransformPath(Transform tr)
        {
            if (tr == null)
            {
                return "(null)";
            }

            var names = new List<string>();
            Transform cursor = tr;
            while (cursor != null)
            {
                names.Add(cursor.name ?? string.Empty);
                cursor = cursor.parent;
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static RectTransform CreateUiRect(
            Transform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPosition,
            Vector2 sizeDelta)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
            return rect;
        }

        private static LayoutElement GetOrAddLayoutElement(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            LayoutElement element = go.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = go.AddComponent<LayoutElement>();
            }

            return element;
        }

        private static Text GetPreferredButtonText(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            Text[] texts = root.GetComponentsInChildren<Text>(true);
            Text preferred = null;
            int preferredScore = int.MinValue;
            for (int i = 0; i < texts.Length; i++)
            {
                Text text = texts[i];
                if (text == null)
                {
                    continue;
                }

                string nameText = text.name ?? string.Empty;
                int score = 0;
                if (string.Equals(nameText, "Text", StringComparison.OrdinalIgnoreCase) ||
                    nameText.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 20;
                }

                score += text.fontSize;
                if (text.alignment == TextAnchor.MiddleCenter || text.alignment == TextAnchor.MiddleLeft)
                {
                    score += 10;
                }

                if (score > preferredScore)
                {
                    preferredScore = score;
                    preferred = text;
                }
            }

            return preferred;
        }

        private Text CreateSimpleText(
            Transform parent,
            string name,
            string value,
            int fontSize,
            TextAnchor alignment,
            Color color)
        {
            RectTransform rect = CreateUiRect(
                parent,
                name,
                Vector2.zero,
                Vector2.one,
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                Vector2.zero);

            Text text = rect.gameObject.AddComponent<Text>();
            text.text = value ?? string.Empty;
            text.font = _inputPanelSkin.Font != null
                ? _inputPanelSkin.Font
                : Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = Mathf.Clamp(fontSize, 10, 64);
            text.alignment = alignment;
            text.color = color;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private Button CreateButton(Transform parent, string name, string label, Action onClick)
        {
            if (EnableTemplateControlClone && _buttonTemplate != null && _buttonTemplate.gameObject != null)
            {
                GameObject clone = UnityEngine.Object.Instantiate(_buttonTemplate.gameObject);
                clone.name = name;
                clone.transform.SetParent(parent, false);
                clone.transform.localScale = Vector3.one;
                clone.transform.localRotation = Quaternion.identity;

                RectTransform cloneRect = clone.GetComponent<RectTransform>();
                if (cloneRect != null)
                {
                    cloneRect.anchorMin = new Vector2(0.5f, 0.5f);
                    cloneRect.anchorMax = new Vector2(0.5f, 0.5f);
                    cloneRect.pivot = new Vector2(0.5f, 0.5f);
                    cloneRect.anchoredPosition = Vector2.zero;
                    cloneRect.sizeDelta = _buttonTemplateSize;
                }

                Button clonedButton = clone.GetComponent<Button>();
                if (clonedButton != null)
                {
                    clonedButton.onClick = new Button.ButtonClickedEvent();
                    if (onClick != null)
                    {
                        clonedButton.onClick.AddListener(() => onClick());
                    }
                }

                Text preferred = GetPreferredButtonText(clone.transform);
                ApplyButtonLabel(clone.transform, label, preferred);

                if (clonedButton != null)
                {
                    ApplyButtonVisual(clonedButton, false);
                    return clonedButton;
                }

                UnityEngine.Object.Destroy(clone);
            }

            RectTransform rect = CreateUiRect(
                parent,
                name,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                _buttonTemplateSize);

            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = _inputPanelSkin.ButtonSprite;
            image.type = _inputPanelSkin.ButtonType;
            image.color = _inputPanelSkin.ButtonColor;
            image.material = _inputPanelSkin.ButtonMaterial;

            Button button = rect.gameObject.AddComponent<Button>();
            button.colors = _inputPanelSkin.ButtonColors;

            Text labelText = CreateSimpleText(
                rect,
                "Label",
                label,
                20,
                TextAnchor.MiddleCenter,
                new Color(0.94f, 0.96f, 1f, 1f));
            labelText.raycastTarget = false;
            labelText.resizeTextForBestFit = false;
            labelText.horizontalOverflow = HorizontalWrapMode.Overflow;
            labelText.verticalOverflow = VerticalWrapMode.Truncate;

            if (onClick != null)
            {
                button.onClick.AddListener(() => onClick());
            }

            ApplyButtonVisual(button, false);
            return button;
        }

        private void ApplyButtonLabel(Transform buttonRoot, string label, Text preferredUnityText)
        {
            if (buttonRoot == null)
            {
                return;
            }

            string value = label ?? string.Empty;

            Text[] unityTexts = buttonRoot.GetComponentsInChildren<Text>(true);
            Text unityTarget = preferredUnityText;
            if (unityTarget == null && unityTexts.Length > 0)
            {
                unityTarget = unityTexts[0];
            }

            for (int i = 0; i < unityTexts.Length; i++)
            {
                Text t = unityTexts[i];
                if (t == null)
                {
                    continue;
                }

                t.text = t == unityTarget ? value : string.Empty;
                t.raycastTarget = false;
                if (t == unityTarget && _inputPanelSkin.Font != null)
                {
                    t.font = _inputPanelSkin.Font;
                }
            }

            Component[] tmpTexts = GetTmpTextComponents(buttonRoot);
            Component tmpTarget = null;
            if (unityTarget == null && tmpTexts.Length > 0)
            {
                tmpTarget = SelectPreferredTmpText(tmpTexts);
            }
            for (int i = 0; i < tmpTexts.Length; i++)
            {
                Component component = tmpTexts[i];
                if (component == null)
                {
                    continue;
                }

                SetTmpText(component, component == tmpTarget ? value : string.Empty);
                SetTmpRaycast(component, false);
            }

            if (unityTarget == null && tmpTarget == null)
            {
                Text runtimeLabel = CreateSimpleText(
                    buttonRoot,
                    "MainGameSubtitleRuntimeLabel",
                    value,
                    20,
                    TextAnchor.MiddleCenter,
                    new Color(0.94f, 0.96f, 1f, 1f));
                runtimeLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
                runtimeLabel.verticalOverflow = VerticalWrapMode.Truncate;
                runtimeLabel.supportRichText = false;
                runtimeLabel.resizeTextForBestFit = false;
                runtimeLabel.raycastTarget = false;
            }
        }

        private static Component[] GetTmpTextComponents(Transform root)
        {
            if (root == null)
            {
                return Array.Empty<Component>();
            }

            Component[] components = root.GetComponentsInChildren<Component>(true);
            var matches = new List<Component>(4);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component is Text)
                {
                    continue;
                }

                string fullName = component.GetType().FullName ?? string.Empty;
                if (fullName.IndexOf("TMPro.", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                matches.Add(component);
            }

            return matches.ToArray();
        }

        private static Component SelectPreferredTmpText(Component[] tmpTexts)
        {
            if (tmpTexts == null || tmpTexts.Length == 0)
            {
                return null;
            }

            Component preferred = null;
            int bestScore = int.MinValue;
            for (int i = 0; i < tmpTexts.Length; i++)
            {
                Component component = tmpTexts[i];
                if (component == null)
                {
                    continue;
                }

                int score = 0;
                string nameText = component.name ?? string.Empty;
                if (string.Equals(nameText, "Text", StringComparison.OrdinalIgnoreCase))
                {
                    score += 40;
                }
                if (nameText.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nameText.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 20;
                }
                if (component.gameObject.activeInHierarchy)
                {
                    score += 10;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    preferred = component;
                }
            }

            return preferred ?? tmpTexts[0];
        }

        private static void SetTmpText(Component component, string value)
        {
            if (component == null)
            {
                return;
            }

            PropertyInfo textProperty = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            if (textProperty != null &&
                textProperty.CanWrite &&
                textProperty.PropertyType == typeof(string))
            {
                textProperty.SetValue(component, value ?? string.Empty, null);
            }
        }

        private static void SetTmpRaycast(Component component, bool enabled)
        {
            if (component == null)
            {
                return;
            }

            PropertyInfo raycastProperty = component.GetType().GetProperty("raycastTarget", BindingFlags.Instance | BindingFlags.Public);
            if (raycastProperty != null &&
                raycastProperty.CanWrite &&
                raycastProperty.PropertyType == typeof(bool))
            {
                raycastProperty.SetValue(component, enabled, null);
            }
        }

        private void ApplyButtonVisual(Button button, bool highlighted)
        {
            if (button == null)
            {
                return;
            }

            bool usingTemplateClone = EnableTemplateControlClone && _buttonTemplate != null;
            if (!usingTemplateClone)
            {
                Image image = button.GetComponent<Image>();
                if (image != null)
                {
                    image.sprite = _inputPanelSkin.ButtonSprite;
                    image.type = _inputPanelSkin.ButtonType;
                    image.material = _inputPanelSkin.ButtonMaterial;
                    image.color = highlighted
                        ? Color.Lerp(_inputPanelSkin.ButtonColor, Color.white, 0.22f)
                        : _inputPanelSkin.ButtonColor;
                }
            }

            ColorBlock colors = button.colors;
            if (highlighted)
            {
                colors.normalColor = Color.Lerp(colors.normalColor, Color.white, 0.18f);
                colors.selectedColor = colors.normalColor;
            }
            else if (!usingTemplateClone)
            {
                colors = _inputPanelSkin.ButtonColors;
            }
            button.colors = colors;
        }

        private InputField CreateInputField(Transform parent, string name)
        {
            if (EnableTemplateControlClone && _inputFieldTemplate != null && _inputFieldTemplate.gameObject != null)
            {
                GameObject clone = UnityEngine.Object.Instantiate(_inputFieldTemplate.gameObject);
                clone.name = name;
                clone.transform.SetParent(parent, false);
                clone.transform.localScale = Vector3.one;
                clone.transform.localRotation = Quaternion.identity;

                RectTransform cloneRect = clone.GetComponent<RectTransform>();
                if (cloneRect != null)
                {
                    cloneRect.anchorMin = new Vector2(0.5f, 0.5f);
                    cloneRect.anchorMax = new Vector2(0.5f, 0.5f);
                    cloneRect.pivot = new Vector2(0.5f, 0.5f);
                    cloneRect.anchoredPosition = Vector2.zero;
                    cloneRect.sizeDelta = _inputFieldTemplateSize;
                }

                InputField templateField = clone.GetComponent<InputField>();
                if (templateField != null)
                {
                    templateField.text = string.Empty;
                    templateField.onValueChanged = new InputField.OnChangeEvent();
                    templateField.onEndEdit = new InputField.SubmitEvent();
                    templateField.lineType = InputField.LineType.SingleLine;
                    templateField.contentType = InputField.ContentType.Standard;

                    int templateFontSize = _settings != null ? Mathf.Clamp(_settings.InputPanelInputFontSize, 14, 48) : 22;
                    if (templateField.textComponent != null)
                    {
                        if (_inputPanelSkin.Font != null)
                        {
                            templateField.textComponent.font = _inputPanelSkin.Font;
                        }

                        templateField.textComponent.fontSize = templateFontSize;
                        templateField.textComponent.alignment = TextAnchor.MiddleLeft;
                        templateField.textComponent.horizontalOverflow = HorizontalWrapMode.Overflow;
                        templateField.textComponent.verticalOverflow = VerticalWrapMode.Truncate;
                    }

                    Text templatePlaceholder = templateField.placeholder as Text;
                    if (templatePlaceholder != null)
                    {
                        templatePlaceholder.text = "字幕を入力...";
                        templatePlaceholder.fontSize = Mathf.Max(12, templateFontSize - 2);
                        templatePlaceholder.alignment = TextAnchor.MiddleLeft;
                        if (_inputPanelSkin.Font != null)
                        {
                            templatePlaceholder.font = _inputPanelSkin.Font;
                        }
                    }

                    return templateField;
                }

                UnityEngine.Object.Destroy(clone);
            }

            RectTransform rect = CreateUiRect(
                parent,
                name,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                _inputFieldTemplateSize);

            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = _inputPanelSkin.ButtonSprite;
            image.type = _inputPanelSkin.ButtonType;
            image.material = _inputPanelSkin.ButtonMaterial;
            image.color = new Color(0f, 0f, 0f, 0.48f);
            Outline frame = rect.gameObject.AddComponent<Outline>();
            frame.effectColor = new Color(0.4f, 0.9f, 1f, 0.9f);
            frame.effectDistance = new Vector2(1f, -1f);

            InputField field = rect.gameObject.AddComponent<InputField>();
            field.lineType = InputField.LineType.SingleLine;
            field.contentType = InputField.ContentType.Standard;

            RectTransform textArea = CreateUiRect(
                rect,
                "TextArea",
                Vector2.zero,
                Vector2.one,
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                Vector2.zero);
            textArea.offsetMin = new Vector2(10f, 5f);
            textArea.offsetMax = new Vector2(-10f, -5f);

            int fontSize = _settings != null ? Mathf.Clamp(_settings.InputPanelInputFontSize, 14, 48) : 22;

            Text text = CreateSimpleText(
                textArea,
                "Text",
                string.Empty,
                fontSize,
                TextAnchor.MiddleLeft,
                new Color(1f, 1f, 1f, 1f));
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;

            Text placeholder = CreateSimpleText(
                textArea,
                "Placeholder",
                "字幕を入力...",
                Mathf.Max(12, fontSize - 2),
                TextAnchor.MiddleLeft,
                new Color(1f, 1f, 1f, 0.35f));
            placeholder.raycastTarget = false;
            placeholder.fontStyle = FontStyle.Italic;

            field.textComponent = text;
            field.placeholder = placeholder;
            return field;
        }

        private void BuildListColumn(
            Transform parent,
            string title,
            out Text headerText,
            out RectTransform content)
        {
            RectTransform column = CreateUiRect(
                parent,
                title + "Column",
                Vector2.zero,
                Vector2.one,
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                Vector2.zero);

            LayoutElement columnElement = column.gameObject.AddComponent<LayoutElement>();
            columnElement.flexibleWidth = 1f;
            columnElement.flexibleHeight = 1f;
            columnElement.minWidth = 180f;

            VerticalLayoutGroup columnLayout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            columnLayout.padding = new RectOffset(0, 0, 0, 0);
            columnLayout.spacing = 6f;
            columnLayout.childControlWidth = true;
            columnLayout.childControlHeight = true;
            columnLayout.childForceExpandWidth = true;
            columnLayout.childForceExpandHeight = false;

            headerText = CreateSimpleText(
                column,
                title + "Header",
                title,
                17,
                TextAnchor.MiddleLeft,
                new Color(0.94f, 0.96f, 1f, 0.98f));
            LayoutElement headerElement = headerText.gameObject.AddComponent<LayoutElement>();
            headerElement.preferredHeight = 26f;

            RectTransform listRoot = CreateUiRect(
                column,
                title + "ListRoot",
                Vector2.zero,
                Vector2.one,
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                Vector2.zero);
            LayoutElement listRootElement = listRoot.gameObject.AddComponent<LayoutElement>();
            listRootElement.flexibleHeight = 1f;
            listRootElement.minHeight = 180f;
            Image viewportImage = listRoot.gameObject.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.16f);
            Mask viewportMask = listRoot.gameObject.AddComponent<Mask>();
            viewportMask.showMaskGraphic = false;

            ScrollRect scroll = listRoot.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
            scroll.inertia = true;

            content = CreateUiRect(
                listRoot,
                "Content",
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0.5f, 1f),
                Vector2.zero,
                new Vector2(0f, 0f));

            VerticalLayoutGroup contentLayout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            contentLayout.padding = new RectOffset(4, 4, 4, 4);
            contentLayout.spacing = 6f;
            contentLayout.childControlWidth = true;
            contentLayout.childControlHeight = true;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;

            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = listRoot;
            scroll.content = content;
            scroll.verticalNormalizedPosition = 1f;
        }

        private void RebuildInputLists()
        {
            if (_historyHeaderText != null)
            {
                _historyHeaderText.text = $"履歴 ({_inputHistory.Count})";
            }

            if (_presetHeaderText != null)
            {
                _presetHeaderText.text = $"プリセット ({_inputPresets.Count})";
            }

            if (_historyContent != null)
            {
                RebuildListContent(_historyContent, _inputHistory, false);
            }

            if (_presetContent != null)
            {
                RebuildListContent(_presetContent, _inputPresets, true);
            }
        }

        private void RebuildListContent(RectTransform content, List<string> values, bool isPreset)
        {
            if (content == null)
            {
                return;
            }

            for (int i = content.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(content.GetChild(i).gameObject);
            }

            if (values == null)
            {
                return;
            }

            bool hasAny = false;
            for (int i = 0; i < values.Count; i++)
            {
                string item = values[i];
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }
                hasAny = true;

                string captured = item;

                RectTransform row = CreateUiRect(
                    content,
                    "Row_" + i,
                    Vector2.zero,
                    Vector2.one,
                    new Vector2(0.5f, 0.5f),
                    Vector2.zero,
                    Vector2.zero);
                LayoutElement rowElement = row.gameObject.AddComponent<LayoutElement>();
                float rowHeight = Mathf.Clamp(_buttonTemplateSize.y > 1f ? _buttonTemplateSize.y * 0.65f : 38f, 34f, 56f);
                rowElement.preferredHeight = rowHeight;

                HorizontalLayoutGroup rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                rowLayout.spacing = 5f;
                rowLayout.childControlWidth = true;
                rowLayout.childControlHeight = true;
                rowLayout.childForceExpandWidth = false;
                rowLayout.childForceExpandHeight = true;

                Button valueButton = CreateButton(row, "ValueButton", captured, () =>
                {
                    if (_inputField != null)
                    {
                        _inputField.text = captured;
                        _focusInputNextFrame = true;
                        SetActiveInputSection(InputPanelSection.None, focusInput: true);
                    }
                });
                LayoutElement valueButtonElement = GetOrAddLayoutElement(valueButton.gameObject);
                valueButtonElement.flexibleWidth = 1f;
                valueButtonElement.minWidth = 120f;
                Text valueText = valueButton.GetComponentInChildren<Text>();
                if (valueText != null)
                {
                    valueText.alignment = TextAnchor.MiddleLeft;
                    valueText.horizontalOverflow = HorizontalWrapMode.Overflow;
                    valueText.verticalOverflow = VerticalWrapMode.Truncate;
                }

                Button sendButton = CreateButton(row, "SendButton", "送信", () => SendTextFromInputPanel(captured));
                LayoutElement rowSendLayout = GetOrAddLayoutElement(sendButton.gameObject);
                rowSendLayout.preferredWidth = 88f;
                rowSendLayout.preferredHeight = 32f;

                Button removeButton = CreateButton(row, "RemoveButton", "削除", () =>
                {
                    if (isPreset)
                    {
                        _inputPresets.RemoveAll(x => string.Equals(x, captured, StringComparison.Ordinal));
                    }
                    else
                    {
                        _inputHistory.RemoveAll(x => string.Equals(x, captured, StringComparison.Ordinal));
                    }

                    SaveInputPanelState();
                    RebuildInputLists();
                });
                LayoutElement rowRemoveLayout = GetOrAddLayoutElement(removeButton.gameObject);
                rowRemoveLayout.preferredWidth = 88f;
                rowRemoveLayout.preferredHeight = 32f;
            }

            if (!hasAny)
            {
                RectTransform emptyRow = CreateUiRect(
                    content,
                    isPreset ? "PresetEmpty" : "HistoryEmpty",
                    Vector2.zero,
                    Vector2.one,
                    new Vector2(0.5f, 0.5f),
                    Vector2.zero,
                    Vector2.zero);

                LayoutElement emptyRowElement = emptyRow.gameObject.AddComponent<LayoutElement>();
                emptyRowElement.preferredHeight = 30f;

                Text emptyText = CreateSimpleText(
                    emptyRow,
                    "EmptyLabel",
                    isPreset ? "（プリセットなし）" : "（履歴なし）",
                    15,
                    TextAnchor.MiddleLeft,
                    new Color(0.9f, 0.95f, 1f, 0.85f));
                emptyText.raycastTarget = false;
            }
        }

        private static bool IsConfiguredKeyDown(string keyText)
        {
            if (string.IsNullOrWhiteSpace(keyText))
            {
                return false;
            }

            if (!Enum.TryParse(keyText.Trim(), ignoreCase: true, out KeyCode key))
            {
                return false;
            }

            if (key == KeyCode.None)
            {
                return false;
            }

            return Input.GetKeyDown(key);
        }

        private static bool IsHSceneActiveCached()
        {
            float now = Time.unscaledTime;
            if (now < _hSceneCachedUntil)
            {
                return _isHSceneCached;
            }

            _isHSceneCached = IsHSceneActiveNow();
            _hSceneCachedUntil = now + 0.25f;
            return _isHSceneCached;
        }

        private static bool IsHSceneActiveNow()
        {
            if (HSceneProcType != null && UnityEngine.Object.FindObjectOfType(HSceneProcType) != null)
            {
                return true;
            }

            if (VRHSceneType != null && UnityEngine.Object.FindObjectOfType(VRHSceneType) != null)
            {
                return true;
            }

            return false;
        }

        private void SendInputDraft()
        {
            if (_inputField == null)
            {
                if (_settings != null && _settings.VerboseLog)
                {
                    LogWarn("input panel send ignored: input field is null");
                }
                return;
            }

            string text = (_inputField.text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
            {
                if (_settings != null && _settings.VerboseLog)
                {
                    Log("input panel send ignored: empty text");
                }
                return;
            }

            if (_settings != null && _settings.VerboseLog)
            {
                Log("input panel send clicked: len=" + text.Length);
            }
            SendTextFromInputPanel(text);
            ClearInputDraft();
        }

        private void ClearInputDraft()
        {
            if (_inputField == null)
            {
                return;
            }

            _inputField.text = string.Empty;
            _focusInputNextFrame = true;
        }

        private void SendTextFromInputPanel(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            string normalized = text.Trim();
            try
            {
                DisplayBackendMode backend = ParseDisplayBackend(_settings.InputPanelSendBackend);
                string modeText = string.IsNullOrWhiteSpace(_settings.InputPanelSendDisplayMode)
                    ? _settings.DisplayMode
                    : _settings.InputPanelSendDisplayMode;
                float holdSeconds = Mathf.Clamp(_settings.InputPanelSendHoldSeconds, 0.1f, 600f);

                if (_settings != null && _settings.VerboseLog)
                {
                    Log(
                        "input panel dispatch: backend=" + backend +
                        ", mode=" + modeText +
                        ", hold=" + holdSeconds.ToString("0.00") +
                        ", len=" + normalized.Length);
                }
                ShowSubtitleCore(normalized, holdSeconds, backend, modeText, "input-panel");
                TryForwardInputText(normalized);
                PushUniqueFirst(_inputHistory, normalized, _settings.InputPanelHistoryMax);
                SaveInputPanelState();
                RebuildInputLists();
            }
            catch (Exception ex)
            {
                LogError("input panel send failed: " + ex.Message);
            }
            finally
            {
                SetInputPanelVisible(false);
            }
        }

        private void TryForwardInputText(string text)
        {
            if (_settings == null || !_settings.InputPanelForwardEnabled)
            {
                if (_settings != null && _settings.VerboseLog)
                {
                    Log("input panel forward skipped: disabled");
                }
                return;
            }

            string normalized = (text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                return;
            }

            string host = string.IsNullOrWhiteSpace(_settings.InputPanelForwardHost)
                ? "127.0.0.1"
                : _settings.InputPanelForwardHost.Trim();
            int port = Mathf.Clamp(_settings.InputPanelForwardPort, 1, 65535);
            string endpoint = NormalizeHttpEndpoint(_settings.InputPanelForwardEndpoint, "/manual-text");
            int timeoutMs = Mathf.Clamp(_settings.InputPanelForwardTimeoutMs, 100, 15000);
            string token = _settings.InputPanelForwardToken ?? string.Empty;
            string source = string.IsNullOrWhiteSpace(_settings.InputPanelForwardSource)
                ? "subtitle-ui"
                : _settings.InputPanelForwardSource.Trim();
            if (_settings != null && _settings.VerboseLog)
            {
                Log("input panel forward begin: http://" + host + ":" + port + endpoint + ", len=" + normalized.Length);
            }

            var payload = new ForwardTextPayload
            {
                text = normalized,
                source = source,
                event_id = Guid.NewGuid().ToString("N"),
                sent_at_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string url = $"http://{host}:{port}{endpoint}";
                    string json = BuildForwardTextJson(payload);
                    byte[] body = Utf8NoBom.GetBytes(json);

                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "POST";
                    request.ContentType = "application/json; charset=utf-8";
                    request.Timeout = timeoutMs;
                    request.ReadWriteTimeout = timeoutMs;
                    request.ContentLength = body.Length;
                    request.Proxy = null;
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        request.Headers["X-Auth-Token"] = token.Trim();
                    }

                    using (Stream reqStream = request.GetRequestStream())
                    {
                        reqStream.Write(body, 0, body.Length);
                    }

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        if (_settings != null && _settings.VerboseLog)
                        {
                            Log("input panel forward ok: status=" + (int)response.StatusCode);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogWarn("input panel forward failed: " + ex.Message);
                }
            });
        }

        private static string NormalizeHttpEndpoint(string endpoint, string fallback)
        {
            string value = string.IsNullOrWhiteSpace(endpoint) ? fallback : endpoint.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                value = fallback;
            }

            if (!value.StartsWith("/"))
            {
                value = "/" + value;
            }

            return value;
        }

        private static string BuildForwardTextJson(ForwardTextPayload payload)
        {
            if (payload == null)
            {
                return "{\"text\":\"\",\"source\":\"\",\"event_id\":\"\",\"sent_at_unix_ms\":0}";
            }

            string text = EscapeJsonString(payload.text ?? string.Empty);
            string source = EscapeJsonString(payload.source ?? string.Empty);
            string eventId = EscapeJsonString(payload.event_id ?? string.Empty);
            return "{\"text\":\"" + text +
                   "\",\"source\":\"" + source +
                   "\",\"event_id\":\"" + eventId +
                   "\",\"sent_at_unix_ms\":" + payload.sent_at_unix_ms.ToString() + "}";
        }

        private static string EscapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)ch).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(ch);
                        }
                        break;
                }
            }

            return sb.ToString();
        }

        private void AddPresetFromDraft()
        {
            if (_inputField == null)
            {
                return;
            }

            string text = (_inputField.text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            PushUniqueFirst(_inputPresets, text, _settings.InputPanelPresetMax);
            SaveInputPanelState();
            RebuildInputLists();
            _focusInputNextFrame = true;
        }

        private static void PushUniqueFirst(List<string> list, string text, int max)
        {
            if (list == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            string normalized = text.Trim();
            list.RemoveAll(x => string.Equals(x, normalized, StringComparison.Ordinal));
            list.Insert(0, normalized);
            while (list.Count > Mathf.Max(1, max))
            {
                list.RemoveAt(list.Count - 1);
            }
        }

        private void ReloadInputPanelState()
        {
            if (string.IsNullOrEmpty(_inputPanelStatePath) || _settings == null)
            {
                return;
            }

            InputPanelState state = InputPanelStateStore.LoadOrCreate(
                _inputPanelStatePath,
                _settings.InputPanelHistoryMax,
                _settings.InputPanelPresetMax,
                Log,
                LogWarn,
                LogError);

            _inputHistory.Clear();
            _inputPresets.Clear();
            if (state != null)
            {
                if (state.History != null)
                {
                    _inputHistory.AddRange(state.History);
                }

                if (state.Presets != null)
                {
                    _inputPresets.AddRange(state.Presets);
                }
            }

            RebuildInputLists();
        }

        private void SaveInputPanelState()
        {
            if (string.IsNullOrEmpty(_inputPanelStatePath) || _settings == null)
            {
                return;
            }

            var state = new InputPanelState
            {
                History = new List<string>(_inputHistory),
                Presets = new List<string>(_inputPresets)
            };
            state.Normalize(_settings.InputPanelHistoryMax, _settings.InputPanelPresetMax);
            InputPanelStateStore.Save(_inputPanelStatePath, state);
        }

        private void BindRuntimeConfig()
        {
            int defaultFontSize = _settings?.OverlayFontSize ?? 36;
            float defaultHoldSeconds = _settings?.HoldSeconds ?? 10f;

            _cfgOverlayFontSize = Config.Bind(
                "Overlay",
                "FontSize",
                defaultFontSize,
                new ConfigDescription(
                    "字幕フォントサイズ（Overlay）。",
                    new AcceptableValueRange<int>(12, 96)));

            _cfgDefaultHoldSeconds = Config.Bind(
                "Subtitle",
                "HoldSeconds",
                defaultHoldSeconds,
                new ConfigDescription(
                    "字幕の表示秒数。",
                    new AcceptableValueRange<float>(0.1f, 600f)));

            _cfgFemaleKeepVisibleUntilNext = Config.Bind(
                "FemaleSubtitle",
                "KeepVisibleUntilNext",
                false,
                "女性字幕を長めに保持する。次字幕まで残したい場合はON。");

            _cfgFemaleMaxHoldSeconds = Config.Bind(
                "FemaleSubtitle",
                "MaxHoldSeconds",
                30f,
                new ConfigDescription(
                    "女性字幕の最大保持秒数。",
                    new AcceptableValueRange<float>(1f, 600f)));

            _cfgOverlayFontSize.SettingChanged += (_, __) => ApplyRuntimeConfigOverrides();
            _cfgDefaultHoldSeconds.SettingChanged += (_, __) => ApplyRuntimeConfigOverrides();
            _cfgFemaleKeepVisibleUntilNext.SettingChanged += (_, __) => ApplyRuntimeConfigOverrides();
            _cfgFemaleMaxHoldSeconds.SettingChanged += (_, __) => ApplyRuntimeConfigOverrides();
        }

        private void ApplyRuntimeConfigOverrides()
        {
            if (_settings == null)
            {
                return;
            }

            if (_cfgOverlayFontSize != null)
            {
                _settings.OverlayFontSize = Mathf.Clamp(_cfgOverlayFontSize.Value, 12, 96);
            }

            if (_cfgDefaultHoldSeconds != null)
            {
                _settings.HoldSeconds = Mathf.Clamp(_cfgDefaultHoldSeconds.Value, 0.1f, 600f);
            }

            if (_cfgFemaleKeepVisibleUntilNext != null)
            {
                _femaleKeepVisibleUntilNext = _cfgFemaleKeepVisibleUntilNext.Value;
            }

            if (_cfgFemaleMaxHoldSeconds != null)
            {
                _femaleMaxHoldSeconds = Mathf.Clamp(_cfgFemaleMaxHoldSeconds.Value, 1f, 600f);
            }
        }

        private void ReloadSettings()
        {
            _settings = SettingsStore.LoadOrCreate(_pluginDir, Log, LogWarn, LogError);
            _displayBackend = ParseDisplayBackend(_settings?.DisplayBackend);
            _overlayTextColor = ParseColor(_settings?.OverlayTextColor, Color.white, "OverlayTextColor");
            _overlayShadowColor = ParseColor(_settings?.OverlayShadowColor, Color.black, "OverlayShadowColor");
            _overlayIntroHighlightColor = ParseColor(_settings?.OverlayIntroHighlightColor, _overlayTextColor, "OverlayIntroHighlightColor");
            _overlayStyle = null;
            _overlayShadowStyle = null;
            _overlayResolvedFont = null;
            _overlayResolvedFontSource = string.Empty;

            ApplyRuntimeConfigOverrides();

            Log(
                $"settings loaded: enabled={_settings?.Enabled}, " +
                $"backend={_displayBackend}, mode={_settings?.DisplayMode}, hold={_settings?.HoldSeconds:0.00}s");
        }

        private static DisplayBackendMode ParseDisplayBackend(string backendText)
        {
            if (Enum.TryParse(backendText, ignoreCase: true, out DisplayBackendMode parsed))
            {
                return parsed;
            }

            if (!string.IsNullOrWhiteSpace(backendText))
            {
                LogWarn("invalid DisplayBackend, fallback to Auto: " + backendText);
            }
            return DisplayBackendMode.Auto;
        }

        private static Color ParseColor(string text, Color fallback, string settingName)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            if (ColorUtility.TryParseHtmlString(text, out Color parsed))
            {
                return parsed;
            }

            LogWarn("invalid " + settingName + ", fallback used: " + text);
            return fallback;
        }

        public bool EnqueueSubtitle(SubtitleRequest request, out string reason)
        {
            if (request == null)
            {
                reason = "request is null";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.Text))
            {
                reason = "text is empty";
                return false;
            }

            SubtitleRequest clone = request.Clone();
            lock (_externalRequestsLock)
            {
                if (_externalRequests.Count >= 128)
                {
                    _externalRequests.Dequeue();
                }

                _externalRequests.Enqueue(clone);
            }

            if (_settings != null && _settings.VerboseLog)
            {
                Log($"external enqueue: text={clone.Text}");
            }

            reason = null;
            return true;
        }

        private void DrainExternalRequests(int maxPerFrame)
        {
            for (int i = 0; i < maxPerFrame; i++)
            {
                SubtitleRequest req = null;
                lock (_externalRequestsLock)
                {
                    if (_externalRequests.Count > 0)
                    {
                        req = _externalRequests.Dequeue();
                    }
                }

                if (req == null)
                {
                    return;
                }

                string text = req.Text;
                float hold = req.HoldSeconds ?? _settings.HoldSeconds;
                string modeText = string.IsNullOrWhiteSpace(req.DisplayMode) ? _settings.DisplayMode : req.DisplayMode;
                DisplayBackendMode backend = req.Backend.HasValue ? ToInternalBackend(req.Backend.Value) : _displayBackend;

                ShowSubtitleCore(text, hold, backend, modeText, "external-api");
            }
        }

        private static DisplayBackendMode ToInternalBackend(SubtitleBackend backend)
        {
            switch (backend)
            {
                case SubtitleBackend.InformationUI:
                    return DisplayBackendMode.InformationUI;
                case SubtitleBackend.Overlay:
                    return DisplayBackendMode.Overlay;
                default:
                    return DisplayBackendMode.Auto;
            }
        }

        private static void ShowSubtitleCore(
            string text,
            float holdSeconds,
            DisplayBackendMode backend,
            string displayModeText,
            string source)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                LogWarn("subtitle text is empty; source=" + source);
                return;
            }

            if (_femaleKeepVisibleUntilNext && IsFemaleSubtitle(displayModeText, text))
            {
                holdSeconds = Mathf.Max(holdSeconds, _femaleMaxHoldSeconds);
            }

            holdSeconds = Mathf.Clamp(holdSeconds, 0.1f, 600f);

            if (backend == DisplayBackendMode.Overlay)
            {
                ShowOverlay(text, holdSeconds, displayModeText, source + ":overlay");
                return;
            }

            if (TryShowWithInformationUI(text, holdSeconds, displayModeText, logMissing: backend == DisplayBackendMode.InformationUI))
            {
                return;
            }

            if (backend == DisplayBackendMode.Auto)
            {
                ShowOverlay(text, holdSeconds, displayModeText, source + ":auto-fallback");
                LogWarn("InformationUI unavailable; fallback to overlay");
                return;
            }

            LogWarn("InformationUI unavailable and backend=InformationUI; no fallback");
        }

        private static bool TryShowWithInformationUI(
            string text,
            float holdSeconds,
            string displayModeText,
            bool logMissing)
        {
            if (!SingletonInitializer<InformationUI>.initialized)
            {
                if (logMissing)
                {
                    LogWarn("InformationUI not initialized yet");
                }
                return false;
            }

            InformationUI ui = SingletonInitializer<InformationUI>.instance;
            if (ui == null)
            {
                if (logMissing)
                {
                    LogWarn("InformationUI instance is null");
                }
                return false;
            }

            if (WaitTimeField != null)
            {
                WaitTimeField.SetValue(ui, holdSeconds);
            }

            InformationUI.Mode mode = ParseMode(displayModeText);
            InformationUI.Set(text, mode);

            if (_settings.VerboseLog)
            {
                Log($"subtitle shown by InformationUI: mode={mode}, hold={holdSeconds:0.00}, text={text}");
            }

            return true;
        }

        private static bool IsFemaleSubtitle(string displayModeText, string text)
        {
            string mode = (displayModeText ?? string.Empty).Trim();
            if (string.Equals(mode, "StackFemale", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "Female", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (TryExtractFirstColorToken(text, out string colorToken) && IsFemaleColorToken(colorToken))
            {
                return true;
            }

            return false;
        }

        private static bool TryExtractFirstColorToken(string text, out string colorToken)
        {
            colorToken = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            Match match = OverlayColorValueTagRegex.Match(text);
            if (match == null || !match.Success)
            {
                return false;
            }

            Group valueGroup = match.Groups["value"];
            if (valueGroup == null || !valueGroup.Success)
            {
                return false;
            }

            colorToken = NormalizeColorToken(valueGroup.Value);
            return !string.IsNullOrWhiteSpace(colorToken);
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

            if (value.Length == 4 || value.Length == 5)
            {
                char r = value[1];
                char g = value[2];
                char b = value[3];
                return "#" + r + r + g + g + b + b;
            }

            return value;
        }

        private static bool IsFemaleColorToken(string token)
        {
            return token == "pink" ||
                token == "hotpink" ||
                token == "#ffc0cb" ||
                token == "#ff69b4" ||
                token == "#ffb6c1";
        }

        private static void ShowOverlay(string text, float holdSeconds, string displayModeText, string source)
        {
            float now = Time.unscaledTime;
            float until = now + holdSeconds;
            string mode = (displayModeText ?? string.Empty).Trim();
            bool stackMale = string.Equals(mode, "StackMale", StringComparison.OrdinalIgnoreCase);
            bool stackFemale = string.Equals(mode, "StackFemale", StringComparison.OrdinalIgnoreCase);

            if (stackFemale)
            {
                if (!string.IsNullOrWhiteSpace(_overlayText) && _overlayUntil > now)
                {
                    _overlayUpperText = _overlayText;
                    _overlayUpperUntil = Mathf.Max(_overlayUntil, until);
                }
                else
                {
                    _overlayUpperText = string.Empty;
                    _overlayUpperUntil = 0f;
                }

                _overlayText = text ?? string.Empty;
                _overlayUntil = until;
            }
            else
            {
                // Normal and StackMale both reset to a single active line.
                _overlayUpperText = string.Empty;
                _overlayUpperUntil = 0f;
                _overlayText = text ?? string.Empty;
                _overlayUntil = until;
            }

            _overlayStartedAt = now;
            EnsureOverlayResources();

            if (_settings != null && _settings.VerboseLog)
            {
                Log(
                    $"subtitle shown by overlay ({source}): hold={holdSeconds:0.00}, " +
                    $"mode={mode}, text={_overlayText}");
            }
        }

        private static void EnsureOverlayResources()
        {
            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.label);
            }

            if (_overlayShadowStyle == null)
            {
                _overlayShadowStyle = new GUIStyle(GUI.skin.label);
            }

            _overlayStyle.alignment = TextAnchor.MiddleCenter;
            _overlayStyle.wordWrap = true;
            _overlayStyle.richText = true;
            _overlayStyle.fontSize = _settings?.OverlayFontSize ?? 28;
            _overlayStyle.normal.textColor = _overlayTextColor;
            _overlayStyle.fontStyle = FontStyle.Normal;

            _overlayShadowStyle.alignment = _overlayStyle.alignment;
            _overlayShadowStyle.wordWrap = _overlayStyle.wordWrap;
            _overlayShadowStyle.richText = _overlayStyle.richText;
            _overlayShadowStyle.fontSize = _overlayStyle.fontSize;
            _overlayShadowStyle.fontStyle = _overlayStyle.fontStyle;
            _overlayShadowStyle.normal.textColor = _overlayShadowColor;
            _overlayShadowStyle.font = _overlayStyle.font;

            if (_settings != null && _settings.OverlayUseConversationFont)
            {
                Font font = ResolveConversationFont();
                if (font != null)
                {
                    _overlayStyle.font = font;
                    _overlayShadowStyle.font = font;
                }
            }
        }

        private static Font ResolveConversationFont()
        {
            if (_overlayResolvedFont != null)
            {
                if (!string.IsNullOrWhiteSpace(_overlayResolvedFontSource) &&
                    _overlayResolvedFontSource.IndexOf("MainGameSubtitleInputCanvas", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _overlayResolvedFont = null;
                    _overlayResolvedFontSource = string.Empty;
                }
                else
                {
                    return _overlayResolvedFont;
                }
            }

            if (_overlayResolvedFont != null)
            {
                return _overlayResolvedFont;
            }

            TextController[] controllers = Resources.FindObjectsOfTypeAll<TextController>();
            for (int i = 0; i < controllers.Length; i++)
            {
                TextController controller = controllers[i];
                if (controller == null)
                {
                    continue;
                }

                Text message = controller.MessageWindow;
                if (message != null && message.font != null)
                {
                    CacheResolvedFont(message.font, "TextController.MessageWindow");
                    return _overlayResolvedFont;
                }
            }

            Text[] activeTexts = UnityEngine.Object.FindObjectsOfType<Text>();
            Text best = ChooseBestConversationText(activeTexts);
            if (best != null && best.font != null)
            {
                CacheResolvedFont(best.font, "SceneActiveText:" + BuildObjectPath(best.transform));
                return _overlayResolvedFont;
            }

            Text[] allTexts = Resources.FindObjectsOfTypeAll<Text>();
            best = ChooseBestConversationText(allTexts);
            if (best != null && best.font != null)
            {
                CacheResolvedFont(best.font, "SceneAllText:" + BuildObjectPath(best.transform));
                return _overlayResolvedFont;
            }

            return null;
        }

        private static Text ChooseBestConversationText(Text[] texts)
        {
            Text best = null;
            int bestScore = int.MinValue;

            for (int i = 0; i < texts.Length; i++)
            {
                Text t = texts[i];
                if (t == null || t.font == null)
                {
                    continue;
                }

                if (IsOwnInputUiObject(t.transform))
                {
                    continue;
                }

                int score = ScoreTextCandidate(t);
                if (best == null || score > bestScore)
                {
                    best = t;
                    bestScore = score;
                }
            }

            return best;
        }

        private static int ScoreTextCandidate(Text t)
        {
            int score = 0;
            if (t.gameObject.activeInHierarchy)
            {
                score += 100;
            }

            string path = BuildObjectPath(t.transform).ToLowerInvariant();
            score += ContainsScore(path, "message", 500);
            score += ContainsScore(path, "talk", 400);
            score += ContainsScore(path, "name", 220);
            score += ContainsScore(path, "window", 200);
            score += ContainsScore(path, "subtitle", 350);
            score += ContainsScore(path, "textscenario", 450);
            score += ContainsScore(path, "adv", 120);
            score += ContainsScore(path, "menu", -120);
            score += ContainsScore(path, "button", -160);

            score += Mathf.Clamp(t.fontSize, 0, 120);

            return score;
        }

        private static int ContainsScore(string text, string token, int score)
        {
            if (text.IndexOf(token, StringComparison.Ordinal) >= 0)
            {
                return score;
            }

            return 0;
        }

        private static string BuildObjectPath(Transform tr)
        {
            if (tr == null)
            {
                return "null";
            }

            StringBuilder sb = new StringBuilder(128);
            Transform cur = tr;
            int guard = 0;
            while (cur != null && guard < 64)
            {
                if (sb.Length > 0)
                {
                    sb.Insert(0, "/");
                }

                sb.Insert(0, cur.name);
                cur = cur.parent;
                guard++;
            }

            return sb.ToString();
        }

        private static void CacheResolvedFont(Font font, string source)
        {
            _overlayResolvedFont = font;
            _overlayResolvedFontSource = source ?? string.Empty;
            Log("overlay font resolved: " + font.name + " source=" + _overlayResolvedFontSource);
        }

        private static float EaseOutBack01(float t, float overshoot)
        {
            float x = Mathf.Clamp01(t) - 1f;
            float c = Mathf.Max(0f, overshoot);
            return 1f + (x * x * ((c + 1f) * x + c));
        }

        private static InformationUI.Mode ParseMode(string modeText)
        {
            if (Enum.TryParse(modeText, ignoreCase: true, out InformationUI.Mode mode))
            {
                return mode;
            }

            LogWarn("invalid DisplayMode, fallback to Normal: " + modeText);
            return InformationUI.Mode.Normal;
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
                    File.AppendAllText(
                        _logFilePath,
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
