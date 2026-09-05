using System;
using System.Collections.Generic;
using System.Linq;
using Pose;
using UnityEngine;

namespace EC_HEditPosePanel
{
    // 复制粒度：单个 guide / 整串（选中节 + 后面末端方向的节，不含前面的）
    internal enum GuideCopyScope
    {
        Single,
        Chain
    }

    // guide 复制 / 反向粘贴。剪贴板只存局部旋转（Quaternion），不持骨骼引用；记录来源 kind 防跨页签误贴。
    internal sealed class GuideCopyService
    {
        private readonly List<Quaternion> _clipboard = new List<Quaternion>();
        private GuideAdjustmentKind _kind;

        internal bool HasClipboard => _clipboard.Count > 0;

        // 裙子左右镜像（真机实测）：x 不变，y/z 取负并归一化到 [0,360)
        internal static Quaternion MirrorSkirt(Quaternion rotation)
        {
            Vector3 e = rotation.eulerAngles;
            return Quaternion.Euler(e.x, Norm(-e.y), Norm(-e.z));
        }

        // 手部左右镜像（复用保存/应用结论）：仅手指根节（0 号节）镜像；1/2 号节原样
        internal static Quaternion MirrorHandRoot(Quaternion rotation)
        {
            Vector3 e = rotation.eulerAngles;
            return Quaternion.Euler(Norm(-e.x), Norm(180f - e.y), Norm(180f + e.z));
        }

        private static float Norm(float value)
        {
            value %= 360f;
            return value < 0f ? value + 360f : value;
        }

        internal bool TryCopy(GuideAdjustmentKind kind, OCBone selected, IEnumerable<OCBone> bones,
            GuideCopyScope scope, out string error)
        {
            error = null;
            _kind = kind;
            _clipboard.Clear();
            if (!TryValidateSelection(kind, selected, out error)) return false;
            List<OCBone> chain;
            if (!TryCollect(kind, selected, bones, scope, out chain, out error)) return false;
            foreach (OCBone bone in chain)
                _clipboard.Add(Quaternion.Euler(bone.localRotation));
            return true;
        }

        internal bool TryReversePaste(GuideAdjustmentKind kind, OCBone selected, IEnumerable<OCBone> bones,
            GuideCopyScope scope, out string error)
        {
            error = null;
            if (!HasClipboard) { error = "剪贴板为空，请先复制。"; return false; }
            if (_kind != kind) { error = "剪贴板是另一页签复制的，请回到对应页签。"; return false; }
            if (!TryValidateSelection(kind, selected, out error)) return false;
            List<OCBone> chain;
            if (!TryCollect(kind, selected, bones, scope, out chain, out error)) return false;
            int count = Math.Min(_clipboard.Count, chain.Count);
            for (int i = 0; i < count; i++)
            {
                Quaternion mirrored = MirrorFor(kind, chain[i], _clipboard[i]);
                chain[i].localRotation = mirrored.eulerAngles;
            }
            return true;
        }

        private static bool TryValidateSelection(GuideAdjustmentKind kind, OCBone selected, out string error)
        {
            error = null;
            if (kind == GuideAdjustmentKind.Skirt)
            {
                if (selected == null || selected.group != OIBone.BoneGroup.Skirt) { error = "请先选择裙子 FK Guide。"; return false; }
            }
            else
            {
                if (selected == null || (selected.group != OIBone.BoneGroup.LeftHand && selected.group != OIBone.BoneGroup.RightHand))
                { error = "请先选择手指 FK Guide。"; return false; }
            }
            return true;
        }

        private static bool TryCollect(GuideAdjustmentKind kind, OCBone selected, IEnumerable<OCBone> bones,
            GuideCopyScope scope, out List<OCBone> chain, out string error)
        {
            chain = new List<OCBone>();
            error = null;
            if (scope == GuideCopyScope.Single)
            {
                chain.Add(selected);
                return true;
            }
            if (kind == GuideAdjustmentKind.Skirt)
            {
                List<GuideRotationTarget> targets;
                if (!GuideTargetDiscovery.TryCollectSkirt(selected, bones, out targets, out error)) return false;
                chain = targets.Select(target => target.Bone).ToList();
                return true;
            }
            return TryCollectHandChain(selected, bones, out chain, out error);
        }

        // 手部整串：选中节所属手指内，段编号 >= 选中段的节，按段号升序（不依赖骨骼父子链）
        private static bool TryCollectHandChain(OCBone selected, IEnumerable<OCBone> bones,
            out List<OCBone> chain, out string error)
        {
            chain = new List<OCBone>();
            error = null;
            string finger;
            int selectedSegment;
            OIBone.BoneGroup group;
            if (!TryParseHandBone(selected, out finger, out selectedSegment, out group))
            {
                error = "无法解析手指 guide 名称。";
                return false;
            }
            var collected = new List<KeyValuePair<OCBone, int>>();
            foreach (OCBone bone in bones)
            {
                if (bone == null || bone.group != group) continue;
                string f;
                int seg;
                OIBone.BoneGroup ignored;
                if (!TryParseHandBone(bone, out f, out seg, out ignored)) continue;
                if (f != finger || seg < selectedSegment) continue;
                collected.Add(new KeyValuePair<OCBone, int>(bone, seg));
            }
            if (collected.Count == 0)
            {
                error = "当前手指没有可复制/粘贴的节。";
                return false;
            }
            collected.Sort((left, right) => left.Value.CompareTo(right.Value));
            chain = collected.Select(pair => pair.Key).ToList();
            return true;
        }

        private static bool TryParseHandBone(OCBone bone, out string finger, out int segment, out OIBone.BoneGroup group)
        {
            finger = null;
            segment = -1;
            group = default(OIBone.BoneGroup);
            if (bone == null) return false;
            string ignored;
            if (HandPoseAdapter.TryGetFingerKeyForRegression(bone.Name, HandSide.Left, out ignored, out finger, out segment))
            {
                group = OIBone.BoneGroup.LeftHand;
                return true;
            }
            if (HandPoseAdapter.TryGetFingerKeyForRegression(bone.Name, HandSide.Right, out ignored, out finger, out segment))
            {
                group = OIBone.BoneGroup.RightHand;
                return true;
            }
            return false;
        }

        // 粘贴镜像：裙子全部镜像；手部仅手指根节（0 号节）镜像
        private static Quaternion MirrorFor(GuideAdjustmentKind kind, OCBone target, Quaternion source)
        {
            if (kind == GuideAdjustmentKind.Skirt) return MirrorSkirt(source);
            string finger;
            int segment;
            OIBone.BoneGroup group;
            return TryParseHandBone(target, out finger, out segment, out group) && segment == 0
                ? MirrorHandRoot(source)
                : source;
        }
    }
}