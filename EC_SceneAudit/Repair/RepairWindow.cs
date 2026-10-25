using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HEdit;
using UnityEngine;

namespace EC_SceneAudit.Repair
{
    internal sealed class RepairWindow
    {
        private const int WindowId = 0x53415046;
        private const float Padding = 12f;
        private const float TitleHeight = 40f;
        private const float FooterHeight = 58f;
        internal static RepairWindow Instance;

        private readonly ManualLogSource log;
        private readonly ConfigEntry<string> savedPlanPath;
        private readonly ConfigEntry<string> savedNodePatchPath;
        private readonly RepairWindowStyles styles = new RepairWindowStyles();
        private readonly RepairApplyEngine applyEngine = new RepairApplyEngine();
        private readonly NodePatchEngine nodePatchEngine = new NodePatchEngine();
        private RepairApplyReport applyReport;
        private NodePatchApplyReport nodePatchReport;
        private string planPath;
        private string nodePatchPath;
        private string repairStatus = string.Empty;
        private string nodeStatus = string.Empty;
        private bool repairError;
        private bool nodeError;
        private bool nodeTab;
        private bool visible;
        private bool inputFocused;
        private bool unfocusRequested;
        private Rect guiRect;
        private Rect windowRect;
        private Vector2 normalizedPosition = new Vector2(0.5f, 0.35f);
        private Vector2 scroll;
        private Vector2 pathScroll;
        private int screenWidth;
        private int screenHeight;
        private bool movedSinceSync;

        public bool Visible => visible;
        internal static bool HasInputFocus => Instance != null && Instance.visible && Instance.inputFocused;

        public RepairWindow(ManualLogSource log, ConfigEntry<string> savedPlanPath, ConfigEntry<string> savedNodePatchPath)
        {
            this.log = log;
            this.savedPlanPath = savedPlanPath;
            this.savedNodePatchPath = savedNodePatchPath;
            planPath = RepairPathText.Sanitize(savedPlanPath.Value);
            nodePatchPath = RepairPathText.Sanitize(savedNodePatchPath.Value);
        }

        public void Open() { visible = true; }

        public void Close()
        {
            FinishMove();
            visible = false;
            inputFocused = false;
            unfocusRequested = true;
            guiRect = new Rect();
            RepairWindowInputGuard.Reset();
        }

        internal void ReleaseInput()
        {
            inputFocused = false;
            unfocusRequested = true;
            RepairWindowInputGuard.Reset();
        }

        internal void Dispose()
        {
            Close();
            styles.Dispose();
        }

        internal static bool IsMouseOverWindow()
        {
            if (Instance == null || !Instance.visible)
                return false;
            return Instance.guiRect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
        }

        public void OnGUI()
        {
            // 隐藏前留下的焦点也要释放，避免 InputHotkeyBlock 屏蔽重新打开窗口的快捷键。
            if (unfocusRequested || (visible && Event.current.type == EventType.MouseDown
                && !guiRect.Contains(Event.current.mousePosition)))
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
                GUI.UnfocusWindow();
                unfocusRequested = false;
                inputFocused = false;
            }
            if (!visible)
                return;

            var color = GUI.color;
            var background = GUI.backgroundColor;
            var content = GUI.contentColor;
            var enabled = GUI.enabled;
            try
            {
                GUI.color = GUI.backgroundColor = GUI.contentColor = Color.white;
                GUI.enabled = true;
                styles.Ensure();
                SyncWindowRect();
                guiRect = windowRect;
                RepairWindowInputGuard.Sample();
                var before = windowRect.position;
                windowRect = GUI.Window(WindowId, windowRect, DrawWindow, GUIContent.none, styles.WindowFrame);
                var box = RepairWindowGeometry.ClampToScreen(
                    new RepairWindowBox(windowRect.x, windowRect.y, windowRect.width, windowRect.height),
                    Screen.width, Screen.height);
                windowRect = new Rect(box.X, box.Y, box.Width, box.Height);
                movedSinceSync |= before != windowRect.position;
                guiRect = visible ? windowRect : new Rect();
                if (Event.current.rawType == EventType.MouseUp)
                    FinishMove();
                if (IsMouseOverWindow() && (Event.current.isMouse || Event.current.type == EventType.ScrollWheel))
                    Event.current.Use();
            }
            finally
            {
                GUI.color = color;
                GUI.backgroundColor = background;
                GUI.contentColor = content;
                GUI.enabled = enabled;
            }
        }

