using System.Collections.Generic;
using AdvPart = HEdit.ADVPart;
using CharState = HEdit.ADVPart.CharState;
using CoordinateInfo = HEdit.ADVPart.CoordinateInfo;
using Voice = HEdit.ADVPart.Voice;

namespace EC_ADVCutSync
{
    // 一次批量操作的还原点：只快照将被改写的字段（不含 pose），整条操作一次还原
    internal sealed class UndoSnapshot
    {
        private readonly AdvPart owner;
        private readonly HashSet<CharState> captured = new HashSet<CharState>();
        private readonly List<CharState> charStates = new List<CharState>();
        private readonly List<CharStateSnapshot> snapshots = new List<CharStateSnapshot>();
        private readonly List<AdvPart.Cut> cuts = new List<AdvPart.Cut>();
        private readonly List<AdvPart.Cut> bgmCuts = new List<AdvPart.Cut>();
        private readonly List<int> bgmIds = new List<int>();

        public UndoSnapshot(AdvPart owner) => this.owner = owner;

        public AdvPart Owner => owner;
        public bool HasAny => charStates.Count > 0 || bgmCuts.Count > 0;

        public List<AdvPart.Cut> AffectedCuts
        {
            get
            {
                var all = new List<AdvPart.Cut>(cuts);
                foreach (var cut in bgmCuts)
                {
                    if (!all.Contains(cut))
                        all.Add(cut);
                }
                return all;
            }
        }

        // 同一个 CharState 只抓一次：快照含全部可写字段，后续改写都在快照之后
        public void CaptureCharState(AdvPart.Cut cut, CharState cs)
        {
            if (!captured.Add(cs))
                return;
            cuts.Add(cut);
            charStates.Add(cs);
            snapshots.Add(CharStateSnapshot.Capture(cs));
        }

        public void CaptureBgm(AdvPart.Cut cut)
        {
            if (bgmCuts.Contains(cut))
                return;
            bgmCuts.Add(cut);
            bgmIds.Add(cut.cameraImageEffectInfo.bgmInfo.id);
        }

        public List<AdvPart.Cut> Restore()
        {
            for (var i = 0; i < charStates.Count; i++)
                snapshots[i].Restore(charStates[i]);
            for (var i = 0; i < bgmCuts.Count; i++)
                bgmCuts[i].cameraImageEffectInfo.bgmInfo.id = bgmIds[i];
            return AffectedCuts;
        }
    }

    internal sealed class CharStateSnapshot
    {
        private readonly CoordinateInfo coordinate;
        private readonly int[] clothes;
        private readonly int[] accessory;
        private readonly int[] liquid;
        private readonly Voice voice;

        private CharStateSnapshot(CoordinateInfo coordinate, int[] clothes, int[] accessory, int[] liquid, Voice voice)
        {
            this.coordinate = coordinate;
            this.clothes = clothes;
            this.accessory = accessory;
            this.liquid = liquid;
            this.voice = voice;
        }

        public static CharStateSnapshot Capture(CharState cs)
        {
            var coord = new CoordinateInfo();
            coord.Copy(cs.coordinate);
            var voice = new Voice();
            voice.Copy(cs.voice);
            return new CharStateSnapshot(coord, (int[])cs.clothes.Clone(), (int[])cs.accessory.Clone(),
                (int[])cs.liquid.Clone(), voice);
        }

        public void Restore(CharState cs)
        {
            cs.coordinate = coordinate;
            cs.voice = voice;
            for (var i = 0; i < clothes.Length; i++)
                cs.clothes[i] = clothes[i];
            for (var i = 0; i < accessory.Length; i++)
                cs.accessory[i] = accessory[i];
            for (var i = 0; i < liquid.Length; i++)
                cs.liquid[i] = liquid[i];
        }
    }

    internal static class UndoStore
    {
        public static UndoSnapshot Last;
    }
}
