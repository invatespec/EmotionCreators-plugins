using System.Collections.Generic;
using AdvPart = HEdit.ADVPart;
using CharState = HEdit.ADVPart.CharState;
using CoordinateInfo = HEdit.ADVPart.CoordinateInfo;
using Cut = HEdit.ADVPart.Cut;

namespace EC_ADVCutSync
{
    internal sealed class SyncResult
    {
        public int CoordMaintained;
        public int ClothesMaintained;
        public int AccessoryMaintained;
        public int LiquidMaintained;
        public int VoiceMaintained;
        public int BgmMaintained;
        public int TailSnapshotItems;
        public UndoSnapshot Snapshot;

        public bool Any
            => CoordMaintained + ClothesMaintained + AccessoryMaintained + LiquidMaintained
               + VoiceMaintained + BgmMaintained + TailSnapshotItems > 0;

        public string Describe()
        {
            var items = new List<string>();
            if (TailSnapshotItems > 0) items.Add("尾cut快照 " + TailSnapshotItems);
            if (CoordMaintained > 0) items.Add("服装 " + CoordMaintained);
            if (ClothesMaintained > 0) items.Add("穿脱 " + ClothesMaintained);
            if (AccessoryMaintained > 0) items.Add("饰品 " + AccessoryMaintained);
            if (LiquidMaintained > 0) items.Add("汁液 " + LiquidMaintained);
            if (VoiceMaintained > 0) items.Add("音声 " + VoiceMaintained);
            if (BgmMaintained > 0) items.Add("BGM " + BgmMaintained);
            return items.Count == 0 ? "无冗余显式值" : string.Join("、", items.ToArray());
        }
    }

    internal static class CutSyncService
    {
        // 与 Run 同判定但不写数据；Any=false 即"无需优化"
        public static SyncResult Estimate(AdvPart part)
        {
            var result = new SyncResult();
            var cuts = part?.cuts;
            if (cuts == null || cuts.Count < 2)
                return result;
            var set = CutStateModel.WalkMiddleCuts(part, (cut, c, i, cs, kind, elem, redundant, visible, chain, chainSet) =>
                CollectVisit(cut, cs, kind, elem, redundant, visible, result, null, false));
            ApplyTail(cuts, set, result, null, false);
            return result;
        }

        // 中间cut冗余显式值转维持 + 尾cut溯源快照/流型维持化；先快照再改写
        public static SyncResult Run(AdvPart part)
        {
            var cuts = part?.cuts;
            if (cuts == null || cuts.Count < 2)
                return null;

            var snap = new UndoSnapshot(part);
            var result = new SyncResult();
            var set = CutStateModel.WalkMiddleCuts(part, (cut, c, i, cs, kind, elem, redundant, visible, chain, chainSet) =>
                CollectVisit(cut, cs, kind, elem, redundant, visible, result, snap, true));
            ApplyTail(cuts, set, result, snap, true);

            result.Snapshot = snap.HasAny ? snap : null;
            return result;
        }

        private static void CollectVisit(Cut cut, CharState cs, ItemKind kind, int elem,
            bool redundant, bool visible, SyncResult result, UndoSnapshot snap, bool write)
        {
            if (!redundant || !visible)
                return;
            switch (kind)
            {
                case ItemKind.Coordinate:
                    if (write)
                    {
                        snap.CaptureCharState(cut, cs);
                        cs.coordinate.type = CutStateModel.MaintainCoordType;
                    }
                    result.CoordMaintained++;
                    break;
                case ItemKind.Clothes:
                    if (write)
                    {
                        snap.CaptureCharState(cut, cs);
                        cs.clothes[elem] = CutStateModel.MaintainInt;
                    }
                    result.ClothesMaintained++;
                    break;
                case ItemKind.Accessory:
                    if (write)
                    {
                        snap.CaptureCharState(cut, cs);
                        cs.accessory[elem] = CutStateModel.MaintainInt;
                    }
                    result.AccessoryMaintained++;
                    break;
                case ItemKind.Liquid:
                    if (write)
                    {
                        snap.CaptureCharState(cut, cs);
                        cs.liquid[elem] = CutStateModel.MaintainInt;
                    }
                    result.LiquidMaintained++;
                    break;
                case ItemKind.Voice:
                    if (write)
                    {
                        snap.CaptureCharState(cut, cs);
                        cs.voice.type = CutStateModel.MaintainVoiceType;
                    }
                    result.VoiceMaintained++;
                    break;
                case ItemKind.Bgm:
                    if (write)
                    {
                        snap.CaptureBgm(cut);
                        cut.cameraImageEffectInfo.bgmInfo.id = CutStateModel.InheritBgmId;
                    }
                    result.BgmMaintained++;
                    break;
            }
        }

        // 尾cut：状态型项溯源快照（维持项 ← 可见链最晚显式值，含尾cut自身显式参与链的语义由
        // “尾cut显式项不动”保证）；流型项并入维持化（冗余显式转维持，真实切换保留）
        private static void ApplyTail(List<Cut> cuts, CutStateModel.ChainSet set, SyncResult result, UndoSnapshot snap, bool write)
        {
            var tail = cuts[cuts.Count - 1];
            var inheritId = CutStateModel.InheritBgmId;

            // 流型：BGM（每 cut 必应用）
            var tailBgm = tail.cameraImageEffectInfo.bgmInfo.id;
            if (tailBgm != inheritId && set.BgmKnown && set.BgmId == tailBgm)
            {
                if (write)
                {
                    snap.CaptureBgm(tail);
                    tail.cameraImageEffectInfo.bgmInfo.id = inheritId;
                }
                result.BgmMaintained++;
            }

            for (var i = 0; i < tail.charStates.Count; i++)
            {
                var cs = tail.charStates[i];
                var chain = set.CharAt(i);
                if (chain == null)
                    continue;

                // 流型：音声（仅尾cut显示的角色会被应用）
                if (cs.visible && CutStateModel.IsExplicitVoice(cs.voice)
                    && chain.VoiceKnown && CutStateModel.VoiceEquals(chain.VoiceRef, cs.voice))
                {
                    if (write)
                    {
                        snap.CaptureCharState(tail, cs);
                        cs.voice.type = CutStateModel.MaintainVoiceType;
                    }
                    result.VoiceMaintained++;
                }

                // 状态型快照：只升级维持项；尾cut已显式的项是最晚改动，一律不动
                if (!cs.visible)
                    continue;
                if (cs.coordinate.type == CutStateModel.MaintainCoordType && chain.CoordKnown)
                {
                    if (write)
                    {
                        var copy = new CoordinateInfo();
                        copy.Copy(chain.Coord);
                        snap.CaptureCharState(tail, cs);
                        cs.coordinate = copy;
                    }
                    result.TailSnapshotItems++;
                }
                SnapshotInts(cs.clothes, chain.Clothes, tail, cs, snap, write, result);
                SnapshotInts(cs.accessory, chain.Accessory, tail, cs, snap, write, result);
                SnapshotInts(cs.liquid, chain.Liquid, tail, cs, snap, write, result);
            }
        }

        private static void SnapshotInts(int[] values, int[] chainValues, Cut tail, CharState cs,
            UndoSnapshot snap, bool write, SyncResult result)
        {
            for (var j = 0; j < values.Length; j++)
            {
                if (values[j] == CutStateModel.MaintainInt && chainValues[j] >= 0)
                {
                    if (write)
                    {
                        snap.CaptureCharState(tail, cs);
                        values[j] = chainValues[j];
                    }
                    result.TailSnapshotItems++;
                }
            }
        }
    }
}