        private void SyncWindowRect()
        {
            if (screenWidth == Screen.width && screenHeight == Screen.height)
                return;
            screenWidth = Screen.width;
            screenHeight = Screen.height;
            var box = RepairWindowGeometry.FromNormalized(normalizedPosition.x, normalizedPosition.y, screenWidth, screenHeight);
            windowRect = new Rect(box.X, box.Y, box.Width, box.Height);
            movedSinceSync = false;
        }

        private void FinishMove()
        {
            if (!movedSinceSync)
                return;
            RepairWindowGeometry.Normalize(
                new RepairWindowBox(windowRect.x, windowRect.y, windowRect.width, windowRect.height),
                screenWidth, screenHeight, out var x, out var y);
            normalizedPosition = new Vector2(x, y);
            movedSinceSync = false;
        }

        private void DrawWindow(int id)
        {
            GUI.color = GUI.backgroundColor = GUI.contentColor = Color.white;
            var width = windowRect.width;
            var height = windowRect.height;
            RepairWindowStyles.Band(new Rect(0, 0, width, height), RepairWindowStyles.ContentFill);
            RepairWindowStyles.Band(new Rect(0, 0, width, TitleHeight), RepairWindowStyles.TitleFill);
            GUI.Label(new Rect(Padding, 8f, width - 110f, 24f), "EC SceneAudit", styles.Header);
            if (Button(new Rect(width - 84f, 7f, 72f, 28f), "隐藏"))
                Close();

            var tabWidth = (width - Padding * 2f - 8f) / 2f;
            if (Button(new Rect(Padding, 48f, tabWidth, 30f), nodeTab ? "文本修复" : "文本修复  [当前]"))
                SelectTab(false);
            if (Button(new Rect(Padding + tabWidth + 8f, 48f, tabWidth, 30f), nodeTab ? "节点补丁  [当前]" : "节点补丁"))
                SelectTab(true);

            GUILayout.BeginArea(new Rect(Padding, 88f, width - Padding * 2f, height - 88f - FooterHeight - 8f));
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawPage();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            DrawFooter(width, height);
            GUI.DragWindow(new Rect(0f, 0f, width - 90f, TitleHeight));
        }

        private void SelectTab(bool selected)
        {
            if (nodeTab == selected)
                return;
            nodeTab = selected;
            scroll = pathScroll = Vector2.zero;
        }

        private void DrawPage()
        {
            GUILayout.Label(nodeTab ? "节点补丁文件" : "修复清单文件", styles.Label);
            var path = nodeTab ? nodePatchPath : planPath;
            var next = PathField(path, nodeTab ? "EC_SceneAudit_Node_Patch" : "EC_SceneAudit_Repair_Plan");
            if (nodeTab) nodePatchPath = next;
            else planPath = next;
            inputFocused = GUI.GetNameOfFocusedControl() == (nodeTab
                ? "EC_SceneAudit_Node_Patch" : "EC_SceneAudit_Repair_Plan");
            GUILayout.Space(10f);
            GUILayout.Label("只改内存；需要保存时使用游戏的保存功能。", styles.Label);
            GUILayout.Space(10f);
            var message = nodeTab ? nodeStatus : repairStatus;
            var previous = GUI.contentColor;
            GUI.contentColor = (nodeTab ? nodeError : repairError) ? new Color(1f, 0.55f, 0.5f) : Color.white;
            if (!string.IsNullOrEmpty(message))
                GUILayout.Label(message, styles.Status);
            GUI.contentColor = previous;
            var snapshot = nodeTab ? nodePatchReport?.Snapshot?.Describe() : applyReport?.Snapshot?.Describe();
            if (snapshot != null)
                GUILayout.Label("内存快照：" + snapshot, styles.Label);
            if (!nodeTab && applyReport != null)
            {
                foreach (var item in applyReport.Items)
                    GUILayout.Label((item.Written ? "已应用  " : "未应用  ") + item.Label, styles.Label);
            }
        }

