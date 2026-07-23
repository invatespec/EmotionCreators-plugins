using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Pose;
using UnityEngine;
using Map;

namespace EC_HEditPosePanel
{
    internal enum GuideAdjustmentKind
    {
        Hand,
        Skirt
    }

    // 展开 / 收拢；滑条 ±100 只表示当前模式内的负/正向
    internal enum GuideAdjustMode
    {
        Spread,
        Curl
    }

    internal sealed class GuideRotationRule
    {
        internal readonly Vector3 Axis;
        internal readonly float NegativeDegrees;
        internal readonly float PositiveDegrees;

        internal GuideRotationRule(Vector3 axis, float negativeDegrees, float positiveDegrees)
        {
            Axis = axis;
            NegativeDegrees = negativeDegrees;
            PositiveDegrees = positiveDegrees;
        }

        // 对称规则：正负幅度相同、符号相反
        internal static GuideRotationRule Symmetric(Vector3 axis, float positiveDegrees)
            => new GuideRotationRule(axis, -positiveDegrees, positiveDegrees);

        internal float Degrees(float value)
        {
            float t = GuideAdjustmentMath.ClampValue(value) / 100f;
            return t < 0f ? NegativeDegrees * -t : PositiveDegrees * t;
        }

        internal Quaternion Delta(float value)
        {
            // 不用 Vector3.right：回归进程无 Unity native
            Vector3 axis = Axis.sqrMagnitude < 0.0001f ? new Vector3(1f, 0f, 0f) : Axis.normalized;
            return Quaternion.AngleAxis(Degrees(value), axis);
        }
    }

    internal static class GuideAdjustmentMath
    {
        internal static float ClampValue(float value)
            => value < -100f ? -100f : value > 100f ? 100f : value;

        internal static Quaternion FromBaseline(Quaternion baseline, Quaternion delta)
            => baseline * delta;
    }

    internal sealed class GuideAdjustmentProfile
    {
        private readonly Dictionary<string, GuideRotationRule> _rules;

        internal string Version { get; }
        internal GuideAdjustmentKind Kind { get; }
        internal GuideAdjustMode Mode { get; }
        internal bool IsCalibrated => !string.IsNullOrEmpty(Version) && _rules.Count > 0;

        internal GuideAdjustmentProfile(string version, GuideAdjustmentKind kind, GuideAdjustMode mode,
            IDictionary<string, GuideRotationRule> rules)
        {
            Version = version;
            Kind = kind;
            Mode = mode;
            _rules = rules == null
                ? new Dictionary<string, GuideRotationRule>(StringComparer.Ordinal)
                : new Dictionary<string, GuideRotationRule>(rules, StringComparer.Ordinal);
        }

        // 兼容旧回归构造：无 kind/mode 元数据
        internal GuideAdjustmentProfile(string version, IDictionary<string, GuideRotationRule> rules)
            : this(version, GuideAdjustmentKind.Hand, GuideAdjustMode.Curl, rules)
        {
        }

        internal static GuideAdjustmentProfile Uncalibrated
            => new GuideAdjustmentProfile(null, GuideAdjustmentKind.Hand, GuideAdjustMode.Curl, null);

        internal bool TryGetDelta(string profileKey, float value, out Quaternion delta)
        {
            GuideRotationRule rule;
            if (!TryGetRule(profileKey, out rule))
            {
                delta = Quaternion.identity;
                return false;
            }
            delta = rule.Delta(value);
            return true;
        }

        internal bool TryGetRule(string profileKey, out GuideRotationRule rule)
        {
            rule = null;
            return IsCalibrated && !string.IsNullOrEmpty(profileKey)
                && _rules.TryGetValue(profileKey, out rule);
        }
    }

    // 最新 log.log 固化；改轴/幅度必须升 Version
    internal static class GuideAdjustmentProfiles
    {
        internal const string Version = "guide-v2.1-20260719";

        // 裙 sk_00 深度 00..05 满量程（校准表×2）；收拢 +100 = X-，展开 +100 = Z-
        private static readonly float[] SkirtDepthDegrees =
            { -20f, -20f, -30f, -40f, -50f, -60f };

        // 手收拢 Z 满量程（01/02/03），左右同构
        private static readonly float[] CurlFingerZ = { 64.2f, 104.2f, 100f };
        private static readonly float[] CurlThumbZ = { 47.5f, 14.7f, 57f };

