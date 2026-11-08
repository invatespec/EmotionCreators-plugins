using UnityEngine;
using YS_Node;

namespace EC_NodeSidePanel.Api
{
    // 供其它插件通过反射探测的稳定入口；字段只描述本次调用后的实际画布状态。
    public static class CanvasExtensionApi
    {
        public const int ApiVersion = 1;

        public static bool TryGetState(NodeControl control, out CanvasState state, out string error)
        {
            return CanvasExpandService.TryGetState(control, out state, out error);
        }

        public static bool TryResizeAbsolute(
            NodeControl control,
            Vector2 desiredSize,
            bool preserveView,
            out CanvasState state,
            out string error)
        {
            return CanvasExpandService.TryResizeAbsolute(control, desiredSize, preserveView, out state, out error);
        }
    }

    public sealed class CanvasState
    {
        public Vector2 CurrentSize;
        public Vector2 NativeSize;
        public Vector2 ViewportSize;
        public Vector2 Pivot;
        public Vector2 Pan;
        public float Scale;
        public float MinScale;
        public bool BackgroundReady;
    }
}
