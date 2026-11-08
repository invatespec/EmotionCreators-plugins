using System.Collections.Generic;
using HEdit;
using AdvPart = HEdit.ADVPart;
using CharState = HEdit.ADVPart.CharState;
using CoordinateInfo = HEdit.ADVPart.CoordinateInfo;
using Cut = HEdit.ADVPart.Cut;
using Voice = HEdit.ADVPart.Voice;

namespace EC_ADVCutSync
{
    internal enum ItemKind
    {
        Coordinate,
        Clothes,
        Accessory,
        Liquid,
        Voice,
        Bgm,
        Visible
    }

    // 各状态的维持语义：服装 type0("-")、穿脱/饰品/汁液 -1、音声 type0（无操作）、BGM 引き継ぐ（空 asset 条目）
    internal static class CutStateModel
    {
        public const int MaintainInt = -1;
        public const byte MaintainCoordType = 0;
        public const int MaintainVoiceType = 0;

        private static int inheritBgmId = -1;

        // 数据层没有固定 id：按“空 asset 且非停止”识别继承条目，标准数据为 id 0（引き継ぐ）
        internal static int InheritBgmId
        {
            get
            {
                if (inheritBgmId >= 0)
                    return inheritBgmId;
                var dic = Singleton<HEditData>.Instance.dicBGM;
                foreach (var kv in dic)
                {
                    var info = kv.Value;
                    if (info != null && !info.isStop && string.IsNullOrEmpty(info.asset.asset))
                    {
                        inheritBgmId = kv.Key;
                        return inheritBgmId;
                    }
                }
                inheritBgmId = 0;
                return inheritBgmId;
            }
        }

        internal static void InvalidateBgmCache() => inheritBgmId = -1;

        internal static bool IsExplicitCoordinate(CoordinateInfo c)
            => c != null && (c.type == 1 || c.type == 2);

        internal static bool CoordinateEquals(CoordinateInfo a, CoordinateInfo b)
        {
            if (a.type != b.type)
                return false;
            if (a.type != 1)
                return true;
            if (a.name != b.name || a.version != b.version)
                return false;
            if ((a.data == null) != (b.data == null) || (a.data != null && a.data.Length != b.data.Length))
                return false;
            if (a.data == null)
                return true;
            for (var i = 0; i < a.data.Length; i++)
            {
                if (a.data[i] != b.data[i])
                    return false;
            }
            return true;
        }

        internal static bool IsExplicitVoice(Voice v) => v != null && v.type != MaintainVoiceType;

        internal static bool VoiceEquals(Voice a, Voice b)
        {
            if (a.type != b.type)
                return false;
            switch (a.type)
            {
                case 1:
                    return a.breathData.category == b.breathData.category && a.breathData.id == b.breathData.id;
                case 2:
                    return VoiceSetEquals(a.voiceSet, b.voiceSet);
                default:
                    return true;
            }
        }

        private static bool VoiceSetEquals(Voice.VoiceSet a, Voice.VoiceSet b)
        {
            if (a == null || b == null || a.datas.Count != b.datas.Count)
                return false;
            for (var i = 0; i < a.datas.Count; i++)
            {
                if (a.datas[i].category != b.datas[i].category
                    || a.datas[i].id != b.datas[i].id
                    || a.datas[i].state != b.datas[i].state)
                    return false;
            }
            return true;
        }

        // 单个角色的生效状态链：只推进“可见 cut 的显式值”——隐藏角色整段跳过应用，其显式值从未生效。
        // 数组元素各自独立追踪（-1 表示尚无可用的显式值）。
        internal sealed class EffectiveChain
        {
            public readonly int[] Clothes = NewArray(8);
            public readonly int[] Accessory = NewArray(20);
            public readonly int[] Liquid = NewArray(5);
            public CoordinateInfo Coord;
            public bool CoordKnown;
            public Voice VoiceRef;
            public bool VoiceKnown;

            private static int[] NewArray(int length)
            {
                var array = new int[length];
                for (var i = 0; i < length; i++)
                    array[i] = MaintainInt;
                return array;
            }

            // 仅可见首cut的显式值可作链起点
            public void Seed(CharState cs, bool visible)
            {
                if (!visible)
                    return;
                SeedOrAdvance(cs);
            }

            public void Advance(CharState cs, bool visible)
            {
                if (!visible)
                    return;
                SeedOrAdvance(cs);
            }

