using System;
using System.Collections.Generic;
using HarmonyLib;
using ADVCreate;
using HEdit;
using UnityEngine;
using UnityEngine.EventSystems;

namespace EC_ADVCutSync
{
    // 面板命中区阻断 uGUI 射线与相机；手势归属按按下位置判定，窗口外手势放行到松开。
    // 禁止调用 Input.ResetInputAxes()：它会一并清掉按住状态，使窗口外手势丢失松开边沿。
    internal static class WindowInputGuard
    {
        private static readonly PointerOwnership Ownership = new PointerOwnership();
        private static int sampledFrame = -1;
        private static BaseCameraControl_Ver2 hookedCamera;
        private static BaseCameraControl_Ver2.NoCtrlFunc cameraLock;
        // 原生会用 Delegate.Combine 更换条件链，用链引用比较发现替换后重装
        private static BaseCameraControl_Ver2.NoCtrlFunc installedChain;
        private static bool loggedCameraFailure;

        public static void Sample()
        {
            var frame = Time.frameCount;
            if (frame == sampledFrame)
                return;
            sampledFrame = frame;
            Ownership.BeginFrame();
            var over = CutSyncWindow.IsMouseOverWindow();
            for (var button = 0; button < PointerOwnership.ButtonCount; button++)
            {
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

        public static bool BlocksGameInput()
        {
            if (!CutSyncWindow.IsVisible)
            {
                ReleaseCameraLock();
                return false;
            }
            Sample();
            var block = Ownership.ShouldBlock(CutSyncWindow.IsMouseOverWindow());
            if (block)
                EnsureCameraLock();
            else
                ReleaseCameraLock();
            return block;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EventSystem), "RaycastAll")]
        private static void RaycastAllPostfix(List<RaycastResult> raycastResults)
        {
            try
            {
                if (raycastResults != null && raycastResults.Count > 0 && BlocksGameInput())
                    raycastResults.Clear();
            }
            catch
            {
                // 钩子内异常不能打断 EventSystem，未命中即不屏蔽
            }
        }

        // isInputNow 参与 CameraKeyCondition 与 PreviewCut 判定；本窗口持有 IMGUI 焦点时取或
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ADVPartUICtrl), "get_isInputNow")]
        private static void AdvIsInputNowPostfix(ref bool __result)
        {
            if (__result)
                return;
            __result = CutSyncWindow.HasKeyboardFocus;
        }

        // 清空 uGUI 射线会让相机认为鼠标没点在 UI 上而放出，用追加委托锁住相机
        private static void EnsureCameraLock()
        {
            try
            {
                var cam = Singleton<HEditGlobal>.IsInstance() ? Singleton<HEditGlobal>.Instance.cam : null;
                if (cam == null)
                    return;
                if (ReferenceEquals(hookedCamera, cam) && cameraLock != null
                    && ReferenceEquals(cam.NoCtrlCondition, installedChain))
                    return;
                ReleaseCameraLock();
                cameraLock = CameraLockFunc;
                cam.NoCtrlCondition = (BaseCameraControl_Ver2.NoCtrlFunc)Delegate.Combine(cam.NoCtrlCondition, cameraLock);
                installedChain = cam.NoCtrlCondition;
                hookedCamera = cam;
            }
            catch (Exception ex)
            {
                LogCameraFailure("camera guard unavailable: " + ex.Message);
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
                LogCameraFailure("camera guard removal failed: " + ex.Message);
            }
            hookedCamera = null;
            cameraLock = null;
            installedChain = null;
        }

        // 相机委托只读：不在 GetInvocationList 迭代期间改动委托链
        private static bool CameraLockFunc()
        {
            if (!CutSyncWindow.IsVisible)
                return false;
            Sample();
            return Ownership.ShouldBlock(CutSyncWindow.IsMouseOverWindow());
        }

        private static void LogCameraFailure(string message)
        {
            if (loggedCameraFailure)
                return;
            loggedCameraFailure = true;
            CutSyncPlugin.Log.LogWarning("cutsync window " + message);
        }
    }
}