        // 手展开：仅 01；index Y+，little/ring Y-；thumb Z 取反（真机修正反向）
        private const float SpreadIndexY = 15f;
        private const float SpreadSideY = 15f;
        private const float SpreadThumbZ = -25f;

        internal static GuideAdjustmentProfile For(GuideAdjustmentKind kind, GuideAdjustMode mode)
        {
            return kind == GuideAdjustmentKind.Skirt
                ? CreateSkirt(mode)
                : CreateHand(mode);
        }

        // 不用 Vector3.right/up/forward：回归进程无 Unity native，静态属性会 InvalidProgram
        private static readonly Vector3 AxisX = new Vector3(1f, 0f, 0f);
        private static readonly Vector3 AxisY = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 AxisZ = new Vector3(0f, 0f, 1f);

        private static GuideAdjustmentProfile CreateSkirt(GuideAdjustMode mode)
        {
            Vector3 axis = mode == GuideAdjustMode.Curl ? AxisX : AxisZ;
            var rules = new Dictionary<string, GuideRotationRule>(StringComparer.Ordinal);
            for (int depth = 0; depth < SkirtDepthDegrees.Length; depth++)
            {
                // +100 用表中负角度；-100 对称取反
                rules[SkirtKey(depth)] = GuideRotationRule.Symmetric(axis, SkirtDepthDegrees[depth]);
            }
            return new GuideAdjustmentProfile(Version, GuideAdjustmentKind.Skirt, mode, rules);
        }

        private static GuideAdjustmentProfile CreateHand(GuideAdjustMode mode)
        {
            var rules = new Dictionary<string, GuideRotationRule>(StringComparer.Ordinal);
            if (mode == GuideAdjustMode.Curl)
            {
                foreach (string finger in new[] { "index", "middle", "ring", "little" })
                    for (int seg = 0; seg < 3; seg++)
                        rules[HandKey(finger, seg)] = GuideRotationRule.Symmetric(AxisZ, CurlFingerZ[seg]);
                for (int seg = 0; seg < 3; seg++)
                    rules[HandKey("thumb", seg)] = GuideRotationRule.Symmetric(AxisZ, CurlThumbZ[seg]);
            }
            else
            {
                // 展开：用户规则优先；仅 01
                rules[HandKey("index", 0)] = GuideRotationRule.Symmetric(AxisY, SpreadIndexY);
                rules[HandKey("little", 0)] = GuideRotationRule.Symmetric(AxisY, -SpreadSideY);
                rules[HandKey("ring", 0)] = GuideRotationRule.Symmetric(AxisY, -SpreadSideY);
                rules[HandKey("thumb", 0)] = GuideRotationRule.Symmetric(AxisZ, SpreadThumbZ);
            }
            return new GuideAdjustmentProfile(Version, GuideAdjustmentKind.Hand, mode, rules);
        }

        internal static string HandKey(string finger, int segmentIndex)
            => finger + (segmentIndex + 1).ToString("00", CultureInfo.InvariantCulture);

        internal static string SkirtKey(int depthIndex)
            => "skirt" + depthIndex.ToString("00", CultureInfo.InvariantCulture);

        internal static bool TryProfileKey(string boneName, GuideAdjustmentKind kind, int skirtIndex,
            out string key)
        {
            key = null;
            if (string.IsNullOrEmpty(boneName)) return false;
            if (kind == GuideAdjustmentKind.Hand)
            {
                string finger;
                int segment;
                if (!TryParseFinger(boneName, out finger, out segment)) return false;
                key = HandKey(finger, segment);
                return true;
            }
            if (skirtIndex < 0) return false;
            key = SkirtKey(skirtIndex);
            return true;
        }

        private static bool TryParseFinger(string boneName, out string finger, out int segment)
        {
            finger = null;
            segment = 0;
            // 去侧后缀后解析 finger+01..03
            string name = boneName;
            if (name.EndsWith("_L", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 2);
            string lower = name.ToLowerInvariant();
            foreach (string candidate in new[] { "thumb", "index", "middle", "ring", "little" })
            {
                int position = lower.LastIndexOf(candidate, StringComparison.Ordinal);
                if (position < 0) continue;
                int numberStart = position + candidate.Length;
                if (numberStart + 2 > lower.Length) continue;
                int parsed;
                if (!int.TryParse(lower.Substring(numberStart, 2), NumberStyles.None,
                        CultureInfo.InvariantCulture, out parsed)
                    || parsed < 1 || parsed > 3)
                    continue;
                if (numberStart + 2 != lower.Length) continue;
                finger = candidate;
                segment = parsed - 1;
                return true;
            }
            return false;
        }
    }

