using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Pose;
using UnityEngine;

namespace EC_HEditPosePanel
{
    internal sealed class PoseCommandOutcome
    {
        internal bool Success;
        internal bool NeedsOverwrite;
        internal int Applied;
        internal int Skipped;
        internal string Message;
    }

    internal sealed class FkPoseService
    {
        private readonly MonoBehaviour _owner;
        private readonly PoseLibrary _library;
        private readonly ThumbnailService _thumbnailService;
        private readonly Action _rollbackPreview;
        private readonly PoseEditContext _context = new PoseEditContext();
        private bool _busy;

        internal FkPoseService(MonoBehaviour owner, PoseLibrary library, ThumbnailService thumbnailService,
            Action rollbackPreview = null)
        {
            _owner = owner;
            _library = library;
            _thumbnailService = thumbnailService;
            _rollbackPreview = rollbackPreview;
        }

        internal bool IsBusy => _busy;

        internal void Save(PoseLibraryKind kind, PoseFolder folder, string name, HandSide? hand,
            bool overwrite, bool replaceThumbnail, Action<PoseCommandOutcome> completed)
        {
            if (_busy)
            {
                completed?.Invoke(Failure("另一个姿势操作正在进行。"));
                return;
            }
            PoseEditSnapshot snapshot;
            string error;
            if (!_context.TryResolve(out snapshot, out error))
            {
                completed?.Invoke(Failure(error));
                return;
            }
            if (kind == PoseLibraryKind.Hand && !hand.HasValue)
            {
                completed?.Invoke(Failure("未指定保存手侧。"));
                return;
            }
            if (kind == PoseLibraryKind.Hand
                && snapshot.Kinematic.GetHandMode(TargetIndex(hand.Value)) != HandState.HandEN.FK)
            {
                completed?.Invoke(Failure("保存手势前请先进入目标手 FK。"));
                return;
            }
            if (kind == PoseLibraryKind.Skirt && !snapshot.Kinematic.poseInfo.usedFKSkirt)
            {
                completed?.Invoke(Failure("保存裙子姿势前请先启用裙子 FK。"));
                return;
            }

            string dataPath;
            string thumbnailPath;
            if (!_library.TryGetPoseTargets(kind, folder, name, overwrite,
                out dataPath, out thumbnailPath, out error))
            {
                if (error == "姿势已存在，需要确认覆盖。")
                {
                    completed?.Invoke(new PoseCommandOutcome { NeedsOverwrite = true, Message = error });
                    return;
                }
                completed?.Invoke(Failure(error));
                return;
            }

            PartialPoseData data;
            if (!TryCapture(snapshot, kind, hand, out data, out error))
            {
                completed?.Invoke(Failure(error));
                return;
            }
            try
            {
                PartialPoseSerializer.WriteAtomic(dataPath, data);
            }
            catch (Exception ex)
            {
                completed?.Invoke(Failure("姿势保存失败: " + ex.Message));
                return;
            }

            // 覆盖且用户选择保留缩略图：只写数据，不碰 PNG
            if (overwrite && !replaceThumbnail)
            {
                completed?.Invoke(new PoseCommandOutcome
                {
                    Success = true,
                    Message = "姿势已保存（保留原缩略图）。"
                });
                return;
            }

            string thumbnailCleanupWarning = null;
            if (overwrite && System.IO.File.Exists(thumbnailPath))
            {
                try { System.IO.File.Delete(thumbnailPath); }
                catch (Exception ex) { thumbnailCleanupWarning = "旧缩略图清理失败: " + ex.Message; }
            }

            _busy = true;
            completed?.Invoke(new PoseCommandOutcome
            {
                Success = true,
                Message = string.IsNullOrEmpty(thumbnailCleanupWarning)
                    ? "姿势已保存，正在生成缩略图。"
                    : "姿势已保存，正在生成缩略图（" + thumbnailCleanupWarning + "）。"
            });
            if (_thumbnailService == null || !_thumbnailService.CaptureNextFrame(thumbnailPath, (result, captureError) =>
            {
                _busy = false;
                completed?.Invoke(new PoseCommandOutcome
                {
                    Success = result,
                    Message = result ? "姿势与缩略图已保存。" : "姿势已保存，缩略图失败: " + (captureError ?? "未知错误")
                });
            }))
            {
                _busy = false;
                completed?.Invoke(new PoseCommandOutcome
                {
                    Success = true,
                    Message = "姿势已保存，缩略图排队失败。"
                });
            }
        }

