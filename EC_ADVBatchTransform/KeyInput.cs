using UnityEngine;
using UnityEngine.EventSystems;

namespace EC_ADVBatchTransform
{
    // 方向键移动、Alt+←/→ 旋转、G 切换中心、Ctrl+Z 撤销；方向键被插件使用时让相机不响应
    internal static class KeyInput
    {
        private const float RepeatDelay = 0.35f;
        private const float RepeatInterval = 0.03f;

        private static readonly BaseCameraControl_Ver2.NoCtrlFunc cameraCondition = () => !WantsArrows();
        private static float nextStepTime;
        private static EventSystem navOwner;
        private static bool navPrev;

        // 槽位模式下只有按着 Alt 才由插件处理，普通方向键仍交给相机
        internal static bool WantsArrows()
        {
            if (!AnyArrowHeld() || MultiSelection.Count == 0) return false;
            if (!EditorContext.CanOperate || EditorContext.IsTyping) return false;
            return RotateHeld() || EditorContext.ActiveSlot < 0;
        }

        // 相机的多个 KeyCondition 按与组合；先删再加保证只挂一次
        internal static void HookCamera()
        {
            if (!Singleton<HEdit.HEditGlobal>.IsInstance()) return;
            var cam = Singleton<HEdit.HEditGlobal>.Instance.cam;
            if (cam == null) return;
            var cond = (BaseCameraControl_Ver2.NoCtrlFunc)System.Delegate.Remove(cam.KeyCondition, cameraCondition);
            cam.KeyCondition = (BaseCameraControl_Ver2.NoCtrlFunc)System.Delegate.Combine(cond, cameraCondition);
        }

        // 先结束已松开的手势，松开鼠标那一帧按的 Ctrl+Z 才不会被手势守卫吞掉
        internal static void Tick()
        {
            EndReleasedGestures();
            HandleHotkeys();
            HandleCycle();
            HandleArrows();
            UpdateNav(MultiSelection.Count > 0 && EditorContext.CanOperate);
        }

        internal static void ReleaseNav() => UpdateNav(false);

        private static void HandleHotkeys()
        {
            bool pivot = ADVBatchTransformPlugin.PivotToggleKey.Value.IsDown();
            bool undo = ADVBatchTransformPlugin.UndoKey.Value.IsDown();
            if (!pivot && !undo) return;
            if (EditorContext.IsTyping) return;
            // 撤销看页面而非 CanOperate：预览、隐藏 UI 时面板改动也要能撤销
            if (undo && EditorContext.InTextEffectPage && !BatchOps.Active && !SlotOps.Active
                && !Input.GetMouseButton(0))
            {
                UndoStack.Undo();
            }
            if (pivot && EditorContext.CanOperate) TogglePivot();
        }

        // 上一个/下一个对象的快捷键。多选时停用，避免把整组选中打散
        private static void HandleCycle()
        {
            if (!EditorContext.CanOperate || EditorContext.IsTyping || MultiSelection.IsMulti) return;
            bool next = ADVBatchTransformPlugin.CycleNextKey.Value.IsDown();
            bool prev = ADVBatchTransformPlugin.CyclePrevKey.Value.IsDown();
            if (!next && !prev) return;
            // 切对象时 EventSystem 的焦点也会跟着走，先关掉 UI 导航
            UpdateNav(true);
            MultiSelection.Step(next ? 1 : -1);
        }

        private static void TogglePivot()
        {
            var cfg = ADVBatchTransformPlugin.Pivot;
            cfg.Value = cfg.Value == PivotMode.Group ? PivotMode.Individual : PivotMode.Group;
            ADVBatchTransformPlugin.Log.LogMessage(cfg.Value == PivotMode.Group ? "旋转/缩放中心：整组中心" : "旋转/缩放中心：各自中心");
        }

        // 拖动手势在松开鼠标左键时结束；键盘手势在插件不再接管方向键时结束（松键、点开槽位、预览等）
        private static void EndReleasedGestures()
        {
            bool mouse = Input.GetMouseButton(0);
            bool keys = WantsArrows();
            if (BatchOps.Active && !(BatchOps.IsKeyGesture ? keys : mouse)) BatchOps.End();
            if (SlotOps.Active && !keys && !mouse) SlotOps.End();
        }

        private static void HandleArrows()
        {
            if (Input.GetMouseButton(0) || !WantsArrows() || !StepDue()) return;
            bool large = LargeHeld();
            if (RotateHeld())
            {
                Rotate(HorizontalDir(), large);
                return;
            }
            var dir = ArrowDir();
            if (dir == Vector2.zero) return;
            float step = large ? ADVBatchTransformPlugin.MoveStepLarge.Value : ADVBatchTransformPlugin.MoveStep.Value;
            BatchOps.KeyMove(dir * step);
        }

        // 按下立即一步，之后先等 RepeatDelay，再每 RepeatInterval 一步
        private static bool StepDue()
        {
            float now = Time.unscaledTime;
            if (AnyArrowDown())
            {
                nextStepTime = now + RepeatDelay;
                return true;
            }
            if (now < nextStepTime) return false;
            nextStepTime = now + RepeatInterval;
            return true;
        }

        // Alt+→ 顺时针（角度减小）
        private static void Rotate(int dir, bool large)
        {
            if (dir == 0) return;
            float step = large ? ADVBatchTransformPlugin.RotateStepLarge.Value : ADVBatchTransformPlugin.RotateStep.Value;
            float delta = -dir * step;
            if (EditorContext.ActiveSlot >= 0) SlotOps.KeyRotate(delta);
            else BatchOps.KeyRotate(delta);
        }

        // 方向键会让 EventSystem 改到当前选中的滑条或开关，有选中对象时关闭 UI 导航
        private static void UpdateNav(bool disable)
        {
            if (disable && navOwner == null)
            {
                var es = EventSystem.current;
                if (es == null) return;
                navOwner = es;
                navPrev = es.sendNavigationEvents;
                es.sendNavigationEvents = false;
            }
            else if (!disable && navOwner != null)
            {
                navOwner.sendNavigationEvents = navPrev;
                navOwner = null;
            }
        }

        private static bool AnyArrowHeld()
        {
            return Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow)
                || Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow);
        }

        private static bool AnyArrowDown()
        {
            return Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow)
                || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow);
        }

        private static int HorizontalDir()
        {
            int dir = 0;
            if (Input.GetKey(KeyCode.RightArrow)) dir++;
            if (Input.GetKey(KeyCode.LeftArrow)) dir--;
            return dir;
        }

        private static Vector2 ArrowDir()
        {
            var dir = new Vector2(HorizontalDir(), 0f);
            if (Input.GetKey(KeyCode.UpArrow)) dir.y++;
            if (Input.GetKey(KeyCode.DownArrow)) dir.y--;
            return dir;
        }

        private static bool RotateHeld() => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        private static bool LargeHeld() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    }
}
