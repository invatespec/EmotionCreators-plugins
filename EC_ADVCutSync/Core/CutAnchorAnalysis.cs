using System.Collections.Generic;
using System.Text;
using HEdit;
using Manager;
using AdvPart = HEdit.ADVPart;
using CharState = HEdit.ADVPart.CharState;
using ItemKind = EC_ADVCutSync.ItemKind;

namespace EC_ADVCutSync
{
    internal sealed class AnchorDetail
    {
        public ItemKind Kind;   // 跳转后面板定位目标栏目
        public string Text;
    }

    internal sealed class AnchorEntry
    {
        public int CutIndex;
        public int CharIndex;   // -1 = BGM（Part 级变化）
        public string Summary;
        public readonly List<AnchorDetail> Details = new List<AnchorDetail>();
    }

    internal sealed class AnalysisResult
    {
        public AdvPart Part;
        public int MiddleCount;
        public int CoordExplicit;
        public int ClothesExplicit;
        public int AccessoryExplicit;
        public int LiquidExplicit;
        public int VoiceExplicit;
        public int BgmExplicit;
        public bool NeedsOptimize;
        public readonly List<AnchorEntry> Entries = new List<AnchorEntry>();
    }

    // 变化点 = 与进入前生效状态不同的显式值（维持化后即剩显式保留点）+ 中间 cut 首次显示。
    // 每行一个 (cut, 角色) 或 BGM；明细按栏目合并成一条（同栏目跳同一处），元素按"旧→新"归组。
    internal static class CutAnchorAnalysis
    {
        // clothes[8] 槽位名（游戏顺序）
        private static readonly string[] ClothesSlots = { "上衣", "下衣", "胸罩", "内裤", "连裤袜", "手套", "袜子", "鞋子" };
        // liquid[5] 槽位名
        private static readonly string[] LiquidSlots = { "脸部", "胸部", "背部", "腹部", "屁股" };

        // 服装状态名：3态槽 0穿/1半/3脱；内裤 0穿/1半1/2半2/3脱；手套袜子鞋 0穿/3脱；未定义值回退数字
        private static string ClothesValueName(int slot, int value)
        {
            switch (value)
            {
                case 0: return "穿";
                case 1: return slot == 3 ? "半1" : (slot >= 5 ? value.ToString() : "半");
                case 2: return slot == 3 ? "半2" : value.ToString();
                case 3: return "脱";
                default: return value.ToString();
            }
        }

        private static string ClothesOldName(int slot, int value)
            => value >= 0 ? ClothesValueName(slot, value) : "？";

