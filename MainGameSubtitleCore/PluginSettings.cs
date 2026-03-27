using System.Runtime.Serialization;

namespace MainGameSubtitleCore
{
    [DataContract]
    internal sealed class PluginSettings
    {
        [DataMember] public bool Enabled = true;
        [DataMember] public bool VerboseLog = true;
        [DataMember] public bool EnableCtrlRReload = true;
        [DataMember] public string TriggerKey = "F6";
        [DataMember] public string DisplayBackend = "Auto"; // Auto / InformationUI / Overlay
        [DataMember] public string DisplayMode = "Normal";
        [DataMember] public string TestText = "字幕テスト";
        [DataMember] public float HoldSeconds = 10.0f;
        [DataMember] public int OverlayFontSize = 36;
        [DataMember] public bool OverlayUseConversationFont = true;
        [DataMember] public bool OverlayShadowEnabled = true;
        [DataMember] public string OverlayShadowColor = "#000000FF";
        [DataMember] public float OverlayShadowOffsetX = 3f;
        [DataMember] public float OverlayShadowOffsetY = 3f;
        [DataMember] public float OverlayBottomMargin = 60f;
        [DataMember] public float OverlayHorizontalMargin = 120f;
        [DataMember] public float OverlayLineSpacing = 8f;
        [DataMember] public string OverlayTextColor = "#66D9FFFF";
        [DataMember] public string OverlayBackgroundColor = "#00000000";
        [DataMember] public bool OverlayIntroEnabled = true;
        [DataMember] public float OverlayIntroDuration = 0.22f;
        [DataMember] public float OverlayIntroScaleFrom = 1.35f;
        [DataMember] public float OverlayIntroYOffset = 14f;
        [DataMember] public string OverlayIntroHighlightColor = "#A8ECFFFF";
        [DataMember] public float OverlayIntroHighlightDuration = 0.10f;
        [DataMember] public float OverlayIntroBackOvershoot = 1.2f;
        [DataMember] public bool InputPanelEnabled = true;
        [DataMember] public string InputPanelToggleKey = "F7";
        [DataMember] public bool InputPanelShowOnlyInH = true;
        [DataMember] public bool InputPanelCloseWhenHExits = true;
        [DataMember] public float InputPanelWidth = 980f;
        [DataMember] public float InputPanelHeight = 600f;
        [DataMember] public int InputPanelInputFontSize = 22;
        [DataMember] public bool InputPanelSendOnEnter = true;
        [DataMember] public string InputPanelSendBackend = "Auto";
        [DataMember] public string InputPanelSendDisplayMode = "Normal";
        [DataMember] public float InputPanelSendHoldSeconds = 6.0f;
        [DataMember] public bool InputPanelForwardEnabled = true;
        [DataMember] public string InputPanelForwardHost = "127.0.0.1";
        [DataMember] public int InputPanelForwardPort = 18767;
        [DataMember] public string InputPanelForwardEndpoint = "/manual-text";
        [DataMember] public string InputPanelForwardToken = "";
        [DataMember] public int InputPanelForwardTimeoutMs = 2000;
        [DataMember] public string InputPanelForwardSource = "subtitle-ui";
        [DataMember] public int InputPanelHistoryMax = 100;
        [DataMember] public int InputPanelPresetMax = 100;
        [DataMember] public float InputPanelListHeight = 360f;

