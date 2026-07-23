using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Manager;
using Pose;

namespace EC_HEditPosePanel
{
    internal sealed class PoseEditSnapshot
    {
        internal PoseCreateScene Root;
        internal ChaControl Character;
        internal KinematicCtrl Kinematic;
        internal List<OCBone> Bones;
    }

    internal sealed class PoseEditContext
    {
        internal bool TryResolve(out PoseEditSnapshot snapshot, out string error)
        {
            snapshot = null;
            error = null;
            if (!HEditPosePanelPlugin.IsInPoseEdit())
            {
                error = "当前不在 PoseCreate。";
                return false;
            }

            PoseCreateScene root = Scene.GetRootComponent<PoseCreateScene>("PoseCreate");
            if (root == null)
            {
                error = "未找到 PoseCreate 上下文。";
                return false;
            }

            try
            {
                Traverse fields = Traverse.Create(root);
                KinematicCtrl kinematic = fields.Field("kinematicCtrl").GetValue<KinematicCtrl>();
                ChaControl character = fields.Field("chaControl").GetValue<ChaControl>();
                if (kinematic == null || character == null || kinematic.lstFKBone == null)
                {
                    error = "PoseCreate 角色或 FK 数据尚未就绪。";
                    return false;
                }
                snapshot = new PoseEditSnapshot
                {
                    Root = root,
                    Character = character,
                    Kinematic = kinematic,
                    Bones = kinematic.lstFKBone.Where(bone => bone != null).ToList()
                };
                return true;
            }
            catch (Exception ex)
            {
                error = "读取 PoseCreate 上下文失败: " + ex.Message;
                return false;
            }
        }

        internal bool IsCurrent(PoseEditSnapshot expected)
        {
            PoseEditSnapshot current;
            string error;
            return TryRefresh(expected, out current, out error);
        }

        internal bool TryRefresh(PoseEditSnapshot expected, out PoseEditSnapshot refreshed, out string error)
        {
            refreshed = null;
            error = null;
            if (expected == null)
            {
                error = "PoseCreate 上下文快照为空。";
                return false;
            }
            PoseEditSnapshot current;
            if (!TryResolve(out current, out error)) return false;
            if (!ReferenceEquals(expected.Root, current.Root)
                || !ReferenceEquals(expected.Character, current.Character)
                || !ReferenceEquals(expected.Kinematic, current.Kinematic))
            {
                error = "PoseCreate 角色或场景已变化。";
                return false;
            }
            refreshed = current;
            return true;
        }
    }
}