        public static AnalysisResult Analyze(AdvPart part)
        {
            var result = new AnalysisResult { Part = part };
            var cuts = part?.cuts;
            if (cuts == null)
                return result;
            result.MiddleCount = cuts.Count >= 2 ? cuts.Count - 2 : 0;

            for (var c = 1; c < cuts.Count - 1; c++)
            {
                var cut = cuts[c];
                for (var i = 0; i < cut.charStates.Count; i++)
                {
                    var cs = cut.charStates[i];
                    if (CutStateModel.IsExplicitCoordinate(cs.coordinate))
                        result.CoordExplicit++;
                    CountExplicit(cs.clothes, ref result.ClothesExplicit);
                    CountExplicit(cs.accessory, ref result.AccessoryExplicit);
                    CountExplicit(cs.liquid, ref result.LiquidExplicit);
                    if (CutStateModel.IsExplicitVoice(cs.voice))
                        result.VoiceExplicit++;
                }
                if (cut.cameraImageEffectInfo.bgmInfo.id != CutStateModel.InheritBgmId)
                    result.BgmExplicit++;
            }
            result.NeedsOptimize = CutSyncService.Estimate(part).Any;

            // (cutIndex, charIndex) → 变化集合；visitor 调用时链未推进，chain 即进入前生效状态。
            // 隐藏角色的显式值从未被应用、也不推进链，不列为变化点。
            var rows = new Dictionary<KeyValuePair<int, int>, Row>();
            CutStateModel.WalkMiddleCuts(part, (cut, c, i, cs, kind, elem, redundant, visible, chain, set) =>
            {
                if (redundant || !visible)
                    return;
                var key = new KeyValuePair<int, int>(c, i);
                if (!rows.TryGetValue(key, out var row))
                {
                    row = new Row();
                    rows[key] = row;
                }
                row.Kinds.Add(kind);
                switch (kind)
                {
                    case ItemKind.Clothes:
                        row.Items(kind).Add(new DetailItem
                        {
                            Element = "[" + ClothesSlots[elem] + "]",
                            OldV = ClothesOldName(elem, chain.Clothes[elem]),
                            NewV = ClothesValueName(elem, cs.clothes[elem])
                        });
                        break;
                    case ItemKind.Accessory:
                        row.Items(kind).Add(new DetailItem
                        {
                            Element = "[" + elem + "]",
                            OldV = FmtInt(chain.Accessory[elem]),
                            NewV = cs.accessory[elem].ToString()
                        });
                        break;
                    case ItemKind.Liquid:
                        row.Items(kind).Add(new DetailItem
                        {
                            Element = "[" + LiquidSlots[elem] + "]",
                            OldV = FmtInt(chain.Liquid[elem]),
                            NewV = cs.liquid[elem].ToString()
                        });
                        break;
                    case ItemKind.Coordinate:
                        row.Items(kind).Add(new DetailItem
                        {
                            Element = null,
                            OldV = chain.CoordKnown ? CoordLabel(chain.Coord) : "（未定义）",
                            NewV = CoordLabel(cs.coordinate)
                        });
                        break;
                    case ItemKind.Voice:
                        row.Items(kind).Add(new DetailItem
                        {
                            Element = null,
                            OldV = chain.VoiceKnown ? VoiceLabel(chain.VoiceRef.type) : "（未定义）",
                            NewV = VoiceLabel(cs.voice.type)
                        });
                        break;
                    case ItemKind.Bgm:
                        row.Items(kind).Add(new DetailItem
                        {
                            Element = null,
                            OldV = set.BgmKnown ? BgmName(set.BgmId) : "（未定义）",
                            NewV = BgmName(cut.cameraImageEffectInfo.bgmInfo.id)
                        });
                        break;
                }
            });

            foreach (var row in rows)
            {
                var entry = new AnchorEntry { CutIndex = row.Key.Key, CharIndex = row.Key.Value };
                entry.Summary = (row.Key.Value >= 0 ? NameOf(row.Key.Value) + "：" : "") + DescribeKinds(row.Value);
                BuildMergedDetails(entry, row.Value);
                result.Entries.Add(entry);
            }

            // 首 cut 未显示、后续首个设为显示的中间 cut；首 cut 已显示的角色不产生锚点
            var first = cuts[0];
            for (var i = 0; i < first.charStates.Count; i++)
            {
                if (first.charStates[i].visible)
                    continue;
                for (var c = 1; c < cuts.Count; c++)
                {
                    var states = cuts[c].charStates;
                    if (c <= cuts.Count - 2 && i < states.Count && states[i].visible)
                    {
                        var entry = new AnchorEntry { CutIndex = c, CharIndex = i, Summary = NameOf(i) + "：显示开始" };
                        entry.Details.Add(new AnchorDetail { Kind = ItemKind.Visible, Text = "显示:隐藏 → 显示" });
                        result.Entries.Add(entry);
                        break;
                    }
                }
            }

            result.Entries.Sort((a, b) => a.CutIndex != b.CutIndex ? a.CutIndex - b.CutIndex : a.CharIndex - b.CharIndex);
            return result;
        }

        private sealed class DetailItem
        {
            public string Element;  // null = 单体项（穿搭/音声/BGM）
            public string OldV;
            public string NewV;
        }

        private sealed class Row
        {
            public readonly HashSet<ItemKind> Kinds = new HashSet<ItemKind>();
            private readonly Dictionary<ItemKind, List<DetailItem>> items = new Dictionary<ItemKind, List<DetailItem>>();

            public List<DetailItem> Items(ItemKind kind)
            {
                if (!items.TryGetValue(kind, out var list))
                {
                    list = new List<DetailItem>();
                    items[kind] = list;
                }
                return list;
            }

            public bool TryItems(ItemKind kind, out List<DetailItem> list) => items.TryGetValue(kind, out list);
        }

