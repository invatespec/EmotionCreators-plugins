using System.Collections.Generic;
using ADVPart.List;
using ADVPart.Manipulate;
using UnityEngine;

namespace EC_ADVBatchTransform
{
    using SortOrder = HEdit.ADVPart.SortOrder;
    using SpeechBubbles = HEdit.ADVPart.SpeechBubbles;
    using ScreenEffect = HEdit.ADVPart.ScreenEffect;

    internal abstract class UndoEntry
    {
        internal abstract void Restore();

        // 回放后刷新面板，避免显示的值和场景不一致
        protected static void RefreshPanel() => Items.RefreshPrimaryFullPanel();
    }

    // 原生操作与插件操作共用一个时间线，Ctrl+Z 按倒序回放。
    // 一次拖动/一次输入只提交一条，所以不需要按时间合并
    internal static class UndoStack
    {
        private static readonly List<UndoEntry> entries = new List<UndoEntry>();

        // 回放期间关闭记录，避免撤销写入被重新记录
        internal static bool Executing { get; private set; }

        // 回放或清空后数据被整体改写，面板基线据此重建
        internal static int Version { get; private set; }

        internal static void Push(UndoEntry entry)
        {
            if (entry == null || Executing) return;
            entries.Add(entry);
            int overflow = entries.Count - ADVBatchTransformPlugin.UndoLimit.Value;
            if (overflow > 0) entries.RemoveRange(0, overflow);
        }

        // 回放失败时保留记录：写入的都是绝对旧值，重复回放幂等
        internal static void Undo()
        {
            if (entries.Count == 0) return;
            var entry = entries[entries.Count - 1];
            Executing = true;
            try
            {
                entry.Restore();
                entries.RemoveAt(entries.Count - 1);
            }
            finally
            {
                Executing = false;
                Version++;
            }
        }

        internal static void Clear()
        {
            entries.Clear();
            Version++;
        }
    }

    internal sealed class TransformEntry : UndoEntry
    {
        private readonly List<ItemState> states;

        internal TransformEntry(List<ItemState> states)
        {
            this.states = new List<ItemState>(states);
        }

        // 对象已被删除时跳过
        internal override void Restore()
        {
            foreach (var s in states)
                if (EditorContext.HasCtrl(s.Target)) Items.Write(s);
            RefreshPanel();
        }
    }

    // 原生拖动源：位置、角度、尺寸
    internal sealed class FieldEntry : UndoEntry
    {
        private readonly ItemState state;

        internal FieldEntry(ItemState state)
        {
            this.state = state;
        }

        internal override void Restore()
        {
            if (!EditorContext.HasCtrl(state.Target)) return;
            Items.Write(state);
            RefreshPanel();
        }
    }

    internal enum PropField
    {
        PosX,
        PosY,
        Rot,
        SizeX,
        SizeY,
        Color,
        OutlineColor,
        OutlineSpread,
        SlotRot,
        SlotColor,
        SlotOutlineColor,
        SlotOutlineSpread
    }

    // 气泡或特效本身（Slot = -1），或气泡里的某个槽位
    internal sealed class PropTarget
    {
        internal readonly SortOrder Target;
        internal readonly int Slot;

        private PropTarget(SortOrder target, int slot)
        {
            Target = target;
            Slot = slot;
        }

        internal static PropTarget Root(SortOrder so) => so == null ? null : new PropTarget(so, -1);

        internal static PropTarget For(SortOrder so, int slot)
        {
            if (so == null) return null;
            return slot >= 0 && so is SpeechBubbles ? new PropTarget(so, slot) : new PropTarget(so, -1);
        }

        internal bool SameAs(PropTarget other)
        {
            return other != null && other.Target == Target && other.Slot == Slot;
        }
    }

    // 字段旧值按类型二选一
    internal struct PropValue
    {
        public float Float;
        public Color Color;

        public static PropValue Of(float v) => new PropValue { Float = v };

        public static PropValue Of(Color c) => new PropValue { Color = c };
    }