        internal void Normalize()
        {
            if (string.IsNullOrWhiteSpace(TriggerKey))
            {
                TriggerKey = "F6";
            }

            if (string.IsNullOrWhiteSpace(DisplayMode))
            {
                DisplayMode = "Normal";
            }

            if (string.IsNullOrWhiteSpace(DisplayBackend))
            {
                DisplayBackend = "Auto";
            }

            if (string.IsNullOrWhiteSpace(TestText))
            {
                TestText = "字幕テスト";
            }

            if (HoldSeconds < 0.1f)
            {
                HoldSeconds = 0.1f;
            }

            if (HoldSeconds > 600f)
            {
                HoldSeconds = 600f;
            }

            if (OverlayFontSize < 12)
            {
                OverlayFontSize = 12;
            }

            if (OverlayFontSize > 96)
            {
                OverlayFontSize = 96;
            }

            if (OverlayBottomMargin < 0f)
            {
                OverlayBottomMargin = 0f;
            }

            if (OverlayBottomMargin > 800f)
            {
                OverlayBottomMargin = 800f;
            }

            if (OverlayHorizontalMargin < 0f)
            {
                OverlayHorizontalMargin = 0f;
            }

            if (OverlayHorizontalMargin > 1200f)
            {
                OverlayHorizontalMargin = 1200f;
            }

            if (OverlayLineSpacing < 0f)
            {
                OverlayLineSpacing = 0f;
            }

            if (OverlayLineSpacing > 80f)
            {
                OverlayLineSpacing = 80f;
            }

            if (string.IsNullOrWhiteSpace(OverlayTextColor))
            {
                OverlayTextColor = "#FFFFFFFF";
            }

            if (string.IsNullOrWhiteSpace(OverlayBackgroundColor))
            {
                OverlayBackgroundColor = "#00000099";
            }

            if (string.IsNullOrWhiteSpace(OverlayShadowColor))
            {
                OverlayShadowColor = "#000000FF";
            }

            if (OverlayIntroDuration < 0f)
            {
                OverlayIntroDuration = 0f;
            }

            if (OverlayIntroDuration > 2f)
            {
                OverlayIntroDuration = 2f;
            }

            if (OverlayIntroScaleFrom < 1f)
            {
                OverlayIntroScaleFrom = 1f;
            }

            if (OverlayIntroScaleFrom > 2f)
            {
                OverlayIntroScaleFrom = 2f;
            }

            if (OverlayIntroYOffset < 0f)
            {
                OverlayIntroYOffset = 0f;
            }

            if (OverlayIntroYOffset > 120f)
            {
                OverlayIntroYOffset = 120f;
            }

            if (string.IsNullOrWhiteSpace(OverlayIntroHighlightColor))
            {
                OverlayIntroHighlightColor = "#A8ECFFFF";
            }

            if (OverlayIntroHighlightDuration < 0f)
            {
                OverlayIntroHighlightDuration = 0f;
            }

            if (OverlayIntroHighlightDuration > 1f)
            {
                OverlayIntroHighlightDuration = 1f;
            }

            if (OverlayIntroBackOvershoot < 0f)
            {
                OverlayIntroBackOvershoot = 0f;
            }

            if (OverlayIntroBackOvershoot > 4f)
            {
                OverlayIntroBackOvershoot = 4f;
            }

            if (OverlayShadowOffsetX < -20f)
            {
                OverlayShadowOffsetX = -20f;
            }

            if (OverlayShadowOffsetX > 20f)
            {
                OverlayShadowOffsetX = 20f;
            }

            if (OverlayShadowOffsetY < -20f)
            {
                OverlayShadowOffsetY = -20f;
            }

            if (OverlayShadowOffsetY > 20f)
            {
                OverlayShadowOffsetY = 20f;
            }

            if (string.IsNullOrWhiteSpace(InputPanelToggleKey))
            {
                InputPanelToggleKey = "F7";
            }

            if (InputPanelWidth < 320f)
            {
                InputPanelWidth = 320f;
            }

            if (InputPanelWidth > 2000f)
            {
                InputPanelWidth = 2000f;
            }

            if (InputPanelHeight < 240f)
            {
                InputPanelHeight = 240f;
            }

            if (InputPanelHeight > 1400f)
            {
                InputPanelHeight = 1400f;
            }

            if (InputPanelInputFontSize < 14)
            {
                InputPanelInputFontSize = 14;
            }

            if (InputPanelInputFontSize > 48)
            {
                InputPanelInputFontSize = 48;
            }

            if (string.IsNullOrWhiteSpace(InputPanelSendBackend))
            {
                InputPanelSendBackend = "Auto";
            }

            if (string.IsNullOrWhiteSpace(InputPanelSendDisplayMode))
            {
                InputPanelSendDisplayMode = "Normal";
            }

            if (InputPanelSendHoldSeconds < 0.1f)
            {
                InputPanelSendHoldSeconds = 0.1f;
            }

            if (InputPanelSendHoldSeconds > 600f)
            {
                InputPanelSendHoldSeconds = 600f;
            }

            if (string.IsNullOrWhiteSpace(InputPanelForwardHost))
            {
                InputPanelForwardHost = "127.0.0.1";
            }

            if (InputPanelForwardPort < 1)
            {
                InputPanelForwardPort = 1;
            }

            if (InputPanelForwardPort > 65535)
            {
                InputPanelForwardPort = 65535;
            }

            if (string.IsNullOrWhiteSpace(InputPanelForwardEndpoint))
            {
                InputPanelForwardEndpoint = "/manual-text";
            }
            else if (!InputPanelForwardEndpoint.StartsWith("/"))
            {
                InputPanelForwardEndpoint = "/" + InputPanelForwardEndpoint.Trim();
            }

            if (InputPanelForwardTimeoutMs < 100)
            {
                InputPanelForwardTimeoutMs = 100;
            }

            if (InputPanelForwardTimeoutMs > 15000)
            {
                InputPanelForwardTimeoutMs = 15000;
            }

            if (string.IsNullOrWhiteSpace(InputPanelForwardSource))
            {
                InputPanelForwardSource = "subtitle-ui";
            }

            if (InputPanelHistoryMax < 10)
            {
                InputPanelHistoryMax = 10;
            }

            if (InputPanelHistoryMax > 100)
            {
                InputPanelHistoryMax = 100;
            }

            if (InputPanelPresetMax < 10)
            {
                InputPanelPresetMax = 10;
            }

            if (InputPanelPresetMax > 100)
            {
                InputPanelPresetMax = 100;
            }

            if (InputPanelListHeight < 120f)
            {
                InputPanelListHeight = 120f;
            }

            if (InputPanelListHeight > 900f)
            {
                InputPanelListHeight = 900f;
            }
        }
    }
}