    internal sealed class GuideRotationTarget
    {
        internal readonly OCBone Bone;
        internal readonly string Name;
        internal readonly Quaternion Baseline;
        internal readonly string ProfileKey;

        internal GuideRotationTarget(OCBone bone, string profileKey)
        {
            Bone = bone;
            Name = bone?.Name ?? string.Empty;
            ProfileKey = profileKey ?? string.Empty;
            Baseline = bone == null ? Quaternion.identity : Quaternion.Euler(bone.localRotation);
        }

        internal Quaternion Rotation
        {
            get => Bone == null ? Quaternion.identity : Quaternion.Euler(Bone.localRotation);
            set
            {
                if (Bone != null) Bone.localRotation = value.eulerAngles;
            }
        }
    }

    internal static class GuideTargetDiscovery
    {
        private static readonly string[] FingerNames = { "thumb", "index", "middle", "ring", "little" };
        private static readonly Regex SkirtDepthRegex =
            new Regex(@"_(\d{2})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static bool TryCollectHand(IEnumerable<OCBone> bones, HandSide side,
            out List<GuideRotationTarget> targets, out string error)
        {
            targets = new List<GuideRotationTarget>();
            error = null;
            List<HandBoneBinding> bindings;
            if (!HandPoseAdapter.TryCollect(bones, side, out bindings, out error)) return false;
            if (bindings.Count != 15 || !HasCompleteHandNames(bindings.Select(binding => binding.Bone.Name), side))
            {
                error = "手指 FK 骨不完整，需要五指 01..03 共 15 根。";
                return false;
            }
            foreach (HandBoneBinding binding in bindings)
            {
                string key;
                if (!GuideAdjustmentProfiles.TryProfileKey(binding.Bone.Name, GuideAdjustmentKind.Hand, -1, out key))
                {
                    error = "无法解析手指 profile 键: " + binding.Bone.Name;
                    targets.Clear();
                    return false;
                }
                targets.Add(new GuideRotationTarget(binding.Bone, key));
            }
            return true;
        }

        internal static bool HasCompleteHandNamesForRegression(IEnumerable<string> names, HandSide side)
            => HasCompleteHandNames(names, side);

        internal static bool TryCollectSkirt(OCBone selected, IEnumerable<OCBone> bones,
            out List<GuideRotationTarget> targets, out string error)
        {
            targets = new List<GuideRotationTarget>();
            error = null;
            if (selected == null || selected.group != OIBone.BoneGroup.Skirt || selected.transBone == null)
            {
                error = "请先选择裙子 FK Guide。";
                return false;
            }
            if (bones == null)
            {
                error = "裙子 FK 骨列表为空。";
                return false;
            }
            var collected = new List<OCBone>();
            foreach (OCBone bone in bones)
            {
                if (bone == null || bone.group != OIBone.BoneGroup.Skirt || bone.transBone == null) continue;
                if (IsDescendantOrSelf(selected.transBone, bone.transBone))
                    collected.Add(bone);
            }
            if (collected.Count == 0)
            {
                error = "当前裙子 Guide 没有可调整的后代骨。";
                return false;
            }
            // 先按名中深度号，再按树深度，保证 sk_XX_0d 顺序稳定
            collected.Sort(CompareSkirtBones);
            for (int i = 0; i < collected.Count; i++)
            {
                int depth = ResolveSkirtDepth(collected[i].Name, i);
                string key = GuideAdjustmentProfiles.SkirtKey(Math.Min(depth, 5));
                targets.Add(new GuideRotationTarget(collected[i], key));
            }
            return true;
        }

        internal static OCBone FindSelectedSkirtBone()
        {
            if (!Singleton<Selection>.IsInstance() || Singleton<Selection>.Instance == null) return null;
            return Singleton<Selection>.Instance.selectCtrls
                .OfType<OCBone>()
                .FirstOrDefault(bone => bone != null && bone.group == OIBone.BoneGroup.Skirt);
        }

