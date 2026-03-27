using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MainGameVoiceFaceEventBridge
{
    internal static class VoiceGuardHooks
    {
        private static Action<string> _logInfo;
        private static Action<string> _logWarn;
        private static Action<string> _logError;

        internal static void Apply(
            Harmony harmony,
            Action<string> logInfo,
            Action<string> logWarn,
            Action<string> logError)
        {
            _logInfo = logInfo;
            _logWarn = logWarn;
            _logError = logError;

            PatchPrefix(harmony, FindHVoiceCtrlProcMethod(), nameof(HVoiceCtrlProcPrefix), "HVoiceCtrl.Proc");
            PatchPrefix(harmony, FindHVoiceCtrlPlayVoiceMethod(), nameof(HVoiceCtrlPlayVoicePrefix), "HVoiceCtrl.PlayVoice");

            PatchPrefix(
                harmony,
                AccessTools.Method(typeof(Manager.Voice), "Play", new[] { typeof(Manager.Voice.Loader) }),
                nameof(ManagerVoicePlayPrefix),
                "Manager.Voice.Play");

            PatchPrefix(
                harmony,
                AccessTools.Method(typeof(Manager.Voice), "OncePlay", new[] { typeof(Manager.Voice.Loader) }),
                nameof(ManagerVoiceOncePlayPrefix),
                "Manager.Voice.OncePlay");

            PatchPrefix(
                harmony,
                AccessTools.Method(typeof(Manager.Voice), "OncePlayChara", new[] { typeof(Manager.Voice.Loader) }),
                nameof(ManagerVoiceOncePlayCharaPrefix),
                "Manager.Voice.OncePlayChara");
        }

        private static MethodInfo FindHVoiceCtrlPlayVoiceMethod()
        {
            foreach (var method in typeof(HVoiceCtrl).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "PlayVoice" || method.ReturnType != typeof(bool))
                {
                    continue;
                }

                var p = method.GetParameters();
                if (p.Length == 7 && p[0].ParameterType == typeof(ChaControl))
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo FindHVoiceCtrlProcMethod()
        {
            foreach (var method in typeof(HVoiceCtrl).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "Proc" || method.ReturnType != typeof(bool))
                {
                    continue;
                }

                var p = method.GetParameters();
                if (p.Length == 2
                    && p[0].ParameterType != null
                    && p[0].ParameterType.FullName == "UnityEngine.AnimatorStateInfo")
                {
                    return method;
                }
            }

            return null;
        }

        private static void PatchPrefix(Harmony harmony, MethodInfo target, string prefixMethodName, string label)
        {
            if (target == null)
            {
                _logWarn?.Invoke("[patch] prefix target not found: " + label);
                return;
            }

            var prefix = AccessTools.Method(typeof(VoiceGuardHooks), prefixMethodName);
            if (prefix == null)
            {
                _logError?.Invoke("[patch] prefix method not found: " + prefixMethodName);
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            _logInfo?.Invoke("[patch] prefix ok: " + label);
        }

        private static bool ShouldBlock()
        {
            var plugin = Plugin.Instance;
            return plugin != null && plugin.ShouldBlockGameVoiceEvents();
        }

        private static bool HVoiceCtrlProcPrefix(ref bool __result)
        {
            if (!ShouldBlock())
            {
                return true;
            }

            __result = true;
            Plugin.Instance?.OnGameVoiceEventBlocked("HVoiceCtrl.Proc");
            return false;
        }

        private static bool HVoiceCtrlPlayVoicePrefix(ref bool __result)
        {
            if (!ShouldBlock())
            {
                return true;
            }

            __result = false;
            Plugin.Instance?.OnGameVoiceEventBlocked("HVoiceCtrl.PlayVoice");
            return false;
        }

        private static bool ManagerVoicePlayPrefix(Manager.Voice.Loader loader, ref AudioSource __result)
        {
            if (!ShouldBlock())
            {
                return true;
            }

            __result = null;
            Plugin.Instance?.OnGameVoiceEventBlocked("Manager.Voice.Play");
            return false;
        }

        private static bool ManagerVoiceOncePlayPrefix(Manager.Voice.Loader loader, ref AudioSource __result)
        {
            if (!ShouldBlock())
            {
                return true;
            }

            __result = null;
            Plugin.Instance?.OnGameVoiceEventBlocked("Manager.Voice.OncePlay");
            return false;
        }

        private static bool ManagerVoiceOncePlayCharaPrefix(Manager.Voice.Loader loader, ref AudioSource __result)
        {
            if (!ShouldBlock())
            {
                return true;
            }

            __result = null;
            Plugin.Instance?.OnGameVoiceEventBlocked("Manager.Voice.OncePlayChara");
            return false;
        }
    }
}
