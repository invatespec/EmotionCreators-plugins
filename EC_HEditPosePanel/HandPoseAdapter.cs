using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Pose;
using UnityEngine;

namespace EC_HEditPosePanel
{
    internal enum HandSide
    {
        Left,
        Right
    }

    internal sealed class HandBoneBinding
    {
        internal string CanonicalKey;
        internal OCBone Bone;
        internal int SegmentIndex;
    }

    internal static class HandPoseAdapter
    {
        // EC 实测左右手镜像使用欧拉角映射；校准结果稳定后只允许通过格式版本变更此规则。
        private static Quaternion MirrorAnatomical(Quaternion rotation)
        {
            Vector3 euler = rotation.eulerAngles;
            float x;
            float y;
            float z;
            MirrorEuler(euler.x, euler.y, euler.z, out x, out y, out z);
            return Quaternion.Euler(x, y, z);
        }

        internal static Quaternion ToCanonical(HandSide source, Quaternion rotation, int segmentIndex)
            => source == HandSide.Left || segmentIndex != 0 ? rotation : MirrorAnatomical(rotation);

        internal static Quaternion FromCanonical(HandSide target, Quaternion rotation, int segmentIndex)
            => target == HandSide.Left || segmentIndex != 0 ? rotation : MirrorAnatomical(rotation);

        internal static bool ShouldMirrorSegmentForRegression(int segmentIndex)
            => segmentIndex == 0;

        internal static void MirrorEulerForRegression(float x, float y, float z,
            out float mirroredX, out float mirroredY, out float mirroredZ)
            => MirrorEuler(x, y, z, out mirroredX, out mirroredY, out mirroredZ);

        private static void MirrorEuler(float x, float y, float z,
            out float mirroredX, out float mirroredY, out float mirroredZ)
        {
            mirroredX = NormalizeDegrees(-x);
            mirroredY = NormalizeDegrees(180f - y);
            mirroredZ = NormalizeDegrees(180f + z);
        }

        private static float NormalizeDegrees(float value)
        {
            value %= 360f;
            return value < 0f ? value + 360f : value;
        }

        internal static bool TryGetFingerKeyForRegression(string rawName, HandSide side,
            out string canonical, out string finger, out int segment)
            => TryGetFingerKey(rawName, side, out canonical, out finger, out segment);

        internal static bool TryCollect(IEnumerable<OCBone> bones, HandSide side,
            out List<HandBoneBinding> bindings, out string error)
        {
            bindings = new List<HandBoneBinding>();
            error = null;
            if (bones == null)
            {
                error = "手部 FK 骨列表为空。";
                return false;
            }

            OIBone.BoneGroup group = side == HandSide.Left
                ? OIBone.BoneGroup.LeftHand : OIBone.BoneGroup.RightHand;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (OCBone bone in bones)
            {
                if (bone == null || bone.group != group) continue;
                string canonical;
                string finger;
                int segment;
                if (!TryGetFingerKey(bone.Name, side, out canonical, out finger, out segment)) continue;
                if (!seen.Add(canonical))
                {
                    error = "手势骨骼键重复: " + canonical;
                    bindings.Clear();
                    return false;
                }
                bindings.Add(new HandBoneBinding
                {
                    CanonicalKey = canonical,
                    Bone = bone,
                    SegmentIndex = segment
                });
            }
            bindings.Sort((left, right) => StringComparer.Ordinal.Compare(left.CanonicalKey, right.CanonicalKey));
            if (bindings.Count == 0) error = "未找到可用手指 FK 骨。";
            return bindings.Count > 0;
        }

        internal static PartialPoseData Capture(IEnumerable<HandBoneBinding> bindings, HandSide source)
        {
            var data = new PartialPoseData { Kind = PoseLibraryKind.Hand };
            foreach (HandBoneBinding binding in bindings)
            {
                Quaternion rotation = Quaternion.Euler(binding.Bone.localRotation);
                data.Bones.Add(new PartialPoseBone
                {
                    Key = binding.CanonicalKey,
                    Rotation = ToCanonical(source, rotation, binding.SegmentIndex)
                });
            }
            return data;
        }

        internal static bool TryBuildApplyMap(IEnumerable<HandBoneBinding> bindings,
            PartialPoseData data, HandSide target, out List<RotationTarget> targets,
            out int skipped, out string error)
        {
            targets = new List<RotationTarget>();
            skipped = 0;
            error = null;
            if (data == null || data.Kind != PoseLibraryKind.Hand)
            {
                error = "姿势类型不是手势。";
                return false;
            }
            var map = bindings.ToDictionary(binding => binding.CanonicalKey, StringComparer.Ordinal);
            foreach (PartialPoseBone source in data.Bones)
            {
                HandBoneBinding targetBinding;
                if (!map.TryGetValue(source.Key, out targetBinding))
                {
                    skipped++;
                    continue;
                }
                targets.Add(new RotationTarget
                {
                    Bone = targetBinding.Bone,
                    Rotation = FromCanonical(target, source.Rotation, targetBinding.SegmentIndex)
                });
            }
            if (targets.Count == 0)
            {
                error = "没有可匹配的目标手骨。";
                return false;
            }
            return true;
        }

        private static bool TryGetFingerKey(string rawName, HandSide side,
            out string canonical, out string finger, out int segment)
        {
            canonical = null;
            finger = null;
            segment = 0;
            if (string.IsNullOrEmpty(rawName)) return false;
            string suffix = side == HandSide.Left ? "_L" : "_R";
            if (!rawName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
            string baseName = rawName.Substring(0, rawName.Length - suffix.Length);
            string lower = baseName.ToLowerInvariant();
            foreach (string candidate in new[] { "thumb", "index", "middle", "ring", "little" })
            {
                int position = lower.LastIndexOf(candidate, StringComparison.Ordinal);
                if (position < 0) continue;
                int numberStart = position + candidate.Length;
                if (numberStart + 2 > lower.Length) continue;
                int parsed;
                if (!int.TryParse(lower.Substring(numberStart, 2), NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
                    || parsed < 1 || parsed > 3)
                    continue;
                if (numberStart + 2 != lower.Length) continue;
                canonical = baseName;
                finger = candidate;
                segment = parsed - 1;
                return true;
            }
            return false;
        }
    }

    internal sealed class RotationTarget
    {
        internal OCBone Bone;
        internal Quaternion Rotation;
    }
}
