namespace EC_ADVCutSync
{
    // 鼠标手势归属：窗口内按下整段屏蔽，窗口外按下整段放行，游戏才能拿到松开边沿
    internal sealed class PointerOwnership
    {
        public const int ButtonCount = 3;

        private readonly bool[] held = new bool[ButtonCount];
        private readonly bool[] owned = new bool[ButtonCount];
        private bool releaseGrace;

        public void ButtonDown(int button, bool overWindow)
        {
            held[button] = true;
            owned[button] = overWindow;
        }

        public void ButtonUp(int button)
        {
            // 窗口外起始的手势在释放帧仍要放行：uGUI 的 drop/click 用该帧的命中结果
            if (held[button] && !owned[button])
                releaseGrace = true;
            held[button] = false;
            owned[button] = false;
        }

        public void BeginFrame() => releaseGrace = false;

        public bool ShouldBlock(bool overWindow)
        {
            for (var button = 0; button < ButtonCount; button++)
            {
                if (owned[button])
                    return true;
            }
            for (var button = 0; button < ButtonCount; button++)
            {
                if (held[button])
                    return false;
            }
            return !releaseGrace && overWindow;
        }

        public void Reset()
        {
            releaseGrace = false;
            for (var button = 0; button < ButtonCount; button++)
            {
                held[button] = false;
                owned[button] = false;
            }
        }
    }
}
