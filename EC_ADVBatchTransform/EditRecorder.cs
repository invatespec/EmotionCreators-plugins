using System.Collections.Generic;
using UnityEngine;

namespace EC_ADVBatchTransform
{
    using SortOrder = HEdit.ADVPart.SortOrder;

    // 原生拖动的记录。插件同步手势由 BatchOps 自己记，同一指针周期内不再重复记源对象
    internal static class EditRecorder
    {
        private sealed class DragGesture
        {
            internal SortOrder Source;
            internal ItemState Start;
        }

        private static readonly List<DragGesture> gestures = new List<DragGesture>();
        private static bool suppressUntilRelease;

        internal static void Reset()
        {
            gestures.Clear();
            suppressUntilRelease = false;
        }

        // 指针落在列表/面板上时 DragMove 也会收到按下，但会在同一帧松开，松开时数值没变自然不记
        internal static void BeginDrag(SortOrder so)
        {
            if (UndoStack.Executing || so == null || !EditorContext.InTextEffectPage) return;
            foreach (var g in gestures)
                if (g.Source == so) return;
            gestures.Add(new DragGesture { Source = so, Start = Items.Read(so) });
        }

        // 插件批量手势已记成一条 TransformEntry，不再追加拖动源的单独记录
        internal static void SuppressNative()
        {
            gestures.Clear();
            suppressUntilRelease = true;
        }

        internal static void Tick()
        {
            if (Input.GetMouseButton(0)) return;
            if (suppressUntilRelease)
            {
                // 插件的批量手势已经记过，这里只把面板基线刷新掉
                suppressUntilRelease = false;
                PanelWatcher.Invalidate();
                return;
            }
            if (gestures.Count == 0) return;
            bool dirty = false;
            foreach (var g in gestures)
            {
                if (!EditorContext.HasCtrl(g.Source)) continue;
                var now = Items.Read(g.Source);
                if (Same(g.Start, now)) continue;
                UndoStack.Push(new FieldEntry(g.Start));
                dirty = true;
            }
            gestures.Clear();
            // 面板观察者的基线是拖动前的值，整条拖动已由这里记录，不能让它再记一次
            PanelWatcher.Invalidate();
            if (dirty) Items.RefreshPrimaryPanel();
        }

        private static bool Same(ItemState a, ItemState b)
        {
            return a.Pos == b.Pos && Mathf.Approximately(a.Rot, b.Rot) && a.Size == b.Size;
        }
    }
}
