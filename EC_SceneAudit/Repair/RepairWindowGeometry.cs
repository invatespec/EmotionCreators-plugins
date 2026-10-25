using System;

namespace EC_SceneAudit.Repair
{
    // 窗口位置计算（纯逻辑，无 Unity 依赖，便于离线验证）。
    // 约定：位置以"可用空间内的归一化坐标"保存，拖动结算时折算一次；
    // 分辨率变化或重新打开窗口时按新屏幕重新展开并夹紧，保证窗口始终在屏幕内。
    internal struct RepairWindowBox
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;

        public RepairWindowBox(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    internal static class RepairWindowGeometry
    {
        public const float PreferredWidth = 640f;
        public const float PreferredHeight = 440f;
        public const float MinimumWidth = 320f;
        public const float MinimumHeight = 260f;
        public const float HorizontalMargin = 24f;
        public const float VerticalMargin = 48f;
        public const float PathFieldLineCount = 2f;

        public static RepairWindowBox FromNormalized(float normalizedX, float normalizedY, int screenWidth, int screenHeight)
        {
            var width = Math.Min(PreferredWidth, Math.Max(MinimumWidth, screenWidth - HorizontalMargin));
            var height = Math.Min(PreferredHeight, Math.Max(MinimumHeight, screenHeight - VerticalMargin));
            var box = new RepairWindowBox(
                (screenWidth - width) * Clamp01(normalizedX),
                (screenHeight - height) * Clamp01(normalizedY),
                width,
                height);
            return ClampToScreen(box, screenWidth, screenHeight);
        }

        public static RepairWindowBox ClampToScreen(RepairWindowBox box, int screenWidth, int screenHeight)
        {
            // 尺寸要先挡非有限值：Math.Min(NaN, x) 仍是 NaN，会一路带进 GUI.Window 与命中判定。
            if (!IsFinite(box.Width))
                box.Width = MinimumWidth;
            if (!IsFinite(box.Height))
                box.Height = MinimumHeight;
            box.Width = Math.Min(box.Width, Math.Max(MinimumWidth, screenWidth));
            box.Height = Math.Min(box.Height, Math.Max(MinimumHeight, screenHeight));
            box.X = Clamp(box.X, 0f, Math.Max(0f, screenWidth - box.Width));
            box.Y = Clamp(box.Y, 0f, Math.Max(0f, screenHeight - box.Height));
            return box;
        }

        public static void Normalize(RepairWindowBox box, int screenWidth, int screenHeight,
            out float normalizedX, out float normalizedY)
        {
            normalizedX = Clamp01(box.X / Math.Max(1f, screenWidth - box.Width));
            normalizedY = Clamp01(box.Y / Math.Max(1f, screenHeight - box.Height));
        }

        private static float Clamp01(float value)
        {
            return IsFinite(value) ? Clamp(value, 0f, 1f) : 0f;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static float Clamp(float value, float minimum, float maximum)
        {
            // NaN 直接落到下界，避免把 NaN 位置带进窗口矩形。
            if (float.IsNaN(value)) return minimum;
            if (value < minimum) return minimum;
            return value > maximum ? maximum : value;
        }
    }
}
