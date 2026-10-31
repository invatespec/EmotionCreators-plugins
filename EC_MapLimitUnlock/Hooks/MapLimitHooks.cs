using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using HEdit;
using YS_Node;

namespace EC_MapLimitUnlock
{
    internal static class MapLimitHooks
    {
        internal const int MapLimit = 99;

        // 原版在添加地图回调里内联写死 maps.Count < 21
        private const int VanillaLimit = 21;

        private const byte LdcI4S = 0x1F;
        private const byte CltFirst = 0xFE;
        private const byte CltSecond = 0x04;

        private static readonly HashSet<string> WarnedScenes = new HashSet<string>();

        internal static void Apply(Harmony harmony)
        {
            PatchLimitIn(typeof(BaseSettingMapUI), harmony);
            PatchLimitIn(typeof(InitializeCanvas), harmony);

            MethodInfo load = AccessTools.Method(
                typeof(HEditData), "Load",
                new[] { typeof(string), typeof(NodeControl), typeof(bool) });

            if (load == null)
            {
                MapLimitUnlockPlugin.Log.LogWarning("未找到 HEditData.Load，跳过越界提示");
                return;
            }

            harmony.Patch(load, postfix: new HarmonyMethod(typeof(MapLimitHooks), nameof(LoadPostfix)));
        }

        // 上限是内联常量，Prefix/Postfix 改不了，只能替换 IL 里的 21
        private static void PatchLimitIn(Type type, Harmony harmony)
        {
            bool patched = false;
            foreach (MethodInfo method in EnumerateMethods(type))
            {
                if (!ContainsVanillaLimit(method)) continue;

                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(MapLimitHooks), nameof(ReplaceLimit)));
                patched = true;
                MapLimitUnlockPlugin.Log.LogInfo("已替换地图上限: " + type.Name + "." + method.Name);
            }

            if (!patched)
            {
                MapLimitUnlockPlugin.Log.LogWarning(type.Name + " 未找到地图上限判断，游戏版本可能已变化");
            }
        }

        // 两个类各自只有一处 21+clt，直接按 IL 模式定位，不依赖编译器命名规则
        private static IEnumerable<MethodInfo> EnumerateMethods(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            foreach (MethodInfo method in type.GetMethods(flags))
            {
                yield return method;
            }

            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (MethodInfo method in nested.GetMethods(flags))
                {
                    yield return method;
                }
            }
        }

        private static bool ContainsVanillaLimit(MethodBase method)
        {
            MethodBody body = method.GetMethodBody();
            if (body == null) return false;

            byte[] il = body.GetILAsByteArray();
            if (il == null) return false;

            for (int i = 0; i + 3 < il.Length; i++)
            {
                if (il[i] == LdcI4S && il[i + 1] == VanillaLimit
                    && il[i + 2] == CltFirst && il[i + 3] == CltSecond)
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<CodeInstruction> ReplaceLimit(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (!IsVanillaLimit(list[i])) continue;
                if (list[i + 1].opcode != OpCodes.Clt) continue;

                list[i] = new CodeInstruction(OpCodes.Ldc_I4, MapLimit);
            }

            return list;
        }

        private static bool IsVanillaLimit(CodeInstruction instruction)
        {
            if (instruction.opcode != OpCodes.Ldc_I4_S) return false;
            if (instruction.operand is sbyte value) return value == VanillaLimit;
            if (instruction.operand is int intValue) return intValue == VanillaLimit;
            return false;
        }

        // 超过原版上限的场景仍能打开，但对方没装同类插件时无法继续加地图
        private static void LoadPostfix(string _path, bool __result)
        {
            if (!__result) return;
            if (!Singleton<HEditData>.IsInstance()) return;

            HEditData data = Singleton<HEditData>.Instance;
            if (data == null || data.maps == null) return;

            // maps[0] 是"なし"，原版按条目数判断，这里保持一致
            if (data.maps.Count <= VanillaLimit) return;
            if (!WarnedScenes.Add(_path)) return;

            MapLimitUnlockPlugin.Log.LogWarning(
                "场景地图数 " + (data.maps.Count - 1) + " 超过原版上限，"
                + "未安装 EC_MapLimitUnlock 的玩家无法继续添加地图");
        }
    }
}
