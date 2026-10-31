using System.Collections.Generic;
using UnityEngine;

namespace EC_ADVBatchTransform
{
    using SortOrder = HEdit.ADVPart.SortOrder;

    internal enum GestureKind
    {
        Move,
        Rotate,
        Resize
    }

    // 一次按住或一次拖动。成员新值都从开始时的快照算，不逐帧累加
    internal sealed class Gesture
    {
        public GestureKind Kind;
        public SortOrder Source;
        public PivotMode Pivot;
        public readonly List<ItemState> Start = new List<ItemState>();
        public Vector2 Center;
        public Vector2 TotalMove;
        public float TotalAngle;
        public float LastAngle;
        public bool Changed;

        internal static Gesture Capture(GestureKind kind, SortOrder source)
        {
            var g = new Gesture { Kind = kind, Source = source, Pivot = ADVBatchTransformPlugin.Pivot.Value };
            foreach (var so in MultiSelection.Members)
            {
                var state = Items.Read(so);
                g.Start.Add(state);
                g.Center += state.Local;
                if (so == source) g.LastAngle = state.Rot;
            }
            if (g.Start.Count > 0) g.Center /= g.Start.Count;
            return g;
        }

        internal ItemState StartOf(SortOrder so)
        {
            foreach (var s in Start)
                if (s.Target == so) return s;
            return Items.Read(so);
        }
    }

    internal static class BatchOps
    {
        private static Gesture current;

        internal static bool Active => current != null;

        internal static bool IsKeyGesture => current != null && current.Source == null;

        internal static void Cancel() => current = null;

        internal static void End()
        {
            if (current == null) return;
            if (current.Changed) UndoStack.Push(new TransformEntry(current.Start));
            // 拖动源的原生记录已被这条覆盖
            if (current.Source != null) EditRecorder.SuppressNative();
            current = null;
        }

        private static Gesture Ensure(GestureKind kind, SortOrder source)
        {
            if (current != null && current.Kind == kind && current.Source == source) return current;
            End();
            current = Gesture.Capture(kind, source);
            return current;
        }

        // 必须在拖动源写入新值之前调用，快照才是拖动前的值
        internal static void BeginDrag(GestureKind kind, SortOrder source) => Ensure(kind, source);

        internal static void KeyMove(Vector2 step)
        {
            var g = Ensure(GestureKind.Move, null);
            g.TotalMove += step;
            ApplyMove(g);
        }

        internal static void KeyRotate(float step)
        {
            var g = Ensure(GestureKind.Rotate, null);
            g.TotalAngle += step;
            ApplyRotate(g);
        }

        internal static void DragMoved(Vector2 sourcePos)
        {
            if (current == null) return;
            current.TotalMove = sourcePos - current.StartOf(current.Source).Pos;
            ApplyMove(current);
        }

        // 原生传的是 eulerAngles.z（0~360），数据里的 rot 可能为负，只能按增量累加
        internal static void DragRotated(float sourceAngle)
        {
            if (current == null) return;
            current.TotalAngle += Mathf.DeltaAngle(current.LastAngle, sourceAngle);
            current.LastAngle = sourceAngle;
            ApplyRotate(current);
        }

        internal static void DragResized(Vector2 sourceSize)
        {
            if (current == null) return;
            ApplyResize(current, sourceSize);
        }

        private static void ApplyMove(Gesture g)
        {
            foreach (var s in g.Start)
            {
                // 拖动源已由原生移动
                if (s.Target == g.Source) continue;
                var next = s;
                next.Pos = s.Pos + g.TotalMove;
                Items.Write(next);
            }
            g.Changed |= g.TotalMove != Vector2.zero;
            Items.RefreshPrimaryPanel();
        }

        private static void ApplyRotate(Gesture g)
        {
            var q = Quaternion.Euler(0f, 0f, g.TotalAngle);
            foreach (var s in g.Start)
            {
                var next = s;
                next.Rot = (s.Rot + g.TotalAngle) % 360f;
                if (g.Pivot == PivotMode.Group)
                    next.Pos = s.Pos + OffsetAround(g.Center, s.Local, (Vector2)(q * (s.Local - g.Center)));
                Commit(g, next);
            }
            g.Changed |= !Mathf.Approximately(g.TotalAngle, 0f);
            Items.RefreshPrimaryPanel();
        }

        private static void ApplyResize(Gesture g, Vector2 sourceSize)
        {
            var size0 = g.StartOf(g.Source).Size;
            var k = new Vector2(Ratio(sourceSize.x, size0.x), Ratio(sourceSize.y, size0.y));
            foreach (var s in g.Start)
            {
                var next = s;
                next.Size = Vector2.Scale(s.Size, k);
                if (g.Pivot == PivotMode.Group)
                    next.Pos = s.Pos + OffsetAround(g.Center, s.Local, Vector2.Scale(s.Local - g.Center, k));
                Commit(g, next);
            }
            g.Changed |= k != Vector2.one;
            Items.RefreshPrimaryPanel();
        }

        // 新位置相对旧位置的偏移，换算到 pos（anchoredPosition 与 localPosition 只差常量）
        private static Vector2 OffsetAround(Vector2 center, Vector2 local0, Vector2 newOffset)
        {
            return center + newOffset - local0;
        }

        // 拖动源的 rot/size 已由原生写好，只补位置
        private static void Commit(Gesture g, ItemState next)
        {
            if (next.Target != g.Source)
            {
                Items.Write(next);
                return;
            }
            if (g.Pivot != PivotMode.Group) return;
            var now = Items.Read(next.Target);
            now.Pos = next.Pos;
            Items.Write(now);
        }

        private static float Ratio(float now, float start)
        {
            return Mathf.Abs(start) < 0.0001f ? 1f : now / start;
        }
    }
}
