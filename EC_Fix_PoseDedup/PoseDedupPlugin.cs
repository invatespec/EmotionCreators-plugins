using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HEdit;
using Map;
using Pose;
using UnityEngine;
using YS_Node;
using CharState = HEdit.ADVPart.CharState;

namespace EC_Fix_PoseDedup
{
    // 动机：每个 CharState.Load 独立反序列化一份 PoseInfo+~107 OIBone，零跨 cut 共享，
    // 大剧本下 95%+ 内容冗余（40992 份仅 1819 唯一），占 managed 堆约 48%。
    // 加载期对相同内容的 PoseInfo 做 intern（共享同一实例），让重复副本及时成垃圾被 GC 回收，
    // 压低 Boehm 分配峰值与基线。仅 playback 场景生效；HEdit 编辑会改 pose，一律跳过。
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    public sealed class PoseDedupPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_Fix_PoseDedup";
        public const string PluginName = "EC Fix PoseDedup";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> EnablePoseDedup;
        internal static ConfigEntry<int> GcInterval;
        internal static ConfigEntry<bool> VerboseLogging;

        internal void Awake()
        {
            Log = Logger;
            EnablePoseDedup = Config.Bind(
                "General", "EnablePoseDedup", true,
                "加载期对内容相同的 PoseInfo 做 intern 去重（仅 playback 场景）。已实测有效默认开启；置 false 可回滚。");
            GcInterval = Config.Bind(
                "General", "GcInterval", 512,
                new ConfigDescription(
                    "每处理 N 个 pose 触发一次 GC.Collect 以压低垃圾峰值；0 表示不在加载中途 GC。",
                    new AcceptableValueRange<int>(0, 100000)));
            VerboseLogging = Config.Bind(
                "General", "VerboseLogging", false,
                "详细日志：启动时跑等价比较自检，并打印更多去重明细。");

            new Harmony(GUID).PatchAll(typeof(PoseDedupHooks));

            Log.LogInfo($"{PluginName} v{Version} loaded. EnablePoseDedup={EnablePoseDedup.Value} GcInterval={GcInterval.Value}");

            if (VerboseLogging.Value)
                PoseDedup.SelfCheck(Log);
        }
    }

    internal static class PoseDedupHooks
    {
        // playback 入口；_isEdit==true 为 HEdit 编辑器，跳过去重。
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HEditData), "Load", new Type[] { typeof(string), typeof(NodeControl), typeof(bool) })]
        internal static void HEditDataLoadPrefix(bool _isEdit)
        {
            PoseDedup.Begin(_isEdit);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HEditData), "Load", new Type[] { typeof(string), typeof(NodeControl), typeof(bool) })]
        internal static void HEditDataLoadPostfix()
        {
            PoseDedup.End();
        }

        // 纯数据反序列化方法（非 HEdit 动画系统）。Active=false 时直接返回，零行为变化。
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharState), "Load", new Type[] { typeof(BinaryReader), typeof(Version) })]
        internal static void CharStateLoadPostfix(CharState __instance, bool __result)
        {
            if (__result)
                PoseDedup.OnCharStateLoaded(__instance);
        }
    }

    internal static class PoseDedup
    {
        private static bool _active;
        // hash -> 同哈希桶内的规范实例列表；命中后逐字段精确比较防碰撞误合并。
        private static readonly Dictionary<int, List<PoseInfo>> _intern = new Dictionary<int, List<PoseInfo>>();
        private static int _gcCounter;
        private static int _total;
        private static int _unique;
        private static int _deduped;

        // 审计估算：40992 pose ≈ 3628MB → ~0.0885MB/pose（含 ~107 OIBone，各含 UniRx RP ~800B）。
        private const double EstMBPerPose = 3628.0 / 40992.0;

        internal static void Begin(bool isEdit)
        {
            _intern.Clear();
            _gcCounter = _total = _unique = _deduped = 0;
            _active = PoseDedupPlugin.EnablePoseDedup.Value && !isEdit;
            if (_active && PoseDedupPlugin.VerboseLogging.Value)
                PoseDedupPlugin.Log.LogInfo("[PoseDedup] playback load detected, dedup active.");
        }

        internal static void OnCharStateLoaded(CharState cs)
        {
            if (!_active || cs == null || cs.pose == null) return;
            _total++;
            var pose = cs.pose;
            int h = Hash(pose);

            if (_intern.TryGetValue(h, out var bucket))
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    if (ContentEquals(bucket[i], pose))
                    {
                        cs.pose = bucket[i]; // intern：丢弃刚反序列化的副本，成为垃圾
                        _deduped++;
                        MaybeGC();
                        return;
                    }
                }
                bucket.Add(pose);
            }
            else
            {
                _intern[h] = new List<PoseInfo> { pose };
            }
            _unique++;
            MaybeGC();
        }

        private static void MaybeGC()
        {
            int interval = PoseDedupPlugin.GcInterval.Value;
            if (interval > 0 && ++_gcCounter % interval == 0)
                GC.Collect();
        }

        internal static void End()
        {
            if (!_active)
            {
                _intern.Clear();
                return;
            }
            _intern.Clear();   // 解除多余引用；规范实例仍被各 CharState 持有
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            double reclaimedMB = _deduped * EstMBPerPose;
            PoseDedupPlugin.Log.LogInfo(
                $"[PoseDedup] total={_total} unique={_unique} deduped={_deduped} " +
                $"estReclaimedMB≈{reclaimedMB:F0}");
            _active = false;
        }

        // ---- 等价定义（仅纳入 playback 实际应用的字段；排除 puid/uuid/name/language/dataVersion/hashPackage）----

        private static int Hash(PoseInfo p)
        {
            int h = 17;
            h = h * 31 + p.animeNo;
            h = h * 31 + (int)p.mode;
            h = h * 31 + (p.usedFKBreast ? 1 : 0);
            h = h * 31 + (p.usedFKSon ? 1 : 0);
            h = h * 31 + (p.usedFKHair ? 1 : 0);
            h = h * 31 + (p.usedFKSkirt ? 1 : 0);
            h = h * 31 + BoolArrHash(p.usedIK);
            h = h * 31 + IntArrHash(p.hairIDs);
            h = h * 31 + BoolArrHash(p.jointCorrection);
            h = h * 31 + HandHash(p.handStates[0]);
            h = h * 31 + HandHash(p.handStates[1]);
            // 逐骨 XOR 累加 → 与字典迭代顺序无关
            h = h * 31 + p.dicFKBone.Count;
            int fk = 0;
            foreach (var kv in p.dicFKBone) fk ^= BoneHash(kv.Key, kv.Value);
            h = h * 31 + fk;
            h = h * 31 + p.dicIKBone.Count;
            int ik = 0;
            foreach (var kv in p.dicIKBone) ik ^= BoneHash(kv.Key, kv.Value);
            h = h * 31 + ik;
            return h;
        }

        private static bool ContentEquals(PoseInfo a, PoseInfo b)
        {
            if (a.mode != b.mode) return false;
            if (a.animeNo != b.animeNo) return false;
            if (a.usedFKBreast != b.usedFKBreast) return false;
            if (a.usedFKSon != b.usedFKSon) return false;
            if (a.usedFKHair != b.usedFKHair) return false;
            if (a.usedFKSkirt != b.usedFKSkirt) return false;
            if (!BoolArrEq(a.usedIK, b.usedIK)) return false;
            if (!IntArrEq(a.hairIDs, b.hairIDs)) return false;
            if (!BoolArrEq(a.jointCorrection, b.jointCorrection)) return false;
            if (!HandEq(a.handStates[0], b.handStates[0])) return false;
            if (!HandEq(a.handStates[1], b.handStates[1])) return false;
            if (!BoneDictEq(a.dicFKBone, b.dicFKBone)) return false;
            if (!BoneDictEq(a.dicIKBone, b.dicIKBone)) return false;
            return true;
        }

        private static bool BoneDictEq(Dictionary<int, OIBone> a, Dictionary<int, OIBone> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var other)) return false;
                if (!ChangeEq(kv.Value.changeAmount, other.changeAmount)) return false;
            }
            return true;
        }

        private static bool ChangeEq(ChangeAmount a, ChangeAmount b)
        {
            return Vec3Eq(a.pos, b.pos) && Vec3Eq(a.rot, b.rot) && Vec3Eq(a.scale, b.scale);
        }

        private static int BoneHash(int key, OIBone bone)
        {
            var ca = bone.changeAmount;
            int h = key;
            h = h * 31 + Vec3Hash(ca.pos);
            h = h * 31 + Vec3Hash(ca.rot);
            h = h * 31 + Vec3Hash(ca.scale);
            return h;
        }

        private static int HandHash(HandState s)
        {
            int h = (int)s.hand;
            h = h * 31 + s.handPatterns[0];
            h = h * 31 + s.handPatterns[1];
            h = h * 31 + s.handPatternRate;
            return h;
        }

        private static bool HandEq(HandState a, HandState b)
        {
            return a.hand == b.hand
                && a.handPatterns[0] == b.handPatterns[0]
                && a.handPatterns[1] == b.handPatterns[1]
                && a.handPatternRate == b.handPatternRate;
        }

        // 浮点 bit 级比较，避免量化误判（NaN/-0 也精确区分）。
        private static bool Vec3Eq(Vector3 x, Vector3 y)
        {
            return FloatBits(x.x) == FloatBits(y.x)
                && FloatBits(x.y) == FloatBits(y.y)
                && FloatBits(x.z) == FloatBits(y.z);
        }

        private static int Vec3Hash(Vector3 v)
        {
            int h = FloatBits(v.x);
            h = h * 31 + FloatBits(v.y);
            h = h * 31 + FloatBits(v.z);
            return h;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatIntUnion
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public int I;
        }

        // net46 无 BitConverter.SingleToInt32Bits；用 union 取 bit，零分配。
        private static int FloatBits(float f)
        {
            FloatIntUnion u = default;
            u.F = f;
            return u.I;
        }

        private static int BoolArrHash(bool[] a)
        {
            int h = 1;
            for (int i = 0; i < a.Length; i++) h = h * 31 + (a[i] ? 1 : 0);
            return h;
        }

        private static int IntArrHash(int[] a)
        {
            int h = 1;
            for (int i = 0; i < a.Length; i++) h = h * 31 + a[i];
            return h;
        }

        private static bool BoolArrEq(bool[] a, bool[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool IntArrEq(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // 自检：相同内容/不同 puid → 判等真；改一个骨骼 → 判等假。
        internal static void SelfCheck(ManualLogSource log)
        {
            try
            {
                var a = MakePose(5, 100, new Vector3(1f, 2f, 3f));
                var b = MakePose(5, 100, new Vector3(1f, 2f, 3f));
                var c = MakePose(5, 100, new Vector3(1f, 2f, 3.5f));

                bool same = ContentEquals(a, b);
                bool hashSame = Hash(a) == Hash(b);
                bool diff = ContentEquals(a, c);

                log.LogInfo($"[PoseDedup.SelfCheck] sameContent={same}(expect True) " +
                            $"hashEq={hashSame}(expect True) diffBone={diff}(expect False)");
                if (!same || !hashSame || diff)
                    log.LogError("[PoseDedup.SelfCheck] FAILED — 等价比较逻辑异常，请勿启用 EnablePoseDedup。");
            }
            catch (Exception ex)
            {
                log.LogWarning("[PoseDedup.SelfCheck] threw: " + ex);
            }
        }

        private static PoseInfo MakePose(int animeNo, int boneKey, Vector3 pos)
        {
            var p = new PoseInfo { animeNo = animeNo, mode = PoseInfo.ModeEN.FK };
            var bone = new OIBone(boneKey);
            bone.changeAmount.pos = pos;
            p.dicFKBone[boneKey] = bone;
            return p;
        }
    }
}
