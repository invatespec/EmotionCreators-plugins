using HEdit;
using UnityEngine;
using YS_Node;

namespace EC_NodeSidePanel
{
    // 画布 pan 读写：与 CanvasExpandService 的 vGridPos=anchoredPosition 语义一致。
    internal static class CanvasPanController
    {
        private static readonly Vector3[] ViewportCorners = new Vector3[4];

        internal static bool TryGetControl(out NodeControl control)
        {
            control = null;
            if (!Singleton<HEditGlobal>.IsInstance())
                return false;
            control = Singleton<HEditGlobal>.Instance.nodeControl;
            return control != null && control.rtfGrid != null;
        }

        internal static bool TryGetPanState(out NodeControl control, out Vector2 pos, out Vector2 maxPan, out float scale)
        {
            pos = Vector2.zero;
            maxPan = Vector2.zero;
            scale = 1f;
            if (!TryGetControl(out control))
                return false;

            var grid = control.rtfGrid;
            var parent = grid.parent as RectTransform;
            if (parent == null)
                return false;

            float gridScale = CanvasExpandService.GridScaleRef(control);
            scale = CanvasExpandService.ScaleFromGridScale(gridScale);
            pos = CanvasExpandService.VGridPosRef(control);
            maxPan = CanvasExpandService.ComputeMaxPan(grid, parent, scale);
            return true;
        }

        internal static void SetPan(NodeControl control, Vector2 pos)
        {
            if (control == null || control.rtfGrid == null)
                return;
            var parent = control.rtfGrid.parent as RectTransform;
            if (parent == null)
                return;

            float scale = CanvasExpandService.ScaleFromGridScale(CanvasExpandService.GridScaleRef(control));
            Vector2 maxPan = CanvasExpandService.ComputeMaxPan(control.rtfGrid, parent, scale);
            pos.x = Mathf.Clamp(pos.x, -maxPan.x, maxPan.x);
            pos.y = Mathf.Clamp(pos.y, -maxPan.y, maxPan.y);
            CanvasExpandService.VGridPosRef(control) = pos;
            control.rtfGrid.anchoredPosition = pos;
        }

        // 用真实 UI 坐标：把节点视觉中心挪到视口中心。
        // 注意用 rect.center 而非 pivot（节点 pivot 常在左上，用 position 会整块偏右下）。
        internal static bool FocusNodeUI(NodeControl control, NodeUI node)
        {
            if (control == null || control.rtfGrid == null || node == null)
                return false;
            var grid = control.rtfGrid;
            var viewport = grid.parent as RectTransform;
            var nodeRt = node.transform as RectTransform;
            if (viewport == null || nodeRt == null)
                return false;

            // 都在 viewport 本地空间比较，避免 scale/旋转下 InverseTransformVector 误差
            Vector2 viewCenter = viewport.rect.center;
            Vector2 nodeCenterInViewport = viewport.InverseTransformPoint(
                nodeRt.TransformPoint(nodeRt.rect.center));
            Vector2 delta = viewCenter - nodeCenterInViewport;

            // 平移 grid.anchoredPosition 会等量移动所有子节点
            Vector2 cur = CanvasExpandService.VGridPosRef(control);
            SetPan(control, cur + delta);
            return true;
        }

        internal static bool TryGetFocusHintGeometry(
            NodeControl control, NodeUI node, out Rect viewportGuiRect, out Vector2 nodeGuiPoint)
        {
            viewportGuiRect = default(Rect);
            nodeGuiPoint = default(Vector2);
            if (control == null || control.rtfGrid == null || node == null)
                return false;

            var viewport = control.rtfGrid.parent as RectTransform;
            var nodeRt = node.transform as RectTransform;
            if (viewport == null || nodeRt == null)
                return false;

            Canvas canvas = viewport.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;

            viewport.GetWorldCorners(ViewportCorners);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, ViewportCorners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, ViewportCorners[2]);
            float xMin = Mathf.Min(a.x, b.x);
            float xMax = Mathf.Max(a.x, b.x);
            float yMin = Mathf.Min(a.y, b.y);
            float yMax = Mathf.Max(a.y, b.y);
            viewportGuiRect = Rect.MinMaxRect(xMin, Screen.height - yMax, xMax, Screen.height - yMin);

            Vector2 nodeScreen = RectTransformUtility.WorldToScreenPoint(
                camera, nodeRt.TransformPoint(nodeRt.rect.center));
            nodeGuiPoint = new Vector2(nodeScreen.x, Screen.height - nodeScreen.y);
            return viewportGuiRect.width > 0f && viewportGuiRect.height > 0f;
        }
    }
}
