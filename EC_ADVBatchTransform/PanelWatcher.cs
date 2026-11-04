using System.Collections.Generic;
using UnityEngine;

namespace EC_ADVBatchTransform
{
    using SortOrder = HEdit.ADVPart.SortOrder;

    // 原生面板的写入都挂在私有控件事件上，没有统一出口；这里每帧把数据值和基线比较。
    // 插件自己的写入（拖动同步、方向键、撤销回放）会调 Invalidate 重建基线，不会被误记。
    // 多选时把同一字段值写到其他成员，与主对象条目一起作为一次操作提交
    internal static class PanelWatcher
    {
        private static readonly PropField[] RootFields =
        {
            PropField.PosX, PropField.PosY, PropField.Rot, PropField.SizeX, PropField.SizeY,
            PropField.Color, PropField.OutlineColor, PropField.OutlineSpread
        };
        private static readonly PropField[] SlotFields =
        {
            PropField.SlotMsg, PropField.SlotFont, PropField.SlotFontSize, PropField.SlotLineSpacing,
            PropField.SlotAnime, PropField.SlotTextDir, PropField.SlotAutoLine,
            PropField.SlotEffect, PropField.SlotOutlineColor, PropField.SlotOutlineSpread,
            PropField.SlotTop, PropField.SlotBottom, PropField.SlotLeft, PropField.SlotRight,
            PropField.SlotRot, PropField.SlotColor
        };

        // 根目标与槽位目标各存一份基线：槽位打开期间本体字段仍可改，
        // 只跟踪当前目标会让另一侧的变化推迟到切目标时才补记，回放顺序被打乱
        private sealed class Track
        {
            internal readonly PropTarget Target;
            internal readonly Dictionary<PropField, PropValue> Baseline = new Dictionary<PropField, PropValue>();

            internal Track(PropTarget target) => Target = target;
        }

        private static Track rootTrack;
        private static Track slotTrack;
        private static int lastVersion = -1;

        internal static void Invalidate()
        {
            rootTrack = null;
            slotTrack = null;
        }

        // 任何会改写数据或有其他记录要入栈的动作之前调用：把两条轨道的待提交改动落栈。
        // 直接 Invalidate 会把基线和改动一起丢掉，输入框打完字的改动就此消失
        internal static void Flush()
        {
            if (rootTrack != null) Commit(rootTrack);
            if (slotTrack != null) Commit(slotTrack);
        }

        internal static void Tick()
        {
            if (!EditorContext.InTextEffectPage)
            {
                Invalidate();
                return;
            }
            // 按住鼠标期间是拖动（对象拖动、滑条、颜色拾取、输入框拖动改值），松开后一次性比较，
            // 避免逐帧写入各记一条，也避免和拖动记录重复
            if (Input.GetMouseButton(0)) return;
            // 文本输入框内每敲一个字都改一次数据，逐帧比对会一个字一条记录；
            // 文本改动由 TextHistory 记录，失焦后再整体比对（接管失败时 SlotMsg 兜底）
            if (EditorContext.IsTyping) return;
            if (!VersionChanged()) Detect();
        }

        // 撤销回放后数据被整体改写，必须重建基线
        private static bool VersionChanged()
        {
            if (lastVersion == UndoStack.Version) return false;
            lastVersion = UndoStack.Version;
            Invalidate();
            return true;
        }

        private static void Detect()
        {
            var primary = EditorContext.Primary;
            int slot = EditorContext.ActiveSlot;
            var root = PropTarget.Root(primary);
            var slotTarget = slot >= 0 ? PropTarget.For(primary, slot) : null;

            rootTrack = Sync(rootTrack, root, RootFields);
            slotTrack = Sync(slotTrack, slotTarget, SlotFields);
        }

        // 目标变化时按新目标重建基线；目标不变时比较并提交变化
        private static Track Sync(Track track, PropTarget target, PropField[] fields)
        {
            if (target == null)
            {
                if (track != null) Commit(track);
                return null;
            }
            if (track == null || !track.Target.SameAs(target))
            {
                if (track != null) Commit(track);
                var fresh = new Track(target);
                foreach (var field in fields) fresh.Baseline[field] = PropWriter.Read(target, field);
                return fresh;
            }
            Compare(track, fields);
            return track;
        }

        // 提交前把该目标残余的变化记完，避免切目标时最后一段编辑丢掉。
        // 没有变化时把暂存号丢掉，否则它会留到下一次无关编辑上把序号取早
        private static void Commit(Track track)
        {
            if (track != null) Compare(track, track.Target.Slot >= 0 ? SlotFields : RootFields);
        }

        private static bool Compare(Track track, PropField[] fields)
        {
            var changed = new List<PropEntry>();
            foreach (var field in fields)
            {
                if (!track.Baseline.TryGetValue(field, out var old)) continue;
                var now = PropWriter.Read(track.Target, field);
                if (!Differs(field, old, now)) continue;
                changed.Add(new PropEntry(field, track.Target, old, now));
                track.Baseline[field] = now;
            }
            bool slot = track.Target.Slot >= 0;
            if (changed.Count == 0)
            {
                // 点了输入框却没改值：取过的号必须丢掉，否则会粘到下一次编辑上把序号取早
                ActionClock.Discard(slot ? ActionScope.Slot : ActionScope.Root);
                return false;
            }
            if (!slot) Batch(changed);
            // 序号取发起时刻：拖动/打字期间比对被暂停，改动会在之后才被发现
            var entry = new PropBatch(changed.ToArray())
            {
                Sequence = ActionClock.Take(slot ? ActionScope.Slot : ActionScope.Root)
            };
            UndoStack.Push(entry);
            return true;
        }

        // 多选且没点开槽位时，把主对象的新值写到其他成员，并与主对象条目合成同一步
        private static void Batch(List<PropEntry> changed)
        {
            if (!EditorContext.CanOperate || !MultiSelection.IsMulti) return;
            var primary = EditorContext.Primary;
            var members = MultiSelection.Members;
            var source = changed.ToArray();
            for (int i = members.Count - 1; i >= 0; i--)
            {
                var so = members[i];
                if (so == primary) continue;
                var t = PropTarget.Root(so);
                foreach (var e in source)
                {
                    var old = PropWriter.Read(t, e.Field);
                    PropWriter.Write(t, e.Field, e.Now);
                    changed.Add(new PropEntry(e.Field, t, old, e.Now));
                }
            }
            Items.RefreshPrimaryPanel();
        }

        private static bool Differs(PropField field, PropValue old, PropValue now)
        {
            switch (field)
            {
                case PropField.Color:
                case PropField.OutlineColor:
                case PropField.SlotColor:
                case PropField.SlotOutlineColor:
                    return now.Color != old.Color;
                case PropField.SlotMsg:
                    // 文本由 TextHistory 记录；接管失败时退回面板比对，避免文本完全不可撤销
                    return !TextHistory.Attached && now.Text != old.Text;
                case PropField.SlotFont:
                case PropField.SlotFontSize:
                case PropField.SlotTextDir:
                case PropField.SlotEffect:
                    return now.Int != old.Int;
                case PropField.SlotAnime:
                case PropField.SlotAutoLine:
                    return now.Bool != old.Bool;
                default:
                    return !Mathf.Approximately(now.Float, old.Float);
            }
        }
    }
}