    internal sealed class PropEntry : UndoEntry
    {
        internal readonly PropField Field;
        internal readonly PropValue Old;
        internal readonly PropValue Now;
        internal readonly PropTarget Target;

        internal PropEntry(PropField field, PropTarget target, PropValue old, PropValue now)
        {
            Field = field;
            Target = target;
            Old = old;
            Now = now;
        }

        internal override void Restore()
        {
            PropWriter.Write(Target, Field, Old);
            RefreshPanel();
        }
    }

    // 一次面板操作写到的全部对象；回放按整组处理
    internal sealed class PropBatch : UndoEntry
    {
        private readonly PropEntry[] entries;

        internal PropBatch(PropEntry[] entries)
        {
            this.entries = entries;
        }

        internal override void Restore()
        {
            foreach (var e in entries) e.Restore();
            RefreshPanel();
        }
    }

    internal static class PropWriter
    {
        internal static PropValue Read(PropTarget t, PropField field)
        {
            if (t == null) return default;
            if (t.Slot >= 0) return ReadSlot(t.Target as SpeechBubbles, t.Slot, field);
            if (t.Target is SpeechBubbles sb) return ReadBubble(sb, field);
            if (t.Target is ScreenEffect se) return ReadEffect(se, field);
            return default;
        }

        internal static void Write(PropTarget t, PropField field, PropValue value)
        {
            if (t == null || !EditorContext.HasCtrl(t.Target)) return;
            if (t.Slot >= 0)
            {
                WriteSlot(t.Target as SpeechBubbles, t.Slot, field, value);
                return;
            }
            if (t.Target is SpeechBubbles sb) WriteBubble(sb, field, value);
            else if (t.Target is ScreenEffect se) WriteEffect(se, field, value);
        }

        private static void WriteBubble(SpeechBubbles sb, PropField field, PropValue v)
        {
            var tc = (EditorContext.GetCtrl(sb) as TextUICtrl.TCUICtrl)?.textComponent;
            switch (field)
            {
                case PropField.PosX: sb.pos = new Vector2(v.Float, sb.pos.y); if (tc != null) tc.position = sb.pos; break;
                case PropField.PosY: sb.pos = new Vector2(sb.pos.x, v.Float); if (tc != null) tc.position = sb.pos; break;
                case PropField.Rot: sb.rot = v.Float; if (tc != null) tc.rot = v.Float; break;
                case PropField.SizeX: sb.size = new Vector2(v.Float, sb.size.y); if (tc != null) tc.sizeDelta = sb.size; break;
                case PropField.SizeY: sb.size = new Vector2(sb.size.x, v.Float); if (tc != null) tc.sizeDelta = sb.size; break;
                case PropField.Color: sb.color = v.Color; if (tc != null) tc.color = v.Color; break;
                case PropField.OutlineColor: sb.outlineColor = v.Color; if (tc != null) tc.outlineColor = v.Color; break;
                default: sb.outlineSpread = v.Float; if (tc != null) tc.outlineSpread = v.Float; break;
            }
        }

        private static void WriteEffect(ScreenEffect se, PropField field, PropValue v)
        {
            var ec = EditorContext.GetCtrl(se)?.effectComponent;
            switch (field)
            {
                case PropField.PosX: se.pos = new Vector2(v.Float, se.pos.y); if (ec != null) ec.position = se.pos; break;
                case PropField.PosY: se.pos = new Vector2(se.pos.x, v.Float); if (ec != null) ec.position = se.pos; break;
                case PropField.Rot: se.rot = v.Float; if (ec != null) ec.rot = v.Float; break;
                case PropField.SizeX: se.size = new Vector2(v.Float, se.size.y); if (ec != null) ec.sizeDelta = se.size; break;
                case PropField.SizeY: se.size = new Vector2(se.size.x, v.Float); if (ec != null) ec.sizeDelta = se.size; break;
                case PropField.Color: se.color = v.Color; if (ec != null) ec.color = v.Color; break;
                case PropField.OutlineColor: se.outlineColor = v.Color; if (ec != null) ec.outlineColor = v.Color; break;
                default: se.outlineSpread = v.Float; if (ec != null) ec.outlineSpread = v.Float; break;
            }
        }

