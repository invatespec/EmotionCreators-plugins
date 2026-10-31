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
            PropField.SlotRot, PropField.SlotColor, PropField.SlotOutlineColor, PropField.SlotOutlineSpread
        };

        private static readonly Dictionary<PropField, PropValue> baseline = new Dictionary<PropField, PropValue>();
        private static PropTarget lastTarget;
        private static int lastVersion = -1;
        private static bool hasBase;

        internal static void Invalidate() => hasBase = false;

        internal static void Tick()
        {
            if (!EditorContext.InTextEffectPage)
            {
                hasBase = false;
                return;
            }
            // 按住鼠标期间是拖动（对象拖动、滑条、颜色拾取），松开后一次性比较，
            // 避免逐帧写入各记一条，也避免和拖动记录重复
            if (Input.GetMouseButton(0)) return;
            var target = PropTarget.For(EditorContext.Primary, EditorContext.ActiveSlot);
            if (target == null || VersionChanged())
            {
                hasBase = false;
                return;
            }
            if (!hasBase || !target.SameAs(lastTarget))
            {
                hasBase = true;
                lastTarget = target;
                Baseline(target);
                return;
            }
            Detect(target);
        }

        // 撤销回放后数据被整体改写，必须重建基线
        private static bool VersionChanged()
        {
            if (lastVersion == UndoStack.Version) return false;
            lastVersion = UndoStack.Version;
            return true;
        }

        private static void Baseline(PropTarget t)
        {
            baseline.Clear();
            foreach (var field in Fields(t)) baseline[field] = PropWriter.Read(t, field);
        }

        private static PropField[] Fields(PropTarget t) => t.Slot >= 0 ? SlotFields : RootFields;

        private static void Detect(PropTarget target)
        {
            var changed = new List<PropEntry>();
            foreach (var field in Fields(target))
            {
                if (!baseline.TryGetValue(field, out var old)) continue;
                var now = PropWriter.Read(target, field);
                if (!Differs(field, old, now)) continue;
                changed.Add(new PropEntry(field, target, old, now));
                baseline[field] = now;
            }
            if (changed.Count == 0) return;
            if (target.Slot < 0) Batch(changed);
            // 一律用整组条目，回放后统一刷新一次面板
            UndoStack.Push(new PropBatch(changed.ToArray()));
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
                default:
                    return !Mathf.Approximately(now.Float, old.Float);
            }
        }

        // 多选且没点开槽位时，把主对象的新值写到其他成员
        private static void Batch(List<PropEntry> changed)
        {
            if (!EditorContext.CanOperate || !MultiSelection.IsMulti || EditorContext.ActiveSlot >= 0) return;
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
    }
}
