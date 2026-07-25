using System;
using System.IO;
using HarmonyLib;
using HEdit;
using UnityEngine;

namespace EC_SceneExport
{
    [HarmonyPatch(typeof(Motion.Animation), nameof(Motion.Animation.Load))]
    internal static class FingerCjImportSanitize
    {
        private const int NativeCjSize = 5;
        private const int FallbackExpandSize = 36;

        [HarmonyPrefix]
        private static void Prefix(Motion.Animation __instance, BinaryReader _reader, out bool __state)
        {
            // __state：Load 后是否需要按配置裁回 5
            __state = false;
            if (__instance == null || _reader == null)
                return;

            try
            {
                if (!_reader.BaseStream.CanSeek)
                {
                    ExpandTo(__instance, FallbackExpandSize);
                    __state = true;
                    return;
                }

                long pos = _reader.BaseStream.Position;
                SkipToCorrectionJointCount(_reader);
                int count = _reader.ReadInt32();
                _reader.BaseStream.Position = pos;

                if (count <= __instance.correctionJoints.Length)
                    return;

                ExpandTo(__instance, count);
                __state = count > NativeCjSize;
            }
            catch
            {
                // peek 失败：兜底扩容，避免 OOB
                ExpandTo(__instance, FallbackExpandSize);
                __state = true;
            }
        }

        [HarmonyPostfix]
        private static void Postfix(Motion.Animation __instance, bool __state)
        {
            if (!__state || __instance == null)
                return;
            // 与 EnableCharaRemove 同模式：用户主动开才做破坏性裁剪
            if (SceneExport.TrimExtraCorrectionJoints == null
                || !SceneExport.TrimExtraCorrectionJoints.Value)
                return;

            TrimToNative(__instance);
        }

        private static void ExpandTo(Motion.Animation anim, int minLength)
        {
            if (minLength < NativeCjSize)
                minLength = NativeCjSize;
            Vector3[] src = anim.correctionJoints;
            if (src != null && src.Length >= minLength)
                return;

            var dst = new Vector3[minLength];
            int copy = src == null ? 0 : Math.Min(src.Length, minLength);
            for (int i = 0; i < copy; i++)
                dst[i] = src[i];
            anim.correctionJoints = dst;
        }

        private static void TrimToNative(Motion.Animation anim)
        {
            Vector3[] src = anim.correctionJoints;
            if (src == null || src.Length <= NativeCjSize)
                return;

            var dst = new Vector3[NativeCjSize];
            for (int i = 0; i < NativeCjSize; i++)
                dst[i] = src[i];
            anim.correctionJoints = dst;
        }

        // 与 Animation.Load 前半段 / Finger SkipToCorrectionJointCount 同构
        private static void SkipToCorrectionJointCount(BinaryReader reader)
        {
            reader.ReadInt32(); // category
            reader.ReadInt32(); // id
            reader.ReadInt32(); // type
            reader.ReadSingle(); // waitParameter
            reader.ReadSingle(); // statePlayTime
            reader.ReadSingle(); // paizuriPlayTime

            int n = reader.ReadInt32();
            for (int i = 0; i < n; i++)
                reader.ReadBoolean(); // siruUses

            n = reader.ReadInt32();
            for (int i = 0; i < n; i++)
                reader.ReadSingle(); // siruTiming

            n = reader.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                // Layer: weight, state, initWeight
                reader.ReadSingle();
                reader.ReadInt32();
                reader.ReadSingle();
            }
        }
    }
}