        // 槽位 rot 是相对气泡的 localRotation，与 TextLayoutUICtrl 的写法定一致
        internal static void WriteSlot(SpeechBubbles bubble, int slot, PropField field, PropValue v)
        {
            var layout = SlotLayout(bubble, slot);
            if (layout == null) return;
            var info = SlotInfo(bubble, slot);
            switch (field)
            {
                case PropField.SlotRot:
                    layout.rot = v.Float % 360f;
                    if (info != null) info.rot = layout.rot;
                    break;
                case PropField.SlotColor:
                    layout.color = v.Color;
                    if (info != null) info.color = v.Color;
                    break;
                case PropField.SlotOutlineColor:
                    layout.outlineColor = v.Color;
                    if (info != null) info.outlineColor = v.Color;
                    break;
                default:
                    layout.outlineSpread = v.Float;
                    if (info != null) info.outlineSpread = v.Float;
                    break;
            }
        }

        private static PropValue ReadBubble(SpeechBubbles sb, PropField field)
        {
            switch (field)
            {
                case PropField.PosX: return PropValue.Of(sb.pos.x);
                case PropField.PosY: return PropValue.Of(sb.pos.y);
                case PropField.Rot: return PropValue.Of(sb.rot);
                case PropField.SizeX: return PropValue.Of(sb.size.x);
                case PropField.SizeY: return PropValue.Of(sb.size.y);
                case PropField.Color: return PropValue.Of(sb.color);
                case PropField.OutlineColor: return PropValue.Of(sb.outlineColor);
                default: return PropValue.Of(sb.outlineSpread);
            }
        }

        private static PropValue ReadEffect(ScreenEffect se, PropField field)
        {
            switch (field)
            {
                case PropField.PosX: return PropValue.Of(se.pos.x);
                case PropField.PosY: return PropValue.Of(se.pos.y);
                case PropField.Rot: return PropValue.Of(se.rot);
                case PropField.SizeX: return PropValue.Of(se.size.x);
                case PropField.SizeY: return PropValue.Of(se.size.y);
                case PropField.Color: return PropValue.Of(se.color);
                case PropField.OutlineColor: return PropValue.Of(se.outlineColor);
                default: return PropValue.Of(se.outlineSpread);
            }
        }

        private static PropValue ReadSlot(SpeechBubbles bubble, int slot, PropField field)
        {
            var layout = SlotLayout(bubble, slot);
            if (layout == null) return default;
            switch (field)
            {
                case PropField.SlotRot: return PropValue.Of(layout.rot);
                case PropField.SlotColor: return PropValue.Of(layout.color);
                case PropField.SlotOutlineColor: return PropValue.Of(layout.outlineColor);
                default: return PropValue.Of(layout.outlineSpread);
            }
        }

        private static HEdit.ADVPart.SpeechBubbles.TextLayout SlotLayout(SpeechBubbles bubble, int slot)
        {
            var layouts = bubble?.textLayouts;
            return layouts != null && slot >= 0 && slot < layouts.Length ? layouts[slot] : null;
        }

        private static ADV.TextComponent.TextInfo SlotInfo(SpeechBubbles bubble, int slot)
        {
            var tc = (EditorContext.GetCtrl(bubble) as TextUICtrl.TCUICtrl)?.textComponent;
            var infos = tc?.textInfos;
            return infos != null && slot >= 0 && slot < infos.Length ? infos[slot] : null;
        }
    }

    internal sealed class SlotRotEntry : UndoEntry
    {
        private readonly SpeechBubbles bubble;
        private readonly int slot;
        private readonly float rot;

        internal SlotRotEntry(SpeechBubbles bubble, int slot, float rot)
        {
            this.bubble = bubble;
            this.slot = slot;
            this.rot = rot;
        }

        internal override void Restore()
        {
            if (EditorContext.HasCtrl(bubble)) SlotOps.WriteRot(bubble, slot, rot);
            RefreshPanel();
        }
    }

