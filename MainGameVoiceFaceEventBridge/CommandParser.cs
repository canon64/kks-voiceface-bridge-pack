using System;
using UnityEngine;

namespace MainGameVoiceFaceEventBridge
{
    internal static class CommandParser
    {
        internal static bool TryParseIncoming(
            string line,
            PluginSettings settings,
            out ExternalVoiceFaceCommand command,
            out string reason)
        {
            command = null;
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(line))
            {
                reason = "empty line";
                return false;
            }

            string trimmed = line.Trim();
            var s = settings ?? new PluginSettings();

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                if (!s.AcceptJsonCommand)
                {
                    reason = "json command disabled";
                    return false;
                }

                try
                {
                    command = JsonUtility.FromJson<ExternalVoiceFaceCommand>(trimmed);
                }
                catch (Exception ex)
                {
                    reason = "json parse failed: " + ex.Message;
                    return false;
                }

                if (command == null)
                {
                    reason = "json parse returned null";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(command.type))
                {
                    command.type = "speak";
                }

                return true;
            }

            if (s.AcceptPlainAudioPath)
            {
                command = ExternalVoiceFaceCommand.FromPlainAudioPath(trimmed);
                return true;
            }

            if (!s.AcceptPlainAssetName)
            {
                reason = "plain command disabled";
                return false;
            }

            command = ExternalVoiceFaceCommand.FromPlainAssetName(trimmed);
            return true;
        }

        internal static string NormalizePipeName(string pipeName)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                return "kks_voice_face_events";
            }

            string value = pipeName.Trim();
            if (value.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(@"\\.\pipe\".Length);
            }

            return value;
        }
    }
}
