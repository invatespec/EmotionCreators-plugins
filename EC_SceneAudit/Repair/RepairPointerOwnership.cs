namespace EC_SceneAudit.Repair
{
    // 鼠标手势归属（纯逻辑，无 Unity 依赖）。
    // 按窗口内/外区分起点：窗口内按下整段屏蔽，窗口外按下整段放行，游戏才能拿到松开边沿。
    // 只按悬停判定会拆散跨窗口手势，拖拽/连线/平移状态无法复位。
    internal sealed class RepairPointerOwnership
    {
        public const int ButtonCount = 3;

        private readonly bool[] held = new bool[ButtonCount];
        private readonly bool[] owned = new bool[ButtonCount];
        private bool releaseGrace;

        public void ButtonDown(int button, bool overWindow)
        {
            if (!Valid(button))
                return;
            held[button] = true;
            owned[button] = overWindow;
        }

        public void ButtonUp(int button)
        {
            if (!Valid(button))
                return;
            // 窗口外起始的手势在释放帧仍要放行：uGUI 的 drop/click 用该帧的命中结果。
            if (held[button] && !owned[button])
                releaseGrace = true;
            held[button] = false;
            owned[button] = false;
        }

        // 每个采样帧开始时清掉上一帧的放行宽限，宽限只作用于释放当帧。
        public void BeginFrame()
        {
            releaseGrace = false;
        }

        public bool AnyOwned
        {
            get
            {
                for (var button = 0; button < ButtonCount; button++)
                {
                    if (owned[button])
                        return true;
                }
                return false;
            }
        }

        public bool AnyHeld
        {
            get
            {
                for (var button = 0; button < ButtonCount; button++)
                {
                    if (held[button])
                        return true;
                }
                return false;
            }
        }

        public bool IsHeld(int button) => Valid(button) && held[button];

        public bool IsOwned(int button) => Valid(button) && owned[button];

        // 窗口内开始的手势优先屏蔽；窗口外手势按住或刚松开时放行，让游戏收到完整释放；
        // 没有按住任何键且不在释放帧时按悬停判定，保留滚轮与首次点击的隔离。
        public bool ShouldBlock(bool overWindow)
        {
            if (AnyOwned)
                return true;
            if (AnyHeld)
                return false;
            // 外部手势的释放帧放行，避免清空射线后 NodeConnectDrop 收不到 drop。
            if (releaseGrace)
                return false;
            return overWindow;
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

        private static bool Valid(int button) => button >= 0 && button < ButtonCount;
    }
}
