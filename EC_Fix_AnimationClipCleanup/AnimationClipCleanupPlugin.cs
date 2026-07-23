using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace IllusionFixes
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    public class AnimationClipCleanupPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_Fix_AnimationClipCleanup";
        public const string PluginName = "AnimationClip Cleanup";
        public const string Version = "1.5.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> EnableCleanup;
        internal static ConfigEntry<bool> VerboseLogging;

        internal void Start()
        {
            Log = Logger;

            EnableCleanup = Config.Bind(
                "Animation", "EnableCleanup", true,
                "Destroy old AnimationClips before loading new pose animation.");
            VerboseLogging = Config.Bind(
                "General", "VerboseLogging", false,
                "Log each destroyed AnimationClip.");

            Harmony.CreateAndPatchAll(typeof(Hooks.LoadAnimationHook), GUID);

            Log.LogInfo($"{PluginName} v{Version} started.");
        }
    }

    namespace Hooks
    {
        // Only destroy individual AnimationClip objects, NOT the controller itself.
        // The controller may be held by editor UI or other systems long-term.

        internal static class LoadAnimationHook
        {
            [HarmonyPrefix]
            [HarmonyPatch(typeof(ChaControl), "LoadAnimation")]
            internal static void Prefix(ChaControl __instance, out RuntimeAnimatorController __state)
            {
                __state = null;
                if (!AnimationClipCleanupPlugin.EnableCleanup.Value) return;
                __state = __instance.animBody?.runtimeAnimatorController;
            }

            [HarmonyPostfix]
            [HarmonyPatch(typeof(ChaControl), "LoadAnimation")]
            internal static void Postfix(RuntimeAnimatorController __result, RuntimeAnimatorController __state)
            {
                if (__state == null || __result == null) return;
                if (__state == __result) return;

                var oldClips = __state.animationClips;
                if (oldClips == null) return;

                // AssetBundle 对同一 clip 路径返回同一对象。新 controller 可能与旧 controller
                // 共享 clip（idle、呼吸等），销毁共享 clip 会让 Animator 持 destroyed 引用。
                var newClips = __result.animationClips;
                var keep = new HashSet<int>();
                if (newClips != null)
                {
                    foreach (var clip in newClips)
                        if (clip != null) keep.Add(clip.GetInstanceID());
                }

                int destroyed = 0, skipped = 0;
                foreach (var clip in oldClips)
                {
                    if (clip == null) continue;
                    if (keep.Contains(clip.GetInstanceID()))
                    {
                        skipped++;
                        continue;
                    }
                    Object.Destroy(clip);
                    destroyed++;
                }

                if (AnimationClipCleanupPlugin.VerboseLogging.Value && (destroyed > 0 || skipped > 0))
                    AnimationClipCleanupPlugin.Log.LogInfo(
                        $"Cleanup: destroyed {destroyed}, kept-shared {skipped} (controller preserved)");
            }
        }
    }
}