        // 同栏目合并为一条明细（跳同一处）；元素按"旧→新"归组：服装:[上衣 下衣]？→脱；[内裤]？→半2
        private static void BuildMergedDetails(AnchorEntry entry, Row row)
        {
            AppendMerged(entry, ItemKind.Coordinate, "穿搭", row);
            AppendMerged(entry, ItemKind.Clothes, "服装", row);
            AppendMerged(entry, ItemKind.Accessory, "饰品", row);
            AppendMerged(entry, ItemKind.Liquid, "汁液", row);
            AppendMerged(entry, ItemKind.Voice, "音声", row);
            AppendMerged(entry, ItemKind.Bgm, "BGM", row);
        }

        private static void AppendMerged(AnchorEntry entry, ItemKind kind, string label, Row row)
        {
            if (!row.TryItems(kind, out var list) || list.Count == 0)
                return;
            if (list[0].Element == null)
            {
                var single = list[0];
                entry.Details.Add(new AnchorDetail
                {
                    Kind = kind,
                    Text = label + ":" + single.OldV + " → " + single.NewV
                });
                return;
            }
            var sb = new StringBuilder(label + ":");
            var firstGroup = true;
            var index = 0;
            while (index < list.Count)
            {
                var head = list[index];
                if (!firstGroup)
                    sb.Append("；");
                firstGroup = false;
                sb.Append('[').Append(head.Element.Trim('[', ']'));
                index++;
                while (index < list.Count && list[index].OldV == head.OldV && list[index].NewV == head.NewV)
                {
                    sb.Append(' ').Append(list[index].Element.Trim('[', ']'));
                    index++;
                }
                sb.Append(']').Append(head.OldV).Append('→').Append(head.NewV);
            }
            entry.Details.Add(new AnchorDetail { Kind = kind, Text = sb.ToString() });
        }

        private static string DescribeKinds(Row row)
        {
            var parts = new List<string>();
            if (row.Kinds.Contains(ItemKind.Coordinate)) parts.Add("穿搭变更");
            if (row.Kinds.Contains(ItemKind.Clothes)) parts.Add("服装变更");
            if (row.Kinds.Contains(ItemKind.Accessory)) parts.Add("饰品变更");
            if (row.Kinds.Contains(ItemKind.Liquid)) parts.Add("汁液变更");
            if (row.Kinds.Contains(ItemKind.Voice)) parts.Add("音声变更");
            if (row.Kinds.Contains(ItemKind.Bgm)) parts.Add("BGM 变更");
            return string.Join("、", parts.ToArray());
        }

        private static string CoordLabel(HEdit.ADVPart.CoordinateInfo c)
        {
            if (c.type == 1)
                return string.IsNullOrEmpty(c.name) ? "未命名穿搭" : "「" + c.name + "」";
            return c.type == 2 ? "原始穿搭" : "-";
        }

        private static string VoiceLabel(int type)
        {
            switch (type)
            {
                case 1: return "吐息";
                case 2: return "语音组";
                case 3: return "停止";
                default: return "无操作";
            }
        }

        internal static string BgmLabel(CutStateModel.ChainSet set, int newId)
        {
            var oldLabel = set.BgmKnown ? BgmName(set.BgmId) : "（未定义）";
            return oldLabel + " → " + BgmName(newId);
        }

        private static string BgmName(int id)
        {
            if (Singleton<HEditData>.IsInstance())
            {
                var info = Singleton<HEditData>.Instance.dicBGM;
                if (info != null && info.TryGetValue(id, out var bgm) && bgm.names != null)
                {
                    var lang = Singleton<GameSystem>.IsInstance() ? Singleton<GameSystem>.Instance.languageInt : 0;
                    if (lang >= 0 && lang < bgm.names.Count && !string.IsNullOrEmpty(bgm.names[lang]))
                        return bgm.names[lang];
                    if (bgm.names.Count > 0 && !string.IsNullOrEmpty(bgm.names[0]))
                        return bgm.names[0];
                }
            }
            return "id " + id;
        }

        internal static string NameOf(int charIndex)
        {
            if (Singleton<HEditData>.IsInstance())
            {
                var charas = Singleton<HEditData>.Instance.charas;
                if (charas != null && charIndex >= 0 && charIndex < charas.Count && charas[charIndex] != null)
                    return charas[charIndex].fileParam.fullname;
            }
            return "角色" + (charIndex + 1);
        }

        private static string FmtInt(int v) => v >= 0 ? v.ToString() : "？";

        private static void CountExplicit(int[] values, ref int counter)
        {
            foreach (var v in values)
            {
                if (v >= 0)
                    counter++;
            }
        }
    }
}