        internal static bool IsDescendantOrSelf(Transform root, Transform candidate)
            => root != null && candidate != null && (candidate == root || candidate.IsChildOf(root));

        internal static string BuildDiagnostic(IEnumerable<GuideRotationTarget> targets,
            GuideAdjustmentKind kind, HandSide? side)
        {
            var builder = new StringBuilder();
            builder.Append(kind == GuideAdjustmentKind.Hand ? "Hand" : "Skirt");
            if (side.HasValue) builder.Append(" ").Append(side.Value == HandSide.Left ? "Left" : "Right");
            builder.Append(" targets=");
            int count = 0;
            foreach (GuideRotationTarget target in targets ?? Enumerable.Empty<GuideRotationTarget>())
            {
                if (count++ > 0) builder.Append(", ");
                builder.Append(target.Name).Append("=").Append(target.Baseline.eulerAngles);
            }
            if (count == 0) builder.Append("<none>");
            return builder.ToString();
        }

        private static int CompareSkirtBones(OCBone left, OCBone right)
        {
            int depthCompare = ResolveSkirtDepth(left.Name, 99).CompareTo(ResolveSkirtDepth(right.Name, 99));
            if (depthCompare != 0) return depthCompare;
            return GetTreeDepth(left.transBone).CompareTo(GetTreeDepth(right.transBone));
        }

        private static int ResolveSkirtDepth(string name, int fallbackIndex)
        {
            if (string.IsNullOrEmpty(name)) return fallbackIndex;
            Match match = SkirtDepthRegex.Match(name);
            int depth;
            if (match.Success
                && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out depth))
                return depth;
            return fallbackIndex;
        }

        private static int GetTreeDepth(Transform transform)
        {
            int depth = 0;
            while (transform != null && transform.parent != null)
            {
                depth++;
                transform = transform.parent;
            }
            return depth;
        }

