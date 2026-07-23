using ADV;
using HEdit;
using Manager;
using UnityEngine;
using UnityEngine.UI;

namespace EC_ADVCameraViewport
{
    /// <summary>
    /// 视口控制器 - 负责监听快捷键并调整摄像机视口及 ADV Canvas 相机
    ///
    /// 渲染管线说明（EC 多相机架构）：
    /// - cam.thisCmaera: 主 3D 场景相机，修改其 rect 缩放 3D 渲染区域
    /// - cameraADVCanvas: ADV Canvas 专用相机，需同步 rect 使 UI 跟随视口
    /// - 游戏已通过 ADV.SetCanvasCamera(cameraADVCanvas) 绑定，本插件只同步 rect
    /// </summary>
    internal class ViewportController : MonoBehaviour
    {
        private Camera _cachedCamera;       // 主场景相机
        private Camera _cachedADVCanvasCam; // ADV Canvas 专用相机
        private Rect _originalRect;
        private Rect _originalADVCanvasRect;
        private bool _isScaled;

        // Canvas 缩放（覆盖 CanvasScaler，手动控制 scaleFactor 使内容跟随视口）
        private Canvas _advCanvas;
        private CanvasScaler _advCanvasScaler;
        private float _originalAdvScaleFactor;
        private bool _advScalerWasEnabled;

        internal void Update()
        {
            if (ADVCameraViewportPlugin.ToggleKey.Value.IsDown())
            {
                if (ADVCameraViewportPlugin.EnableViewportScale.Value)
                {
                    // 已启用 → 禁用
                    ADVCameraViewportPlugin.EnableViewportScale.Value = false;
                }
                else
                {
                    // 已禁用 → 启用
                    ADVCameraViewportPlugin.EnableViewportScale.Value = true;
                }
            }
        }

        /// <summary>
        /// 配置变更时重新应用（由 SettingChanged 事件触发），无需重启游戏。
        /// </summary>
        internal void OnSettingChanged()
        {
            if (!ADVCameraViewportPlugin.EnableViewportScale.Value)
            {
                _isScaled = false;
                RestoreViewport();
                ADVCameraViewportPlugin.Log.LogInfo("视口缩放已通过配置禁用。");
                return;
            }

            _isScaled = true;
            ApplyViewportScale(force: true);
        }

        internal void LateUpdate()
        {
            if (ADVCameraViewportPlugin.EnableViewportScale.Value && _isScaled && _cachedCamera != null)
            {
                TryApplyViewport();
            }
        }

        private void ApplyViewportScale(bool force)
        {
            if (!TryGetADVCamera(out Camera cam))
            {
                return;
            }

            if (_cachedCamera != cam || force)
            {
                _cachedCamera = cam;
                _originalRect = new Rect(0f, 0f, 1f, 1f);
            }

            float scale = ADVCameraViewportPlugin.ViewportScale.Value;
            float offsetX = ADVCameraViewportPlugin.ViewportOffsetX.Value;
            float offsetY = ADVCameraViewportPlugin.ViewportOffsetY.Value;

            float centerOffsetX = (1f - scale) * 0.5f;
            float centerOffsetY = (1f - scale) * 0.5f;

            float finalX = Mathf.Clamp(centerOffsetX + offsetX, 0f, 1f - scale);
            float finalY = Mathf.Clamp(centerOffsetY + offsetY, 0f, 1f - scale);

            Rect scaledRect = new Rect(finalX, finalY, scale, scale);

            // 1) 主相机 viewport
            if (cam.rect != scaledRect)
            {
                cam.rect = scaledRect;
            }

            // 2) ADV Canvas 专用相机 viewport + 内容缩放
            if (ADVCameraViewportPlugin.ScaleADVCanvas.Value)
            {
                SyncADVCanvasCameraRect(scaledRect, force);
                ApplyCanvasScale(scale, force);
            }
        }

        /// <summary>
        /// 同步 cameraADVCanvas 的 rect，使 ADV Canvas 跟随主相机 viewport。
        /// </summary>
        private void SyncADVCanvasCameraRect(Rect targetRect, bool force)
        {
            if (_cachedADVCanvasCam == null || force)
            {
                if (!TryGetADVCanvasCamera(out _cachedADVCanvasCam))
                {
                    return;
                }
                _originalADVCanvasRect = new Rect(0f, 0f, 1f, 1f);
            }

            if (_cachedADVCanvasCam == null) return;

            if (_cachedADVCanvasCam.rect != targetRect)
            {
                _cachedADVCanvasCam.rect = targetRect;
            }
        }