        private string PathField(string value, string controlName)
        {
            var height = Mathf.Max(1f, styles.TextArea.lineHeight) * RepairWindowGeometry.PathFieldLineCount
                + styles.TextArea.padding.vertical + styles.TextArea.border.vertical;
            var rect = GUILayoutUtility.GetRect(GUIContent.none, styles.TextArea,
                GUILayout.ExpandWidth(true), GUILayout.Height(height));
            var text = RepairPathText.Sanitize(value);
            var contentWidth = Mathf.Max(1f, rect.width - 16f);
            var contentHeight = Mathf.Max(height, styles.TextArea.CalcHeight(new GUIContent(text), contentWidth) + 2f);
            pathScroll = GUI.BeginScrollView(rect, pathScroll, new Rect(0, 0, contentWidth, contentHeight),
                false, true, GUIStyle.none, GUI.skin.verticalScrollbar);
            GUI.SetNextControlName(controlName);
            var next = GUI.TextArea(new Rect(0, 0, contentWidth, contentHeight), text, styles.TextArea);
            GUI.EndScrollView();
            return RepairPathText.Sanitize(next);
        }

        private void DrawFooter(float width, float height)
        {
            var y = height - FooterHeight;
            RepairWindowStyles.Band(new Rect(0, y, width, FooterHeight), RepairWindowStyles.TitleFill);
            var buttonWidth = (width - Padding * 2f - 8f) / 2f;
            GUI.enabled = !string.IsNullOrWhiteSpace(nodeTab ? nodePatchPath : planPath);
            if (Button(new Rect(Padding, y + 12f, buttonWidth, 32f), nodeTab ? "应用节点补丁" : "应用修复"))
                Execute(false);
            GUI.enabled = nodeTab ? nodePatchReport?.HasSnapshot == true : applyReport?.HasSnapshot == true;
            if (Button(new Rect(Padding + buttonWidth + 8f, y + 12f, buttonWidth, 32f), "恢复内存快照"))
                Execute(true);
            GUI.enabled = true;
        }

        private bool Button(Rect rect, string label)
        {
            if (!GUI.Button(rect, label, styles.Button))
                return false;
            unfocusRequested = true;
            inputFocused = false;
            return true;
        }

        private void Execute(bool restore)
        {
            if (!RepairEditorContext.IsNodeOverviewActive())
            {
                SetStatus("请回到 HEdit 节点总览。", true);
                return;
            }
            try
            {
                if (nodeTab) ExecuteNodePatch(restore);
                else ExecuteRepair(restore);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, true);
                log.LogError("SceneAudit " + (restore ? "restore" : "apply") + ": " + ex);
            }
        }

        private void ExecuteRepair(bool restore)
        {
            var memory = new RepairGameMemoryGateway(HEditData.Instance, HEditGlobal.Instance.nodeControl);
            if (restore)
            {
                var restored = applyEngine.Restore(applyReport, memory);
                SetStatus(restored ? "恢复完成，快照仍可再次使用。" : applyReport.FirstIssue, !restored);
            }
            else
            {
                savedPlanPath.Value = planPath.Trim();
                var result = applyEngine.Apply(planPath.Trim(), new RepairGameTargetSink(HEditData.Instance), memory);
                if (result.HasSnapshot)
                    applyReport = result;
                SetStatus(result.AppliedCount == 0 && result.Issues.Count == 0 ? "没有匹配的文本项。" : result.StageText
                    + (result.Issues.Count > 0 ? "；" + result.FirstIssue : string.Empty), result.Issues.Count > 0);
            }
            log.LogMessage("Repair " + (restore ? "restore: " : "apply: ") + repairStatus);
        }

        private void ExecuteNodePatch(bool restore)
        {
            var memory = new NodePatchGameMemoryHost(HEditData.Instance, HEditGlobal.Instance.nodeControl);
            if (restore)
            {
                var restored = nodePatchEngine.Restore(nodePatchReport, memory);
                SetStatus(restored ? "恢复完成，快照仍可再次使用。" : nodePatchReport.FirstIssue, !restored);
            }
            else
            {
                savedNodePatchPath.Value = nodePatchPath.Trim();
                var result = nodePatchEngine.Apply(nodePatchPath.Trim(), memory);
                if (result.HasSnapshot)
                    nodePatchReport = result;
                SetStatus(result.ConnectionCount + result.PlacementCount == 0 && result.Issues.Count == 0
                    ? "没有匹配的节点项。" : result.StageText
                    + (result.Issues.Count > 0 ? "；" + result.FirstIssue : string.Empty), result.Issues.Count > 0);
            }
            log.LogMessage("Node patch " + (restore ? "restore: " : "apply: ") + nodeStatus);
        }

        private void SetStatus(string message, bool error)
        {
            if (nodeTab) { nodeStatus = message; nodeError = error; }
            else { repairStatus = message; repairError = error; }
        }
    }
}
