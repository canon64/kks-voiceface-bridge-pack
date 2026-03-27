using System.Runtime.Serialization;

namespace MainGameVoiceFaceEventBridge
{
    [DataContract]
    internal sealed class PluginSettings
    {
        [DataMember] public bool Enabled = true;
        [DataMember] public bool VerboseLog = true;
        [DataMember] public bool EnableCtrlRReload = true;

        [DataMember] public bool EnablePipeServer = true;
        [DataMember] public string PipeName = "kks_voice_face_events";
        [DataMember] public int MaxQueuedCommands = 64;
        [DataMember] public bool AcceptJsonCommand = true;
        [DataMember] public bool AcceptPlainAudioPath = true;
        [DataMember] public bool AcceptPlainAssetName = false;
        [DataMember] public bool StopGameVoiceBeforeExternalPlay = true;
        [DataMember] public bool BlockGameVoiceWhileExternalPlaying = true;
        [DataMember] public float ExternalPlayPreBlockSeconds = 0.25f;

        [DataMember] public bool IgnoreCommandOutsideHScene = true;
        [DataMember] public int TargetMainIndex = 0;

        [DataMember] public string DefaultAssetBundle = "abdata\\sound\\data\\pcm\\c13\\h\\01.unity3d";
        [DataMember] public string DefaultAssetName = "h_ko_13_03_001";
        [DataMember] public float DefaultPitch = 1.0f;
        [DataMember] public float DefaultFadeTime = 0.0f;
        [DataMember] public int DefaultEyeNeck = 0;
        [DataMember] public int DefaultVoiceKind = 0;
        [DataMember] public int DefaultAction = 0;
        [DataMember] public bool DefaultInterruptCurrent = true;
        [DataMember] public bool DeleteAudioAfterPlayback = false;
        [DataMember] public float PlaybackVolume = 1.0f;
        [DataMember] public float FemalePlaybackVolume = -1.0f;
        [DataMember] public float ExternalPlaybackPitch = 1.0f;

        // true: current face is kept by default (face=-1 on PlayVoice).
        // false: use explicit/random face selection.
        [DataMember] public bool KeepCurrentFaceByDefault = false;

        // If >= 0, fixed face. If < 0, choose from RandomFaceCandidates.
        [DataMember] public int DefaultFace = -1;
        [DataMember] public int[] RandomFaceCandidates = new[] { 0 };

        internal void Normalize()
        {
            if (string.IsNullOrWhiteSpace(PipeName))
            {
                PipeName = "kks_voice_face_events";
            }

            if (TargetMainIndex < 0)
            {
                TargetMainIndex = 0;
            }

            if (MaxQueuedCommands < 1)
            {
                MaxQueuedCommands = 1;
            }

            if (MaxQueuedCommands > 1024)
            {
                MaxQueuedCommands = 1024;
            }

            if (ExternalPlayPreBlockSeconds < 0f)
            {
                ExternalPlayPreBlockSeconds = 0f;
            }

            if (ExternalPlayPreBlockSeconds > 2f)
            {
                ExternalPlayPreBlockSeconds = 2f;
            }

            if (DefaultPitch < 0f)
            {
                DefaultPitch = 0f;
            }

            if (DefaultPitch > 3f)
            {
                DefaultPitch = 3f;
            }

            if (DefaultFadeTime < 0f)
            {
                DefaultFadeTime = 0f;
            }

            if (PlaybackVolume < 0f)
            {
                PlaybackVolume = 0f;
            }

            if (PlaybackVolume > 1f)
            {
                PlaybackVolume = 1f;
            }

            if (FemalePlaybackVolume < -1f)
            {
                FemalePlaybackVolume = -1f;
            }

            if (FemalePlaybackVolume > 1f)
            {
                FemalePlaybackVolume = 1f;
            }

            if (ExternalPlaybackPitch < 0.1f)
            {
                ExternalPlaybackPitch = 0.1f;
            }

            if (ExternalPlaybackPitch > 3f)
            {
                ExternalPlaybackPitch = 3f;
            }

            if (RandomFaceCandidates == null)
            {
                RandomFaceCandidates = new int[0];
            }
        }
    }
}