        internal void Apply(PoseLibraryKind kind, PoseEntry entry, HandSide? hand,
            Action<PoseCommandOutcome> completed)
        {
            if (_busy)
            {
                completed?.Invoke(Failure("另一个姿势操作正在进行。"));
                return;
            }
            if (entry == null)
            {
                completed?.Invoke(Failure("请先选择姿势。"));
                return;
            }
            if (kind == PoseLibraryKind.Hand && !hand.HasValue)
            {
                completed?.Invoke(Failure("未指定应用手侧。"));
                return;
            }
            try { _rollbackPreview?.Invoke(); }
            catch (Exception ex)
            {
                completed?.Invoke(Failure("回滚 Guide 预览失败: " + ex.Message));
                return;
            }
            _busy = true;
            try
            {
                _owner.StartCoroutine(ApplyRoutine(kind, entry, hand, completed));
            }
            catch (Exception ex)
            {
                _busy = false;
                completed?.Invoke(Failure("姿势应用启动失败: " + ex.Message));
            }
        }

        private IEnumerator ApplyRoutine(PoseLibraryKind kind, PoseEntry entry, HandSide? hand,
            Action<PoseCommandOutcome> completed)
        {
            PoseEditSnapshot snapshot;
            string error;
            if (!_context.TryResolve(out snapshot, out error))
            {
                Finish(completed, Failure(error));
                yield break;
            }
            PartialPoseData data;
            if (!PartialPoseSerializer.TryRead(entry.DataPath, kind, out data, out error))
            {
                Finish(completed, Failure("姿势读取失败: " + error));
                yield break;
            }

            List<RotationTarget> targets;
            int skipped;
            if (!TryBuildTargets(snapshot, kind, data, hand, out targets, out skipped, out error))
            {
                Finish(completed, Failure(error));
                yield break;
            }

            int targetIndex = hand.HasValue ? TargetIndex(hand.Value) : -1;
            bool handNeedsFk = kind == PoseLibraryKind.Hand
                && snapshot.Kinematic.GetHandMode(targetIndex) != HandState.HandEN.FK;
            bool skirtNeedsFk = kind == PoseLibraryKind.Skirt
                && !snapshot.Kinematic.poseInfo.usedFKSkirt;

            if (kind == PoseLibraryKind.Hand && handNeedsFk)
            {
                snapshot.Kinematic.CopyFKBone(hand.Value == HandSide.Left
                    ? OIBone.BoneGroup.LeftHand : OIBone.BoneGroup.RightHand);
                snapshot.Kinematic.SetHandMode(targetIndex, HandState.HandEN.FK);
                yield return new WaitForEndOfFrame();
                yield return null;
                PoseEditSnapshot refreshed;
                if (!_context.TryRefresh(snapshot, out refreshed, out error))
                {
                    Finish(completed, Failure("FK 切换期间 PoseCreate 上下文已变化。"));
                    yield break;
                }
                snapshot = refreshed;
            }
            if (kind == PoseLibraryKind.Skirt && skirtNeedsFk)
            {
                snapshot.Kinematic.SetUsedFKSkirt(true);
                yield return new WaitForEndOfFrame();
                yield return null;
                PoseEditSnapshot refreshed;
                if (!_context.TryRefresh(snapshot, out refreshed, out error))
                {
                    Finish(completed, Failure("裙子 FK 切换期间 PoseCreate 上下文已变化。"));
                    yield break;
                }
                snapshot = refreshed;
            }

            PoseEditSnapshot currentSnapshot;
            if (!_context.TryRefresh(snapshot, out currentSnapshot, out error))
            {
                Finish(completed, Failure("应用前 PoseCreate 上下文已变化。"));
                yield break;
            }
            snapshot = currentSnapshot;

            if (!TryBuildTargets(snapshot, kind, data, hand, out targets, out skipped, out error))
            {
                Finish(completed, Failure(error));
                yield break;
            }
            if (!TryApplyTargets(targets, out error))
            {
                Finish(completed, Failure(error));
                yield break;
            }

            Finish(completed, new PoseCommandOutcome
            {
                Success = true,
                Applied = targets.Count,
                Skipped = skipped,
                Message = skipped == 0
                    ? "姿势已应用。"
                    : string.Format("姿势已应用 {0} 个骨骼，跳过 {1} 个。", targets.Count, skipped)
            });
        }