        private static bool HasCompleteHandNames(IEnumerable<string> names, HandSide side)
        {
            if (names == null) return false;
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (string finger in FingerNames)
                for (int segment = 1; segment <= 3; segment++)
                    expected.Add(finger + segment.ToString("00"));

            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in names)
            {
                string finger;
                int segment;
                string canonical;
                if (!HandPoseAdapter.TryGetFingerKeyForRegression(
                    name, side, out canonical, out finger, out segment)) continue;
                actual.Add(finger + (segment + 1).ToString("00"));
            }
            return expected.SetEquals(actual);
        }
    }

    internal sealed class GuideAdjustmentSession
    {
        private readonly List<GuideRotationTarget> _targets = new List<GuideRotationTarget>();
        private PoseEditSnapshot _snapshot;
        private float _curlValue;
        private float _spreadValue;

        internal bool IsActive => _snapshot != null && _targets.Count > 0;
        // 当前 UI 模式对应的滑条值
        internal float Value => Mode == GuideAdjustMode.Curl ? _curlValue : _spreadValue;
        internal GuideAdjustmentKind Kind { get; private set; }
        internal GuideAdjustMode Mode { get; private set; }
        internal HandSide? Side { get; private set; }
        internal bool CalibrationReady
            => GuideAdjustmentProfiles.For(Kind, GuideAdjustMode.Curl).IsCalibrated;
        internal IReadOnlyList<GuideRotationTarget> Targets => _targets;

        internal bool Begin(PoseEditSnapshot snapshot, GuideAdjustmentKind kind, HandSide? side,
            GuideAdjustMode mode, IEnumerable<GuideRotationTarget> targets,
            GuideAdjustmentProfile profile, out string error)
        {
            error = null;
            Cancel();
            if (snapshot == null || snapshot.Character == null || snapshot.Kinematic == null)
            {
                error = "PoseCreate 上下文无效。";
                return false;
            }
            if (targets == null || targets.Any(target => target == null || target.Bone == null))
            {
                error = "Guide 目标为空。";
                return false;
            }
            // profile 参数保留兼容；实际双模式均从 GuideAdjustmentProfiles 取
            if ((profile == null || !profile.IsCalibrated)
                && !GuideAdjustmentProfiles.For(kind, mode).IsCalibrated)
            {
                error = "Guide 尚未完成真机校准。";
                return false;
            }
            _snapshot = snapshot;
            Kind = kind;
            Mode = mode;
            Side = side;
            _targets.AddRange(targets);
            _curlValue = 0f;
            _spreadValue = 0f;
            return true;
        }

        // 兼容旧签名：默认收拢
        internal bool Begin(PoseEditSnapshot snapshot, GuideAdjustmentKind kind, HandSide? side,
            IEnumerable<GuideRotationTarget> targets, GuideAdjustmentProfile profile, out string error)
            => Begin(snapshot, kind, side, GuideAdjustMode.Curl, targets, profile, out error);

        // 切展开/收拢：保留另一模式结果与基线，只换当前滑条通道
        internal void SetMode(GuideAdjustMode mode)
        {
            Mode = mode;
        }

        internal float GetModeValue(GuideAdjustMode mode)
            => mode == GuideAdjustMode.Curl ? _curlValue : _spreadValue;

        internal bool Preview(float value, out string error)
        {
            error = null;
            if (!IsActive)
            {
                error = "没有活动的 Guide 预览。";
                return false;
            }
            float clamped = GuideAdjustmentMath.ClampValue(value);
            if (Mode == GuideAdjustMode.Curl) _curlValue = clamped;
            else _spreadValue = clamped;
            return ApplyCombined(out error);
        }

        internal bool Reset(out string error)
        {
            error = null;
            if (!IsActive)
            {
                error = "没有活动的 Guide 预览。";
                return false;
            }
            try
            {
                RestoreBaseline();
                _curlValue = 0f;
                _spreadValue = 0f;
                return true;
            }
            catch (Exception ex)
            {
                error = "Guide 基线恢复失败: " + ex.Message;
                return false;
            }
        }

        internal bool Confirm(out string error)
        {
            error = null;
            if (!IsActive)
            {
                error = "没有活动的 Guide 预览。";
                return false;
            }
            Clear();
            return true;
        }

        // 双模式叠加：baseline * curlDelta * spreadDelta；无规则通道视为 identity
        private bool ApplyCombined(out string error)
        {
            error = null;
            GuideAdjustmentProfile curl = GuideAdjustmentProfiles.For(Kind, GuideAdjustMode.Curl);
            GuideAdjustmentProfile spread = GuideAdjustmentProfiles.For(Kind, GuideAdjustMode.Spread);
            try
            {
                foreach (GuideRotationTarget target in _targets)
                {
                    Quaternion curlDelta;
                    Quaternion spreadDelta;
                    if (!curl.TryGetDelta(target.ProfileKey, _curlValue, out curlDelta))
                        curlDelta = Quaternion.identity;
                    if (!spread.TryGetDelta(target.ProfileKey, _spreadValue, out spreadDelta))
                        spreadDelta = Quaternion.identity;
                    target.Rotation = GuideAdjustmentMath.FromBaseline(
                        target.Baseline, curlDelta * spreadDelta);
                }
                return true;
            }
            catch (Exception ex)
            {
                RestoreBaseline();
                error = "Guide 预览写入失败: " + ex.Message;
                return false;
            }
        }

        internal bool TryRefresh(PoseEditContext context, out string error)
        {
            error = null;
            if (!IsActive) return true;
            PoseEditSnapshot refreshed;
            if (context == null || !context.TryRefresh(_snapshot, out refreshed, out error))
            {
                Cancel();
                return false;
            }
            if (_targets.Any(target => !refreshed.Bones.Any(bone => ReferenceEquals(bone, target.Bone))))
            {
                error = "Guide 目标已离开当前 FK 上下文。";
                Cancel();
                return false;
            }
            _snapshot = refreshed;
            return true;
        }

        internal void Cancel()
        {
            if (!IsActive)
            {
                Clear();
                return;
            }
            try { RestoreBaseline(); }
            catch (Exception ex)
            {
                HEditPosePanelPlugin.Log?.LogWarning("[Guide] 回滚失败: " + ex.Message);
            }
            finally { Clear(); }
        }

        private void RestoreBaseline()
        {
            foreach (GuideRotationTarget target in _targets)
                target.Rotation = target.Baseline;
        }

        private void Clear()
        {
            _targets.Clear();
            _snapshot = null;
            _curlValue = 0f;
            _spreadValue = 0f;
            Side = null;
        }
    }
}
