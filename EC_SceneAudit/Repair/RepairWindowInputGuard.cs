using System;
using BepInEx.Logging;
using HEdit;
using UnityEngine;

namespace EC_SceneAudit.Repair
{
    // 本窗口输入隔离的唯一判定入口：Harmony 钩子、IMGUI 事件消费和相机守卫共用同一份状态。
    // 禁止调用 Input.ResetInputAxes()：它会一并清掉"按住"状态，使窗口外手势丢失松开边沿。
    internal static class RepairWindowInputGuard
    {
        private static readonly RepairPointerOwnership Ownership = new RepairPointerOwnership();
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("EC_SceneAudit");

        private static int sampledFrame = -1;
        private static BaseCameraControl_Ver2 hookedCamera;
        private static BaseCameraControl_Ver2.NoCtrlFunc cameraLock;
        // NoCtrlCondition 的安装后链值：HEdit 的拖拽会整体替换该委托链并连带清掉本锁，
        // 必须用链引用比较发现替换后重装。
        private static BaseCameraControl_Ver2.NoCtrlFunc installedChain;
        private static bool loggedCameraFailure;

        // 每帧采样一次鼠标键边沿，重复调用直接返回；边沿标志帧内不变，因此调用顺序不影响结果。
        public static void Sample()
        {
            var frame = Time.frameCount;
            if (frame == sampledFrame)
                return;
            sampledFrame = frame;
            Ownership.BeginFrame();

            var over = RepairPreflightWindow.IsMouseOverWindow();
            for (var button = 0; button < RepairPointerOwnership.ButtonCount; button++)
            {
                // 极快点击可能同帧同时报告按下与松开，两个边沿都要处理，否则残留归窗口状态。
                if (Input.GetMouseButtonDown(button))
                    Ownership.ButtonDown(button, over);
                if (Input.GetMouseButtonUp(button))
                    Ownership.ButtonUp(button);
            }
        }

        public static void Reset()
        {
            Ownership.Reset();
            ReleaseCameraLock();
        }

        // 是否屏蔽游戏输入（节点射线、节点 Update 滚动缩放、相机）。
        public static bool BlocksGameInput()
        {
            var window = RepairPreflightWindow.Instance;
            if (window == null || !window.Visible)
            {
                ReleaseCameraLock();
                return false;
            }

            Sample();
            var block = Ownership.ShouldBlock(RepairPreflightWindow.IsMouseOverWindow());
            if (block)
                EnsureCameraLock();
            else
                ReleaseCameraLock();
            return block;
        }

        // 清空 uGUI 射线会让 HEdit 认为鼠标没点在 UI 上，从而把相机放出来；
        // 这里用追加委托把相机锁住，不改 HEdit 自己的 isDrag 状态，关闭窗口时移除。
        private static void EnsureCameraLock()
        {
            try
            {
                if (!Singleton<HEditGlobal>.IsInstance())
                    return;
                var cam = Singleton<HEditGlobal>.Instance?.cam;
                if (cam == null)
                    return;
                // 链被 HEdit 替换后必须重装，否则窗口内拖动会连带移动相机。
                if (ReferenceEquals(hookedCamera, cam) && cameraLock != null
                    && ReferenceEquals(cam.NoCtrlCondition, installedChain))
                    return;

                ReleaseCameraLock();
                cameraLock = new BaseCameraControl_Ver2.NoCtrlFunc(CameraLockFunc);
                cam.NoCtrlCondition = (BaseCameraControl_Ver2.NoCtrlFunc)Delegate.Combine(cam.NoCtrlCondition, cameraLock);
                installedChain = cam.NoCtrlCondition;
                hookedCamera = cam;
            }
            catch (Exception ex)
            {
                if (!loggedCameraFailure)
                {
                    loggedCameraFailure = true;
                    Log.LogWarning("Repair window camera guard unavailable: " + ex.Message);
                }
            }
        }

        private static void ReleaseCameraLock()
        {
            if (hookedCamera == null || cameraLock == null)
                return;
            try
            {
                hookedCamera.NoCtrlCondition = (BaseCameraControl_Ver2.NoCtrlFunc)Delegate.Remove(hookedCamera.NoCtrlCondition, cameraLock);
            }
            catch (Exception ex)
            {
                if (!loggedCameraFailure)
                {
                    loggedCameraFailure = true;
                    Log.LogWarning("Repair window camera guard removal failed: " + ex.Message);
                }
            }
            hookedCamera = null;
            cameraLock = null;
            installedChain = null;
        }

        // 相机委托只读：不安装/卸载，避免在 GetInvocationList 迭代期间改动委托链。
        private static bool CameraLockFunc()
        {
            var window = RepairPreflightWindow.Instance;
            if (window == null || !window.Visible)
                return false;
            Sample();
            return Ownership.ShouldBlock(RepairPreflightWindow.IsMouseOverWindow());
        }
    }
}
