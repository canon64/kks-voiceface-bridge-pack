using System.Runtime.Serialization;

namespace MainGameSubtitleEventBridge
{
    [DataContract]
    internal sealed class PluginSettings
    {
        [DataMember] public bool Enabled = true;
        [DataMember] public bool VerboseLog = true;
        [DataMember] public bool EnableCtrlRReload = true;

        [DataMember] public string ListenHost = "0.0.0.0";
        [DataMember] public int ListenPort = 18766;
        [DataMember] public string EndpointPath = "/subtitle-event";
        [DataMember] public string AuthToken = "";
        [DataMember] public int MaxBodyBytes = 32768;
        [DataMember] public bool AcceptPlainTextBody = true;

        [DataMember] public float DefaultHoldSeconds = 10.0f;

        // チャイム設定
        [DataMember] public bool EnableChime = true;
        [DataMember] public float ChimeVolume = 0.5f;
        [DataMember] public string MaleChimeFile = "male/btn03.mp3";
        [DataMember] public string FemaleChimeFile = "female/btn02.mp3";
        [DataMember] public string DefaultBackend = "Auto";
        [DataMember] public string DefaultDisplayMode = "Normal";

        internal void Normalize()
        {
            if (string.IsNullOrWhiteSpace(ListenHost))
            {
                ListenHost = "0.0.0.0";
            }

            if (ListenPort < 1)
            {
                ListenPort = 1;
            }

            if (ListenPort > 65535)
            {
                ListenPort = 65535;
            }

            if (string.IsNullOrWhiteSpace(EndpointPath))
            {
                EndpointPath = "/subtitle-event";
            }
            else if (!EndpointPath.StartsWith("/"))
            {
                EndpointPath = "/" + EndpointPath;
            }

            if (MaxBodyBytes < 256)
            {
                MaxBodyBytes = 256;
            }

            if (MaxBodyBytes > 1024 * 1024)
            {
                MaxBodyBytes = 1024 * 1024;
            }

            if (DefaultHoldSeconds < 0.1f)
            {
                DefaultHoldSeconds = 0.1f;
            }

            if (DefaultHoldSeconds > 300f)
            {
                DefaultHoldSeconds = 300f;
            }

            if (string.IsNullOrWhiteSpace(DefaultBackend))
            {
                DefaultBackend = "Auto";
            }

            if (string.IsNullOrWhiteSpace(DefaultDisplayMode))
            {
                DefaultDisplayMode = "Normal";
            }

            if (ChimeVolume < 0f)
            {
                ChimeVolume = 0f;
            }

            if (ChimeVolume > 1f)
            {
                ChimeVolume = 1f;
            }

            if (string.IsNullOrWhiteSpace(MaleChimeFile))
            {
                MaleChimeFile = "male/btn03.mp3";
            }

            if (string.IsNullOrWhiteSpace(FemaleChimeFile))
            {
                FemaleChimeFile = "female/btn02.mp3";
            }
        }
    }
}
