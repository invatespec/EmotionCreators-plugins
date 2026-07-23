using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace EC_ADVCameraViewport
{
    internal static class UILayoutController
    {
        private static GameObject _advPartRoot;
        internal static GameObject AdvPartRoot => _advPartRoot;
        private static Transform _charaStatePanel;
        private static Vector3 _originalCharaStatePos;
        private static bool _isCharaStateMoved;

        private static Transform _listPanel;

        // 每个 Button Root 路径的原始按钮宽度（首次保存，防二次乘算）
        private static Dictionary<string, float[]> _originalWidths = new Dictionary<string, float[]>();
        private static bool _originalSaved;

        internal static void ApplyUIAdjustments()
        {
            _advPartRoot = GameObject.Find("ADVPart");
            if (_advPartRoot == null)
            {
                ADVCameraViewportPlugin.Log.LogWarning("未找到 ADVPart 根对象，跳过 UI 调整。");
                return;
            }

            if (ADVCameraViewportPlugin.ListWidthScale.Value < 1f)
                ShrinkListPanel();

            if (ADVCameraViewportPlugin.AutoAdjustCharaState.Value)
                SetupCharaStateAutoAdjust();

            ADVCameraViewportPlugin.Log.LogInfo("UI 布局调整已应用。");
        }

        /// <summary> 配置变更后重应用，由 SettingChanged 事件触发 </summary>
        internal static void ReapplyUIAdjustments()
        {
            if (_advPartRoot == null)
            {
                ApplyUIAdjustments();
                return;
            }
            if (ADVCameraViewportPlugin.ListWidthScale.Value < 1f)
                ShrinkListPanel();
        }

        /// <summary> AutoAdjustCharaState 关闭时恢复位置并移除监听 </summary>
        internal static void RestoreCharaState()
        {
            if (_charaStatePanel == null) return;

            if (_isCharaStateMoved)
            {
                _charaStatePanel.position = _originalCharaStatePos;
                _isCharaStateMoved = false;
            }

            // 移除 Toggle 监听
            Transform buttonChara = _advPartRoot?.transform.Find("Canvas ADVPart/Manipulate/Button Root/Button Chara");
            if (buttonChara != null)
            {
                Toggle toggle = buttonChara.GetComponent<Toggle>();
                if (toggle != null)
                    toggle.onValueChanged.RemoveListener(OnCharaToggleChanged);
            }

            _charaStatePanel = null;
            ADVCameraViewportPlugin.Log.LogInfo("CharaState 已恢复并取消监听。");
        }

        /// <summary> 首次保存原始值，后续用原始值 × scale，防二次乘算 </summary>
        private static float _originalListWidth;

        private static void ShrinkListPanel()
        {
            float scale = ADVCameraViewportPlugin.ListWidthScale.Value;

            if (_listPanel == null)
                _listPanel = _advPartRoot.transform.Find("Canvas ADVPart/List");
            if (_listPanel == null) return;

            RectTransform listRect = _listPanel.GetComponent<RectTransform>();
            if (listRect != null && !_originalSaved)
                _originalListWidth = listRect.sizeDelta.x;

            if (listRect != null && _originalListWidth > 0)
                listRect.sizeDelta = new Vector2(_originalListWidth * scale, listRect.sizeDelta.y);

            // 两处 Button Root：Cut List 和 Text Effect List
            ShrinkButtonRoot("Canvas ADVPart/List/Cut List/Button Root",
                new[] { "Button Add", "Button Del", "Button Copy", "Button Paste" },
                scale);

            ShrinkButtonRoot("Canvas ADVPart/List/Text Effect List/Button Root",
                new[] { "Button Add Text", "Button Add Effect", "Button Duplicate", "Button Del" },
                scale);

            Canvas.ForceUpdateCanvases();
            _originalSaved = true;
        }

        /// <summary>
        /// 禁用 GridLayoutGroup + ContentSizeFitter，缩放按钮宽度并 grid_2x2 居中排列。
        /// </summary>
        private static void ShrinkButtonRoot(string path, string[] names, float scale)
        {
            Transform buttonRoot = _advPartRoot.transform.Find(path);
            if (buttonRoot == null) return;

            DisableContentSizeFitter(buttonRoot.parent);
            DisableContentSizeFitter(buttonRoot);

            var grid = buttonRoot.GetComponent<GridLayoutGroup>();
            if (grid == null) return;

            float spacing = grid.spacing.x;
            float cellHeight = grid.cellSize.y;
            grid.enabled = false;

            // 首次保存原始宽度
            if (!_originalWidths.ContainsKey(path))
                _originalWidths[path] = new float[names.Length];

            float[] btnWidths = new float[names.Length];
            RectTransform[] btnRects = new RectTransform[names.Length];

            for (int i = 0; i < names.Length; i++)
            {
                Transform btn = buttonRoot.Find(names[i]);
                if (btn == null) continue;

                RectTransform rect = btn.GetComponent<RectTransform>();
                if (rect == null) continue;

                btnRects[i] = rect;

                if (!_originalSaved && _originalWidths[path] != null)
                    _originalWidths[path][i] = rect.rect.width;

                btnWidths[i] = _originalWidths[path][i] * scale;
            }

            // grid_2x2 居中排列
            float rootWidth = buttonRoot.GetComponent<RectTransform>().rect.width;
            float padding = 8f;

            float colWidth = Mathf.Max(btnWidths[0], btnWidths[2]);
            float colWidth2 = Mathf.Max(btnWidths[1], btnWidths[3]);
            float gridContentWidth = colWidth + spacing + colWidth2;
            float startX = (rootWidth - gridContentWidth) * 0.5f;
            float[] colX = { startX, startX + colWidth + spacing };

            for (int i = 0; i < btnRects.Length; i++)
            {
                if (btnRects[i] == null) continue;
                int col = i % 2;
                int row = i / 2;

                btnRects[i].SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, colX[col],
                    col == 0 ? colWidth : colWidth2);
                btnRects[i].SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top,
                    padding + row * (cellHeight + spacing), cellHeight);
            }
        }

        private static void DisableContentSizeFitter(Transform t)
        {
            if (t == null) return;
            var csf = t.GetComponent<ContentSizeFitter>();
            if (csf != null && csf.enabled) csf.enabled = false;
        }

        internal static void SetupCharaStateAutoAdjust()
        {
            Transform buttonChara = _advPartRoot.transform.Find("Canvas ADVPart/Manipulate/Button Root/Button Chara");
            if (buttonChara == null) return;

            Toggle toggle = buttonChara.GetComponent<Toggle>();
            if (toggle == null) return;

            _charaStatePanel = _advPartRoot.transform.Find("Canvas ADVPart/Manipulate/Chara/State");
            if (_charaStatePanel == null) return;

            _originalCharaStatePos = _charaStatePanel.position;
            // 先移除防重复注册
            toggle.onValueChanged.RemoveListener(OnCharaToggleChanged);
            toggle.onValueChanged.AddListener(OnCharaToggleChanged);
        }

        private static void OnCharaToggleChanged(bool isOn)
        {
            if (_charaStatePanel == null) return;

            if (isOn)
            {
                float targetX = Screen.width * (ADVCameraViewportPlugin.CharaStatePosX.Value / 100f);
                float targetY = Screen.height * (ADVCameraViewportPlugin.CharaStatePosY.Value / 100f);
                _charaStatePanel.position = new Vector3(targetX, targetY, _charaStatePanel.position.z);
                _isCharaStateMoved = true;
            }
            else if (_isCharaStateMoved)
            {
                _charaStatePanel.position = _originalCharaStatePos;
                _isCharaStateMoved = false;
            }
        }
    }
}
