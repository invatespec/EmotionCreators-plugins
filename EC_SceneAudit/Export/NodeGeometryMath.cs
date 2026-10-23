namespace EC_SceneAudit.Export
{
    // 几何纯计算：frame 是画布绝对坐标，端口坐标是相对 frame 左上角的未缩放 UI 单位。
    internal static class NodeGeometryMath
    {
        public static bool ToRelativePoint(
            float absoluteX,
            float absoluteY,
            GeometryFrameSnapshot frame,
            out float relativeX,
            out float relativeY)
        {
            relativeX = 0f;
            relativeY = 0f;
            if (frame == null || !IsFinite(absoluteX) || !IsFinite(absoluteY)
                || !IsFinite(frame.x) || !IsFinite(frame.y))
                return false;
            relativeX = absoluteX - frame.x;
            relativeY = absoluteY - frame.y;
            return IsFinite(relativeX) && IsFinite(relativeY);
        }

        public static bool TryComputePositionOffset(
            float frameX,
            float frameY,
            float nativeX,
            float nativeY,
            out float offsetX,
            out float offsetY)
        {
            offsetX = 0f;
            offsetY = 0f;
            if (!IsFinite(frameX) || !IsFinite(frameY) || !IsFinite(nativeX) || !IsFinite(nativeY))
                return false;
            offsetX = frameX - nativeX;
            offsetY = frameY - (-nativeY);
            return IsFinite(offsetX) && IsFinite(offsetY);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