            private void SeedOrAdvance(CharState cs)
            {
                for (var j = 0; j < cs.clothes.Length; j++)
                {
                    if (cs.clothes[j] >= 0)
                        Clothes[j] = cs.clothes[j];
                }
                for (var j = 0; j < cs.accessory.Length; j++)
                {
                    if (cs.accessory[j] >= 0)
                        Accessory[j] = cs.accessory[j];
                }
                for (var j = 0; j < cs.liquid.Length; j++)
                {
                    if (cs.liquid[j] >= 0)
                        Liquid[j] = cs.liquid[j];
                }
                if (IsExplicitCoordinate(cs.coordinate))
                {
                    Coord = cs.coordinate;
                    CoordKnown = true;
                }
                if (IsExplicitVoice(cs.voice))
                {
                    VoiceRef = cs.voice;
                    VoiceKnown = true;
                }
            }
        }

        // 全 part 链集：每角色一条（互不污染）+ BGM 一条（BGM 不分角色，每 cut 必应用）
        internal sealed class ChainSet
        {
            private readonly EffectiveChain[] indexed;
            public int BgmId;
            public bool BgmKnown;

            public ChainSet(int charCount)
            {
                indexed = new EffectiveChain[charCount];
                for (var i = 0; i < charCount; i++)
                    indexed[i] = new EffectiveChain();
            }

            public EffectiveChain CharAt(int charIndex)
                => charIndex >= 0 && charIndex < indexed.Length ? indexed[charIndex] : null;
        }

        internal delegate void WalkVisitor(
            AdvPart.Cut cut, int cutIndex, int charIndex, CharState cs,
            ItemKind kind, int elementIndex, bool redundant, bool visible, EffectiveChain chain, ChainSet set);

        // 遍历中间 cut：redundant = 该显式值与进入前生效状态相同（可转维持）。
        // visitor 调用时链尚未推进，chain 即“进入该 cut 前的生效状态”（该角色自己的链）。
        // 返回走完中间 cut 后的链集，供尾cut快照/流型维持化使用。
        internal static ChainSet WalkMiddleCuts(AdvPart part, WalkVisitor visitor)
        {
            var cuts = part?.cuts;
            var set = new ChainSet(cuts != null && cuts.Count > 0 ? cuts[0].charStates.Count : 0);
            if (cuts == null || cuts.Count < 2)
                return set;
            var inheritId = InheritBgmId;
            var first = cuts[0];
            for (var i = 0; i < first.charStates.Count; i++)
                set.CharAt(i).Seed(first.charStates[i], first.charStates[i].visible);
            if (first.cameraImageEffectInfo.bgmInfo.id != inheritId)
            {
                set.BgmId = first.cameraImageEffectInfo.bgmInfo.id;
                set.BgmKnown = true;
            }

            for (var c = 1; c < cuts.Count - 1; c++)
            {
                var cut = cuts[c];
                for (var i = 0; i < cut.charStates.Count; i++)
                {
                    var cs = cut.charStates[i];
                    var chain = set.CharAt(i);
                    // 异常数据（中间cut角色数多于首cut）时无链可走，跳过该角色避免 NRE
                    if (chain == null)
                        continue;
                    var visible = cs.visible;
                    for (var j = 0; j < cs.clothes.Length; j++)
                    {
                        var v = cs.clothes[j];
                        if (v < 0)
                            continue;
                        visitor(cut, c, i, cs, ItemKind.Clothes, j, chain.Clothes[j] == v, visible, chain, set);
                    }
                    for (var j = 0; j < cs.accessory.Length; j++)
                    {
                        var v = cs.accessory[j];
                        if (v < 0)
                            continue;
                        visitor(cut, c, i, cs, ItemKind.Accessory, j, chain.Accessory[j] == v, visible, chain, set);
                    }
                    for (var j = 0; j < cs.liquid.Length; j++)
                    {
                        var v = cs.liquid[j];
                        if (v < 0)
                            continue;
                        visitor(cut, c, i, cs, ItemKind.Liquid, j, chain.Liquid[j] == v, visible, chain, set);
                    }
                    if (IsExplicitCoordinate(cs.coordinate))
                    {
                        visitor(cut, c, i, cs, ItemKind.Coordinate, -1,
                            chain.CoordKnown && CoordinateEquals(chain.Coord, cs.coordinate), visible, chain, set);
                    }
                    if (IsExplicitVoice(cs.voice))
                    {
                        visitor(cut, c, i, cs, ItemKind.Voice, -1,
                            chain.VoiceKnown && VoiceEquals(chain.VoiceRef, cs.voice), visible, chain, set);
                    }
                    // 本 cut 全部判定完成后一次性推进该角色的链
                    chain.Advance(cs, visible);
                }

                var bgm = cut.cameraImageEffectInfo.bgmInfo.id;
                if (bgm != inheritId)
                {
                    var redundant = set.BgmKnown && set.BgmId == bgm;
                    visitor(cut, c, -1, null, ItemKind.Bgm, -1, redundant, true, null, set);
                    if (!redundant)
                    {
                        set.BgmId = bgm;
                        set.BgmKnown = true;
                    }
                }
            }
            return set;
        }
    }
}