    internal sealed class OrderEntry : UndoEntry
    {
        private readonly TextAndEffectListUICtrl list;
        private readonly List<SortOrder> order;

        internal OrderEntry(TextAndEffectListUICtrl list, List<SortOrder> order)
        {
            this.list = list;
            this.order = new List<SortOrder>(order);
        }

        // 对象有增删时整条跳过，避免把已删除的对象写回列表
        internal override void Restore()
        {
            var now = list.lstUI;
            if (now.Count != order.Count) return;
            foreach (var so in order)
                if (!now.Contains(so)) return;
            now.Clear();
            now.AddRange(order);
            list.Sort();
            list.UpdateUI();
        }
    }

    // 新建对象：撤销时用原生删除，并把它移出选中集合
    internal sealed class SpawnEntry : UndoEntry
    {
        private readonly SortOrder target;

        internal SpawnEntry(SortOrder target)
        {
            this.target = target;
        }

        internal override void Restore()
        {
            if (!EditorContext.HasCtrl(target)) return;
            MultiSelection.Drop(target);
            DeleteNative(target);
            if (EditorContext.Primary == target)
            {
                var ui = EditorContext.UI;
                if (ui != null) ui.sortOrder = null;
            }
        }

        // 新建与删除撤销共用
        internal static void DeleteNative(SortOrder so)
        {
            if (so is SpeechBubbles sb)
            {
                var ctrl = EditorContext.TextPanel;
                if (ctrl != null) ctrl.DeleteText(sb);
            }
            else if (so is ScreenEffect se)
            {
                var ctrl = EditorContext.EffectPanel;
                if (ctrl != null) ctrl.DeleteEffect(se);
            }
        }

        internal static void AddNative(SortOrder so)
        {
            if (so is SpeechBubbles sb)
            {
                var ctrl = EditorContext.TextPanel;
                if (ctrl != null) ctrl.AddText(sb, false);
            }
            else if (so is ScreenEffect se)
            {
                var ctrl = EditorContext.EffectPanel;
                if (ctrl != null) ctrl.AddEffect(se, false);
            }
        }
    }

    // 换样式：id 变了，画面重建；撤销把 id 写回再走原生 Change
    internal sealed class StyleEntry : UndoEntry
    {
        private readonly SortOrder target;
        private readonly int id;

        internal StyleEntry(SortOrder target, int id)
        {
            this.target = target;
            this.id = id;
        }

        internal override void Restore()
        {
            if (!EditorContext.HasCtrl(target)) return;
            if (target is SpeechBubbles sb)
            {
                sb.id = id;
                EditorContext.TextPanel?.ChangeText(sb);
            }
            else if (target is ScreenEffect se)
            {
                se.id = id;
                EditorContext.EffectPanel?.ChangeEffect(se);
            }
        }
    }

    // 删除对象：保留同一实例，撤销时按原数据重建画面并按删除前的图层顺序放回
    internal sealed class DeleteEntry : UndoEntry
    {
        private readonly SortOrder target;
        private readonly int sortOrder;
        private readonly List<SortOrder> order;

        internal DeleteEntry(SortOrder target, int sortOrder, List<SortOrder> order)
        {
            this.target = target;
            this.sortOrder = sortOrder;
            this.order = new List<SortOrder>(order);
        }

        internal SortOrder Target => target;

        internal override void Restore()
        {
            if (EditorContext.HasCtrl(target)) return;
            SpawnEntry.AddNative(target);
            var list = EditorContext.List;
            if (list == null) return;
            // 新增会把 sortOrder 写成列表长度，可能与现存对象重复；按删除前的顺序整体重编号
            int index = order.IndexOf(target);
            if (index < 0)
            {
                target.sortOrder = sortOrder;
                list.Sort();
                list.UpdateUI();
                return;
            }
            var now = list.lstUI;
            for (int i = 0; i < order.Count; i++)
            {
                var so = order[i];
                if (so == target || now.Contains(so)) so.sortOrder = i;
            }
            list.Sort();
            list.UpdateUI();
        }
    }
}