        private bool TryCapture(PoseEditSnapshot snapshot, PoseLibraryKind kind, HandSide? hand,
            out PartialPoseData data, out string error)
        {
            data = null;
            error = null;
            if (kind == PoseLibraryKind.Hand)
            {
                List<HandBoneBinding> bindings;
                if (!HandPoseAdapter.TryCollect(snapshot.Bones, hand.Value, out bindings, out error)) return false;
                data = HandPoseAdapter.Capture(bindings, hand.Value);
                return true;
            }
            var bones = snapshot.Bones.Where(bone => bone.group == OIBone.BoneGroup.Skirt).ToList();
            if (bones.Count == 0) { error = "未找到裙子 FK 骨。"; return false; }
            data = new PartialPoseData { Kind = PoseLibraryKind.Skirt };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (OCBone bone in bones)
            {
                if (!seen.Add(bone.Name)) { error = "裙子骨骼键重复: " + bone.Name; return false; }
                data.Bones.Add(new PartialPoseBone
                {
                    Key = bone.Name,
                    Rotation = Quaternion.Euler(bone.localRotation)
                });
            }
            return true;
        }

        private bool TryBuildTargets(PoseEditSnapshot snapshot, PoseLibraryKind kind,
            PartialPoseData data, HandSide? hand, out List<RotationTarget> targets,
            out int skipped, out string error)
        {
            if (kind == PoseLibraryKind.Hand)
            {
                List<HandBoneBinding> bindings;
                if (!HandPoseAdapter.TryCollect(snapshot.Bones, hand.Value, out bindings, out error))
                {
                    targets = new List<RotationTarget>();
                    skipped = 0;
                    return false;
                }
                return HandPoseAdapter.TryBuildApplyMap(bindings, data, hand.Value,
                    out targets, out skipped, out error);
            }
            targets = new List<RotationTarget>();
            skipped = 0;
            var map = new Dictionary<string, OCBone>(StringComparer.Ordinal);
            foreach (OCBone bone in snapshot.Bones.Where(candidate => candidate.group == OIBone.BoneGroup.Skirt))
            {
                if (map.ContainsKey(bone.Name))
                {
                    error = "裙子目标骨骼键重复: " + bone.Name;
                    return false;
                }
                map.Add(bone.Name, bone);
            }
            foreach (PartialPoseBone source in data.Bones)
            {
                OCBone bone;
                if (!map.TryGetValue(source.Key, out bone)) { skipped++; continue; }
                targets.Add(new RotationTarget { Bone = bone, Rotation = source.Rotation });
            }
            if (targets.Count == 0) { error = "没有可匹配的裙子骨。"; return false; }
            error = null;
            return true;
        }

        private static bool TryApplyTargets(List<RotationTarget> targets, out string error)
        {
            error = null;
            var backups = targets.Select(target => new RotationBackup
            {
                Bone = target.Bone,
                Rotation = target.Bone.localRotation
            }).ToList();
            try
            {
                foreach (RotationTarget target in targets)
                    target.Bone.localRotation = target.Rotation.eulerAngles;
                return true;
            }
            catch (Exception ex)
            {
                foreach (RotationBackup backup in backups)
                {
                    try { backup.Bone.localRotation = backup.Rotation; }
                    catch { }
                }
                error = "姿势应用失败，已回滚: " + ex.Message;
                return false;
            }
        }

        private static PoseCommandOutcome Failure(string message)
            => new PoseCommandOutcome { Success = false, Message = message };

        private void Finish(Action<PoseCommandOutcome> completed, PoseCommandOutcome outcome)
        {
            _busy = false;
            completed?.Invoke(outcome);
        }

        private static int TargetIndex(HandSide side) => side == HandSide.Left ? 0 : 1;

        private sealed class RotationBackup
        {
            internal OCBone Bone;
            internal Vector3 Rotation;
        }
    }
}