        /// <summary>
        /// 覆盖 CanvasScaler，手动设置 scaleFactor = 原始值 × 视口比例。
        /// 使 ADV Canvas 内容实际缩小，而非被 camera.rect 裁剪。
        /// </summary>
        private void ApplyCanvasScale(float viewportScale, bool force)
        {
            if (_advCanvas == null || force)
            {
                if (!TryGetADVCanvas(out _advCanvas, out _advCanvasScaler))
                {
                    return;
                }
                _originalAdvScaleFactor = _advCanvas.scaleFactor;
                _advScalerWasEnabled = _advCanvasScaler != null && _advCanvasScaler.enabled;
            }

            if (_advCanvas == null) return;

            // 停用 CanvasScaler 自动计算，手动控制
            if (_advCanvasScaler != null && _advCanvasScaler.enabled)
            {
                _advCanvasScaler.enabled = false;
            }

            _advCanvas.scaleFactor = _originalAdvScaleFactor * viewportScale;
        }

        /// <summary>
        /// 恢复 Canvas scaleFactor，重新启用 CanvasScaler
        /// </summary>
        private void RestoreCanvasScale()
        {
            if (_advCanvas != null)
            {
                _advCanvas.scaleFactor = _originalAdvScaleFactor;
            }

            if (_advCanvasScaler != null && _advScalerWasEnabled)
            {
                _advCanvasScaler.enabled = true;
            }

            _advCanvas = null;
            _advCanvasScaler = null;
        }

        private void RestoreViewport()
        {
            if (_cachedCamera != null)
            {
                _cachedCamera.rect = _originalRect;
                _cachedCamera = null;
            }

            if (_cachedADVCanvasCam != null)
            {
                _cachedADVCanvasCam.rect = _originalADVCanvasRect;
                _cachedADVCanvasCam = null;
            }

            RestoreCanvasScale();
        }

        private void TryApplyViewport()
        {
            if (!TryGetADVCamera(out Camera cam))
            {
                return;
            }

            float scale = ADVCameraViewportPlugin.ViewportScale.Value;
            float offsetX = ADVCameraViewportPlugin.ViewportOffsetX.Value;
            float offsetY = ADVCameraViewportPlugin.ViewportOffsetY.Value;

            float centerOffsetX = (1f - scale) * 0.5f;
            float centerOffsetY = (1f - scale) * 0.5f;

            float finalX = Mathf.Clamp(centerOffsetX + offsetX, 0f, 1f - scale);
            float finalY = Mathf.Clamp(centerOffsetY + offsetY, 0f, 1f - scale);

            Rect expectedRect = new Rect(finalX, finalY, scale, scale);

            // 检测主相机是否被重置
            if (cam.rect != expectedRect && cam.rect == new Rect(0, 0, 1, 1))
            {
                cam.rect = expectedRect;
            }

            // 检测 cameraADVCanvas 是否被重置
            if (ADVCameraViewportPlugin.ScaleADVCanvas.Value
                && _cachedADVCanvasCam != null
                && _cachedADVCanvasCam.rect != expectedRect
                && _cachedADVCanvasCam.rect == new Rect(0, 0, 1, 1))
            {
                _cachedADVCanvasCam.rect = expectedRect;
            }

            // 检测 CanvasScaler 是否被游戏重新启用（覆盖了我们的 scaleFactor）
            if (ADVCameraViewportPlugin.ScaleADVCanvas.Value
                && _advCanvas != null
                && _advCanvasScaler != null
                && _advCanvasScaler.enabled)
            {
                _advCanvasScaler.enabled = false;
                _advCanvas.scaleFactor = _originalAdvScaleFactor * scale;
            }
        }

        private bool IsInADVScene()
        {
            return Singleton<HEditGlobal>.IsInstance();
        }

        private bool TryGetADVCamera(out Camera camera)
        {
            camera = null;

            if (!IsInADVScene())
            {
                return false;
            }

            var camCtrl = Singleton<HEditGlobal>.Instance?.cam;
            if (camCtrl == null)
            {
                return false;
            }

            camera = camCtrl.thisCmaera;
            return camera != null;
        }

        private bool TryGetADVCanvasCamera(out Camera camera)
        {
            camera = null;

            if (!IsInADVScene())
            {
                return false;
            }

            camera = Singleton<HEditGlobal>.Instance?.cameraADVCanvas;
            return camera != null;
        }

        /// <summary>
        /// 从 ADV 单例获取 Canvas 和 CanvasScaler
        /// </summary>
        private bool TryGetADVCanvas(out Canvas canvas, out CanvasScaler scaler)
        {
            canvas = null;
            scaler = null;

            if (!IsInADVScene() || !Singleton<ADV.ADV>.IsInstance())
            {
                return false;
            }

            canvas = Singleton<ADV.ADV>.Instance?.canvas;
            if (canvas == null)
            {
                return false;
            }

            scaler = canvas.GetComponent<CanvasScaler>();
            return true;
        }
    }
}
