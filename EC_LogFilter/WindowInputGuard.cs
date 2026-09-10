using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using HEdit;
using Map;
using UnityEngine;
using UnityEngine.EventSystems;

namespace EC_LogFilter
{
    internal sealed class WindowInputGuard : IDisposable
    {
        private static WindowInputGuard _active;
        private readonly LogFilterWindow _window;
        private readonly Harmony _harmony;
        private readonly HashSet<MethodInfo> _targets = new HashSet<MethodInfo>();

        internal WindowInputGuard(LogFilterWindow window, string harmonyId)
        {
            _window = window;
            _harmony = new Harmony(harmonyId);
        }

        internal bool IsInstalled { get; private set; }
        internal string ErrorDetail { get; private set; } = string.Empty;

        internal bool Install()
        {
            if (IsInstalled) return true;
            try
            {
                if (_active != null && _active != this) throw new InvalidOperationException("窗口输入守卫已经存在。");
                _active = this;
                PatchMouse(typeof(BaseCameraControl), nameof(MousePrefix));
                PatchMouse(typeof(BaseCameraControl_Ver2), nameof(CameraV2MousePrefix));
                PatchMouse(typeof(CameraCtrl), nameof(MousePrefix));
                PatchMouse(typeof(SplitCameraCtrl), nameof(MousePrefix));
                foreach (Type type in new[] { typeof(CameraControl), typeof(CameraControl_Ver2), typeof(CameraCtrl), typeof(SplitCameraCtrl) })
                    Patch(type, "LateUpdate", nameof(KeyboardPrefix));
                Patch(typeof(ShortcutKeyCtrl), "Update", nameof(KeyboardPrefix));
                // HEdit 的 F1/F2/F3/Esc 位于原生 isInputNow 检查之前。
                Patch(typeof(HEditScene), "ShortcutKey", nameof(KeyboardPrefix));
                Patch(typeof(HEditGlobal), "get_isInputNow", nameof(TextInputPostfix), true);
                Patch(typeof(EventSystem), "RaycastAll", nameof(RaycastPostfix), true);
                IsInstalled = true;
                return true;
            }
            catch (Exception ex)
            {
                ErrorDetail = ex.Message;
                Dispose();
                return false;
            }
        }

        private void PatchMouse(Type type, string prefix)
        {
            Patch(type, "InputMouseProc", prefix);
            Patch(type, "InputMouseWheelZoomProc", prefix);
        }

        private void Patch(Type type, string name, string hook, bool postfix = false)
        {
            MethodInfo method = AccessTools.Method(type, name);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            if (!_targets.Add(method)) return;
            var patch = new HarmonyMethod(typeof(WindowInputGuard), hook);
            _harmony.Patch(method, prefix: postfix ? null : patch, postfix: postfix ? patch : null);
        }

        private static bool MousePrefix() => _active == null || !_active._window.WantsMouseInput();

        private static bool CameraV2MousePrefix(BaseCameraControl_Ver2 __instance)
            => !(__instance is CameraControl_Ver2) || MousePrefix();

        // 普通悬停只拦鼠标入口，不能阻断相机键盘控制和帧更新。
        private static bool KeyboardPrefix() => _active == null || !_active._window.TextFocused;

        private static void TextInputPostfix(ref bool __result)
        {
            if (_active != null && _active._window.TextFocused) __result = true;
        }

        private static void RaycastPostfix(PointerEventData __0, List<RaycastResult> __1)
        {
            if (_active == null || !_active._window.Visible || __0 == null || __1 == null || __1.Count == 0) return;
            Vector2 point = new Vector2(__0.position.x, Screen.height - __0.position.y);
            if (_active._window.BlocksMouse(point)) __1.Clear();
        }

        public void Dispose()
        {
            if (_active == this) _active = null;
            try { _harmony.UnpatchSelf(); }
            catch (Exception ex) { if (ErrorDetail.Length == 0) ErrorDetail = ex.Message; }
            _targets.Clear();
            IsInstalled = false;
        }
    }
}
