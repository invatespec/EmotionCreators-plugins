using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EC_SceneAudit.Export;
using HEdit;
using UnityEngine;

namespace EC_SceneAudit.Repair
{
    // 只读预检窗口：填写清单与资料包路径，读取预检、查看逐项结论、导出文字报告。
    // 窗口只负责路径、调用与显示；核验规则在 RepairPlanReader / RepairBundleReader / RepairPreflight。
    internal sealed class RepairPreflightWindow
    {
        private const int WindowId = 0x53415046;
        private const float LabelWidth = 86f;
        private const float TitleHeight = 26f;
        private const float ContentPadding = 8f;

        private static readonly Color ErrorColor = new Color(1f, 0.45f, 0.4f);

        internal static RepairPreflightWindow Instance;

        private readonly ManualLogSource log;
        private readonly ConfigEntry<string> savedPlanPath;
        private readonly ConfigEntry<string> savedBundlePath;
        private readonly ConfigEntry<string> savedNodePatchPath;

        private string planPath = string.Empty;
        private string bundlePath = string.Empty;
        private string nodePatchPath = string.Empty;
        private RepairPreflightReport report;
        private string status = string.Empty;
        private bool statusIsError;
        private string exportedReportPath = string.Empty;
        private Vector2 scroll;
        private Vector2 planPathScroll;
        private Vector2 bundlePathScroll;
        private Vector2 nodePatchPathScroll;
        private readonly RepairWindowStyles styles = new RepairWindowStyles();
        private GUIStyle wrapLabel;

        private Rect guiRect;
        // 窗口位置用归一化保存：拖动后按屏幕夹紧，分辨率变化时仍落在合理位置，关闭再打开沿用。
        private Rect windowRect;
        private Vector2 normalizedPosition = new Vector2(0.5f, 0.35f);
        private bool positionInvalid = true;
        private int screenWidth;
        private int screenHeight;
        private bool movedSinceSync;
        private bool visible;
        private bool inputFocused;
        private readonly RepairApplyEngine applyEngine = new RepairApplyEngine();
        private readonly NodePatchEngine nodePatchEngine = new NodePatchEngine();
        private RepairApplyReport applyReport;
        private RepairVerificationReport verification;
        private NodePatchPreflightReport nodePatchPreflight;
        private NodePatchApplyReport nodePatchReport;
        private NodePatchVerificationReport nodePatchVerification;
        private string applyStatus = string.Empty;
        private bool applyStatusIsError;
        private string nodePatchStatus = string.Empty;
        private bool nodePatchStatusIsError;
        private bool nodePatchExpandConfirmed;
        private bool busy;

        // 会话仍持有有效快照时必须保留窗口；短暂离开总览不丢快照，恢复入口也不能消失。
        internal bool KeepsWindowOpen =>
            (applyReport != null && applyReport.HasSession)
            || (nodePatchReport != null && nodePatchReport.HasSession);
        public bool Visible => visible;
        internal static bool HasInputFocus => Instance != null && Instance.visible && Instance.inputFocused;

        public RepairPreflightWindow(
            ManualLogSource log,
            ConfigEntry<string> savedPlanPath,
            ConfigEntry<string> savedBundlePath,
            ConfigEntry<string> savedNodePatchPath)
        {
            this.log = log;
            this.savedPlanPath = savedPlanPath;
            this.savedBundlePath = savedBundlePath;
            this.savedNodePatchPath = savedNodePatchPath;
            planPath = RepairPathText.Sanitize(savedPlanPath.Value);
            bundlePath = RepairPathText.Sanitize(savedBundlePath.Value);
            nodePatchPath = RepairPathText.Sanitize(savedNodePatchPath.Value);
        }

        public void Open()
        {
            ClearResult();
            // 重新打开必须重新确认扩展；上一轮未应用的确认不能跨窗口生命周期复用。
            nodePatchExpandConfirmed = false;
            visible = true;
        }

        public void Close()
        {
            // 关窗时结算一次位置，下次打开沿用最近位置。
            FinishMove();
            visible = false;
            inputFocused = false;
            RepairWindowInputGuard.Reset();
            ClearResult();
            // 关窗即离开当前节点总览，预检与扩展确认都绑定当时的路径/内容/世代，重开必须重做。
            nodePatchPreflight = null;
            nodePatchExpandConfirmed = false;
        }

        // 插件卸载时释放自建贴图；样式为克隆对象，置空即可，不触碰共享 GUI.skin。
        internal void Dispose()
        {
            RepairWindowInputGuard.Reset();
            styles.Dispose();
            wrapLabel = null;
        }

        // 供 Harmony 钩子判断鼠标是否落在窗口上，避免点击穿透到节点画布。
        internal static bool IsMouseOverWindow()
        {
            var instance = Instance;
            if (instance == null || !instance.visible)
                return false;
            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return instance.guiRect.Contains(mouse);
        }

        public void OnGUI()
        {
            if (!visible)
            {
                guiRect = new Rect(0f, 0f, 0f, 0f);
                // 窗口不可见时不能保留手势归属，否则关闭期间的按键状态会带到下次打开。
                RepairWindowInputGuard.Reset();
                return;
            }

            var background = GUI.backgroundColor;
            var content = GUI.contentColor;
            var color = GUI.color;
            var enabled = GUI.enabled;
            try
            {
                GUI.backgroundColor = GUI.contentColor = GUI.color = Color.white;
                GUI.enabled = true;
                EnsureStyles();
                SyncWindowRect();
                // 先用本帧位置填命中矩形，再采样按键归属，避免首帧 guiRect=0 时漏判窗口内按下。
                guiRect = windowRect;
                RepairWindowInputGuard.Sample();
                var before = windowRect.position;
                windowRect = GUI.Window(WindowId, windowRect, DrawWindow, GUIContent.none, styles.WindowFrame);
                var clamped = RepairWindowGeometry.ClampToScreen(
                    new RepairWindowBox(windowRect.x, windowRect.y, windowRect.width, windowRect.height),
                    Screen.width, Screen.height);
                windowRect = new Rect(clamped.X, clamped.Y, clamped.Width, clamped.Height);
                if (before != windowRect.position)
                    movedSinceSync = true;
                // 命中判定必须用 GUI.Window 返回的实际矩形，标题栏拖动后也要跟着走。
                guiRect = windowRect;
                if (Event.current != null && Event.current.rawType == EventType.MouseUp)
                    FinishMove();
                ConsumeMouseEvent();
            }
            finally
            {
                GUI.backgroundColor = background;
                GUI.contentColor = content;
                GUI.color = color;
                GUI.enabled = enabled;
            }
        }

        private void ConsumeMouseEvent()
        {
            if (!IsMouseOverWindow())
                return;
            // 只消费 IMGUI 事件，不重置全局 Input：重置会清掉按住状态，使游戏读不到松开边沿。
            var current = Event.current;
            if (current != null && (current.isMouse || current.type == EventType.ScrollWheel))
                current.Use();
        }

        private void EnsureStyles()
        {
            var firstInitialization = wrapLabel == null;
            styles.Ensure();
            wrapLabel = styles.Label;
            if (firstInitialization)
                log.LogInfo("Repair window font sizes: skinLabel=" + GUI.skin.label.fontSize
                    + ", label=" + styles.Label.fontSize + ", header=" + styles.Header.fontSize
                    + ", status=" + styles.Status.fontSize + ", button=" + styles.Button.fontSize
                    + ", textField=" + styles.TextField.fontSize);
        }

        // 只在窗口位置失效或分辨率变化时重算；拖动只改 windowRect，不改归一化位置（松开时结算）。
        private void SyncWindowRect()
        {
            if (!positionInvalid && screenWidth == Screen.width && screenHeight == Screen.height)
                return;
            screenWidth = Screen.width;
            screenHeight = Screen.height;
            var box = RepairWindowGeometry.FromNormalized(
                normalizedPosition.x, normalizedPosition.y, screenWidth, screenHeight);
            windowRect = new Rect(box.X, box.Y, box.Width, box.Height);
            positionInvalid = false;
            movedSinceSync = false;
        }

        // 拖动结束后把当前位置折算成归一化坐标，供关窗后重开和分辨率变化复用。
        private void FinishMove()
        {
            if (!movedSinceSync)
                return;
            float normalizedX, normalizedY;
            RepairWindowGeometry.Normalize(
                new RepairWindowBox(windowRect.x, windowRect.y, windowRect.width, windowRect.height),
                screenWidth, screenHeight, out normalizedX, out normalizedY);
            normalizedPosition = new Vector2(normalizedX, normalizedY);
            movedSinceSync = false;
        }

        private void DrawWindow(int id)
        {
            // 回调有独立绘制状态，这里明确设置；分层背景先画，控件后画。
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
            var width = windowRect.width;
            var height = windowRect.height;
            RepairWindowStyles.Band(new Rect(0f, 0f, width, height), RepairWindowStyles.WindowFill);
            RepairWindowStyles.Band(new Rect(1f, 1f, width - 2f, height - 2f), RepairWindowStyles.ContentFill);
            RepairWindowStyles.Band(new Rect(1f, 1f, width - 2f, TitleHeight), RepairWindowStyles.TitleFill);
            GUI.Label(new Rect(ContentPadding, 2f, width - ContentPadding * 2f, TitleHeight - 4f),
                "EC SceneAudit 修复预检与应用", styles.Header);

            var body = new Rect(ContentPadding, TitleHeight + ContentPadding,
                width - ContentPadding * 2f, height - TitleHeight - ContentPadding * 2f);
            GUILayout.BeginArea(body);
            // 标题栏保持固定；body 内所有内容共用一个受区域高度约束的纵向滚动区。
            scroll = GUILayout.BeginScrollView(scroll, styles.Box,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            PlanField();
            BundleField();
            NodePatchField();
            var focused = GUI.GetNameOfFocusedControl();
            inputFocused = focused == "EC_SceneAudit_Repair_Plan"
                || focused == "EC_SceneAudit_Repair_Bundle"
                || focused == "EC_SceneAudit_Node_Patch";

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("读取预检", styles.Button, GUILayout.Width(96f)))
                RunPreflight();
            GUI.enabled = report != null;
            if (GUILayout.Button("导出文字报告", styles.Button, GUILayout.Width(120f)))
                ExportReport();
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("关闭", styles.Button, GUILayout.Width(72f)))
                Close();
            GUILayout.EndHorizontal();

            GUILayout.Label("应用只改内存：不写文件、不做全量保存。点「应用修复」前请先在游戏内手动保存当前 Scene。", wrapLabel);

            GUILayout.BeginHorizontal();
            GUI.enabled = !busy && report != null && report.Passed;
            if (GUILayout.Button("应用修复（仅内存）", styles.Button, GUILayout.Width(180f)))
                ApplyRepair();
            GUI.enabled = !busy && applyReport != null
                && (applyReport.Phase == RepairApplyPhase.AppliedUnsaved || applyReport.Phase == RepairApplyPhase.Verified
                    || applyReport.Phase == RepairApplyPhase.VerifyFailed);
            if (GUILayout.Button("读取 F8 回验", styles.Button, GUILayout.Width(120f)))
                ReadVerification();
            // 恢复成功后会话已结束（HasSession=false），快照只作展示，不能再次回写覆盖作者后续编辑。
            GUI.enabled = !busy && applyReport != null && applyReport.HasSession;
            if (GUILayout.Button("恢复内存快照", styles.Button, GUILayout.Width(120f)))
                RestoreFromSnapshot();
            GUI.enabled = !busy && applyReport != null;
            if (GUILayout.Button("清除本次记录", styles.Button, GUILayout.Width(110f)))
                ClearApplySession();
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            DrawNodePatchControls();

            if (status.Length > 0)
            {
                var previous = GUI.contentColor;
                GUI.contentColor = statusIsError ? ErrorColor : Color.white;
                GUILayout.Label(status, styles.Status);
                GUI.contentColor = previous;
            }
            if (exportedReportPath.Length > 0)
                GUILayout.Label("报告：" + exportedReportPath, wrapLabel);

            DrawApply();
            DrawNodePatchStatus();
            DrawResult();
            GUILayout.Space(8f);
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            // 只有标题栏是拖动区；关闭按钮、输入框和按钮都在内容区，不受影响。
            GUI.DragWindow(new Rect(0f, 0f, width, TitleHeight));
        }

        private void PlanField()
        {
            var next = PathField("修复清单", "EC_SceneAudit_Repair_Plan", planPath, ref planPathScroll);
            SetPlanPath(next);
        }

        private void BundleField()
        {
            var next = PathField("F8 资料包", "EC_SceneAudit_Repair_Bundle", bundlePath, ref bundlePathScroll);
            SetBundlePath(next);
        }

        private void NodePatchField()
        {
            var next = PathField("节点补丁", "EC_SceneAudit_Node_Patch", nodePatchPath, ref nodePatchPathScroll);
            SetNodePatchPath(next);
        }

        // 两行视口来自文字行高与当前样式 padding/border；超过时滚动查看，存值保持单行。
        private string PathField(string label, string controlName, string value, ref Vector2 pathScroll)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            var viewportHeight = Mathf.Max(1f, styles.TextArea.lineHeight) * RepairWindowGeometry.PathFieldLineCount
                + styles.TextArea.padding.vertical + styles.TextArea.border.vertical;
            var rect = GUILayoutUtility.GetRect(GUIContent.none, styles.TextArea,
                GUILayout.ExpandWidth(true), GUILayout.Height(viewportHeight));
            var text = RepairPathText.Sanitize(value);
            var contentWidth = Mathf.Max(1f, rect.width - 16f);
            var contentHeight = Mathf.Max(viewportHeight,
                styles.TextArea.CalcHeight(new GUIContent(text), contentWidth) + 2f);
            pathScroll = GUI.BeginScrollView(rect, pathScroll,
                new Rect(0f, 0f, contentWidth, contentHeight), false, true,
                GUIStyle.none, GUI.skin.verticalScrollbar);
            GUI.SetNextControlName(controlName);
            var next = GUI.TextArea(new Rect(0f, 0f, contentWidth, contentHeight), text, styles.TextArea);
            GUI.EndScrollView();
            GUILayout.EndHorizontal();
            return RepairPathText.Sanitize(next);
        }

        // 路径变化立即清除旧结果，失败时不能残留上一轮成功。
        private void SetPlanPath(string value)
        {
            value = RepairPathText.Sanitize(value);
            if (string.Equals(value, planPath, StringComparison.Ordinal))
                return;
            planPath = value ?? string.Empty;
            ClearResult();
        }

        private void SetBundlePath(string value)
        {
            value = RepairPathText.Sanitize(value);
            if (string.Equals(value, bundlePath, StringComparison.Ordinal))
                return;
            bundlePath = value ?? string.Empty;
            ClearResult();
            // 节点补丁预检同时依赖补丁路径与资料包路径；换包必须让旧预检与扩展确认失效。
            nodePatchPreflight = null;
            nodePatchExpandConfirmed = false;
        }

        private void SetNodePatchPath(string value)
        {
            value = RepairPathText.Sanitize(value);
            if (string.Equals(value, nodePatchPath, StringComparison.Ordinal))
                return;
            nodePatchPath = value ?? string.Empty;
            nodePatchPreflight = null;
            nodePatchStatus = string.Empty;
            nodePatchStatusIsError = false;
            nodePatchExpandConfirmed = false;
        }

        private void ClearResult()
        {
            // 只清理预检结果；应用会话与内存快照恢复入口必须保留到作者明确清除。
            report = null;
            status = string.Empty;
            statusIsError = false;
            exportedReportPath = string.Empty;
            scroll = Vector2.zero;
        }

        private void RunPreflight()
        {
            ClearResult();
            if (!RepairPreflightContext.IsNodeOverviewActive())
            {
                Fail("当前不在 HEdit 节点总览，无法读取可信的当前 Scene；请回到节点总览后重试。");
                return;
            }
            if (string.IsNullOrWhiteSpace(planPath) || string.IsNullOrWhiteSpace(bundlePath))
            {
                Fail("请先填写修复清单和 F8 资料包路径。");
                return;
            }

            try
            {
                savedPlanPath.Value = planPath.Trim();
                savedBundlePath.Value = bundlePath.Trim();
                var result = new RepairPreflight().Run(planPath.Trim(), bundlePath.Trim(), BuildCurrentSnapshot);
                report = result;
                status = result.Passed
                    ? "预检通过：" + result.Verdict + "。未修改任何数据。"
                    : "预检未通过：" + result.Verdict + "。未修改任何数据。";
                statusIsError = !result.Passed;
                // 写回后内存文本已是新稿，用旧清单再预检必然冲突，这里说明原因避免误判为失败。
                if (!result.Passed && applyReport != null && applyReport.HasSession)
                    status += "（本次会话已写回清单新文，此处对照的是清单原文，出现冲突属正常。）";
                log.LogMessage("Repair preflight: " + result.Verdict + " (" + result.ElapsedMilliseconds + " ms, " + planPath + ")");
            }
            catch (Exception ex)
            {
                Fail("预检失败：" + ex.Message);
                log.LogError("Repair preflight failed: " + ex);
            }
        }

        private static SceneSnapshot BuildCurrentSnapshot()
        {
            if (!RepairPreflightContext.IsNodeOverviewActive())
                throw new RepairInputException("已离开节点总览，请返回后重新预检。");
            var builder = new SceneSnapshotBuilder(Path.Combine(Paths.GameRootPath, "UserData"));
            return builder.Build(out _, out _, out _, out _);
        }

        // 应用：重新读取清单与资料包并重新核验当前 Scene，通过后做受影响内存快照，再整批写入目标文本。
        private void ApplyRepair()
        {
            if (busy)
                return;
            if (report == null || !report.Passed)
            {
                FailApply("请先通过只读预检，再应用修复。");
                return;
            }
            if (!RepairPreflightContext.IsNodeOverviewActive())
            {
                FailApply("当前不在 HEdit 节点总览，禁止写回。");
                return;
            }
            if (applyReport != null && applyReport.HasSession)
            {
                FailApply("上一次应用尚未恢复或清除，不能再次应用；请先恢复内存快照或清除本次记录。");
                return;
            }
            if (nodePatchReport != null && nodePatchReport.HasSession)
            {
                FailApply("当前存在未处理的节点补丁会话，文本应用与节点写回不能并行。");
                return;
            }

            // Scene 身份由内存网关按实际对象校验，这里只取一段显示文本。
            string sceneDisplay;
            TryGetCurrentScenePath(out sceneDisplay);

            busy = true;
            try
            {
                savedPlanPath.Value = planPath.Trim();
                savedBundlePath.Value = bundlePath.Trim();
                var result = applyEngine.Apply(
                    planPath.Trim(), bundlePath.Trim(), sceneDisplay, BuildCurrentSnapshot, BuildSink,
                    BuildMemoryGateway(), applyReport);
                // 只有真正写入内存的新会话才替换旧会话；失败的新应用不能丢已有有效快照。
                var adopted = result.Phase == RepairApplyPhase.AppliedUnsaved;
                if (adopted)
                {
                    applyReport = result;
                    verification = null;
                }
                applyStatus = result.StageText + (result.Issues.Count > 0 ? "：" + result.FirstIssue : string.Empty);
                applyStatusIsError = result.Phase != RepairApplyPhase.AppliedUnsaved
                    && result.Phase != RepairApplyPhase.Restored;
                log.LogMessage("Repair apply: " + result.StageText + " (phase=" + result.Phase
                    + ", items=" + result.Items.Count + ", snapshotParts=" + result.SnapshotPartCount
                    + ", snapshotConnections=" + result.SnapshotConnectionCount
                    + ", adopted=" + adopted + ")");
                // 写回或失败都会让旧预检结论失效，要求重新预检后才能再次应用。
                ClearResult();
            }
            catch (Exception ex)
            {
                FailApply("应用失败：" + ex.Message);
                log.LogError("Repair apply failed: " + ex);
            }
            finally
            {
                busy = false;
            }
        }

        private void DrawNodePatchControls()
        {
            GUILayout.Label("节点补丁与布局（仅内存）", styles.Header);
            GUILayout.BeginHorizontal();
            GUI.enabled = !busy && nodePatchPath.Length > 0 && bundlePath.Length > 0;
            if (GUILayout.Button("读取节点预检", styles.Button, GUILayout.Width(110f)))
                RunNodePatchPreflight();
            GUI.enabled = !busy && nodePatchPreflight != null && nodePatchPreflight.Passed
                && (applyReport == null || !applyReport.HasSession)
                && (nodePatchReport == null || !nodePatchReport.HasSession);
            if (GUILayout.Button("应用节点补丁（仅内存）", styles.Button, GUILayout.Width(170f)))
                ApplyNodePatch();
            GUI.enabled = !busy && nodePatchReport != null
                && (nodePatchReport.Phase == NodePatchApplyPhase.AppliedUnsaved
                    || nodePatchReport.Phase == NodePatchApplyPhase.Verified
                    || nodePatchReport.Phase == NodePatchApplyPhase.VerifyFailed);
            if (GUILayout.Button("读取节点 F8 回验", styles.Button, GUILayout.Width(140f)))
                ReadNodePatchVerification();
            GUI.enabled = !busy && nodePatchReport != null && nodePatchReport.HasSession;
            if (GUILayout.Button("恢复节点快照", styles.Button, GUILayout.Width(110f)))
                RestoreNodePatch();
            GUI.enabled = !busy && nodePatchReport != null
                && nodePatchReport.Phase != NodePatchApplyPhase.FailedRollbackHeld;
            if (GUILayout.Button("清除节点记录", styles.Button, GUILayout.Width(100f)))
                ClearNodePatchSession();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void DrawNodePatchStatus()
        {
            if (nodePatchPreflight == null && nodePatchReport == null && nodePatchStatus.Length == 0)
                return;
            GUILayout.Label("节点补丁状态", styles.Header);
            var previous = GUI.contentColor;
            GUI.contentColor = nodePatchStatusIsError ? ErrorColor : Color.white;
            if (nodePatchStatus.Length > 0)
                GUILayout.Label("最近操作：" + nodePatchStatus, wrapLabel);
            GUI.contentColor = previous;
            if (nodePatchPreflight != null)
            {
                GUILayout.Label("预检：" + nodePatchPreflight.Verdict
                    + "   连接 " + nodePatchPreflight.ConnectionCount
                    + "   布局节点 " + nodePatchPreflight.LayoutCount, wrapLabel);
                if (nodePatchPreflight.LayoutCount > 0)
                {
                    GUILayout.Label("画布：当前 " + nodePatchPreflight.CurrentCanvasWidth.ToString("0.##") + "×"
                        + nodePatchPreflight.CurrentCanvasHeight.ToString("0.##")
                        + " / 需要 " + nodePatchPreflight.RequiredCanvasWidth.ToString("0.##") + "×"
                        + nodePatchPreflight.RequiredCanvasHeight.ToString("0.##")
                        + (nodePatchPreflight.RequiresCanvasExpansion ? "（需确认扩展）" : string.Empty), wrapLabel);
                }
                foreach (var issue in nodePatchPreflight.GlobalIssues)
                    GUILayout.Label("全局：" + issue, wrapLabel);
                foreach (var item in nodePatchPreflight.Items)
                {
                    if (item.Passed)
                        continue;
                    GUILayout.Label(item.Label + "：" + string.Join("；", item.Issues.ToArray()), wrapLabel);
                }
            }
            if (nodePatchReport != null)
            {
                GUILayout.Label("当前阶段：" + nodePatchReport.StageText
                    + "   连接 " + nodePatchReport.ConnectionCount
                    + "   位置 " + nodePatchReport.PlacementCount, wrapLabel);
                if (nodePatchReport.HasSnapshot)
                    GUILayout.Label("内存快照：" + nodePatchReport.Snapshot.Describe(), wrapLabel);
                foreach (var issue in nodePatchReport.Issues)
                    GUILayout.Label("问题：" + issue, wrapLabel);
            }
            if (nodePatchVerification != null)
                GUILayout.Label("节点回验：" + nodePatchVerification.Verdict, wrapLabel);
        }

        private void RunNodePatchPreflight()
        {
            nodePatchPreflight = null;
            nodePatchStatus = string.Empty;
            nodePatchStatusIsError = false;
            nodePatchExpandConfirmed = false;
            if (!RepairPreflightContext.IsNodeOverviewActive())
            {
                FailNodePatch("当前不在 HEdit 节点总览，无法读取可信的当前 Scene。");
                return;
            }
            if (string.IsNullOrWhiteSpace(nodePatchPath) || string.IsNullOrWhiteSpace(bundlePath))
            {
                FailNodePatch("请先填写节点补丁和 F8 资料包路径。");
                return;
            }
            try
            {
                savedNodePatchPath.Value = nodePatchPath.Trim();
                savedBundlePath.Value = bundlePath.Trim();
                var plan = NodePatchReader.ReadFile(nodePatchPath.Trim());
                var bundle = RepairBundleReader.Read(bundlePath.Trim());
                var memory = BuildNodePatchGateway();
                float width;
                float height;
                bool supportsResize;
                string canvasError;
                if (!memory.TryGetCanvas(out width, out height, out supportsResize, out canvasError))
                    throw new RepairInputException("读取当前画布失败：" + canvasError);
                nodePatchPreflight = new NodePatchPreflight().Evaluate(
                    plan, nodePatchPath.Trim(), bundlePath.Trim(), bundle, BuildCurrentSnapshot(), supportsResize);
                nodePatchStatus = nodePatchPreflight.Passed
                    ? "节点预检通过，未修改任何数据。"
                    : "节点预检未通过，未修改任何数据。";
                nodePatchStatusIsError = !nodePatchPreflight.Passed;
                log.LogMessage("Node patch preflight: " + nodePatchPreflight.Verdict);
            }
            catch (Exception ex)
            {
                FailNodePatch("节点预检失败：" + ex.Message);
                log.LogError("Node patch preflight failed: " + ex);
            }
        }

        private void ApplyNodePatch()
        {
            if (busy)
                return;
            if (nodePatchPreflight == null || !nodePatchPreflight.Passed)
            {
                FailNodePatch("请先通过节点补丁预检，再应用。");
                return;
            }
            if (applyReport != null && applyReport.HasSession)
            {
                FailNodePatch("当前存在未处理的文本修复会话，节点补丁不能并行应用。");
                return;
            }
            if (nodePatchReport != null && nodePatchReport.HasSession)
            {
                FailNodePatch("上一次节点补丁尚未恢复或清除，不能再次应用。");
                return;
            }
            if (!RepairPreflightContext.IsNodeOverviewActive())
            {
                FailNodePatch("当前不在 HEdit 节点总览，禁止写回。");
                return;
            }
            if (nodePatchPreflight.RequiresCanvasExpansion && !nodePatchExpandConfirmed)
            {
                nodePatchExpandConfirmed = true;
                nodePatchStatus = "目标画布大于当前画布。再次点击「应用节点补丁」确认扩展并应用。";
                nodePatchStatusIsError = false;
                return;
            }

            string sceneDisplay;
            TryGetCurrentScenePath(out sceneDisplay);
            busy = true;
            try
            {
                var result = nodePatchEngine.Apply(
                    nodePatchPath.Trim(), bundlePath.Trim(), sceneDisplay, BuildCurrentSnapshot,
                    BuildNodePatchGateway(), nodePatchReport);
                if (result.HasSession)
                {
                    nodePatchReport = result;
                    nodePatchVerification = null;
                }
                nodePatchStatus = result.StageText + (result.Issues.Count > 0 ? "：" + result.FirstIssue : string.Empty);
                nodePatchStatusIsError = result.Phase != NodePatchApplyPhase.AppliedUnsaved
                    && result.Phase != NodePatchApplyPhase.Restored;
                log.LogMessage("Node patch apply: " + result.StageText + " (phase=" + result.Phase
                    + ", connections=" + result.ConnectionCount + ", placements=" + result.PlacementCount + ")");
                nodePatchPreflight = result.Preflight ?? nodePatchPreflight;
                if (result.HasSession)
                    nodePatchExpandConfirmed = false;
            }
            catch (Exception ex)
            {
                FailNodePatch("节点补丁应用失败：" + ex.Message);
                log.LogError("Node patch apply failed: " + ex);
            }
            finally
            {
                busy = false;
            }
        }

        private void ReadNodePatchVerification()
        {
            if (busy || nodePatchReport == null || !nodePatchReport.HasSnapshot || !nodePatchReport.AppliedAtUtc.HasValue)
                return;
            busy = true;
            try
            {
                var root = Path.Combine(Paths.GameRootPath, "UserData", "PluginData", "SceneAudit");
                var newest = NodePatchEngine.FindNewestBundleDirectory(root, nodePatchReport.AppliedAtUtc.Value);
                if (string.IsNullOrEmpty(newest))
                {
                    FailNodePatch("找不到应用之后的新 F8 导出；请先按 F8 重新导出。");
                    return;
                }
                nodePatchVerification = nodePatchEngine.Verify(
                    nodePatchReport.PatchPath, nodePatchReport.BundlePath, newest);
                nodePatchReport.Phase = nodePatchVerification.Passed
                    ? NodePatchApplyPhase.Verified
                    : NodePatchApplyPhase.VerifyFailed;
                nodePatchReport.Verification = nodePatchVerification;
                nodePatchStatus = nodePatchVerification.Verdict;
                nodePatchStatusIsError = !nodePatchVerification.Passed;
                log.LogMessage("Node patch verify: " + nodePatchVerification.Verdict);
            }
            catch (Exception ex)
            {
                FailNodePatch("节点补丁回验失败：" + ex.Message);
                log.LogError("Node patch verify failed: " + ex);
            }
            finally
            {
                busy = false;
            }
        }

        private void RestoreNodePatch()
        {
            if (busy || nodePatchReport == null || !nodePatchReport.HasSession)
                return;
            if (!RepairPreflightContext.IsNodeOverviewActive())
            {
                FailNodePatch("请回到 HEdit 节点总览后再恢复节点补丁。");
                return;
            }
            busy = true;
            try
            {
                var restored = nodePatchEngine.Restore(nodePatchReport, BuildNodePatchGateway());
                nodePatchVerification = null;
                nodePatchStatus = restored
                    ? "已恢复节点位置与连接：" + nodePatchReport.Snapshot.Describe() + "；画布扩展尺寸保留。"
                    : "节点恢复失败：" + nodePatchReport.FirstIssue;
                nodePatchStatusIsError = !restored;
                log.LogMessage("Node patch restore: " + nodePatchStatus);
            }
            catch (Exception ex)
            {
                FailNodePatch("节点恢复失败：" + ex.Message);
                log.LogError("Node patch restore failed: " + ex);
            }
            finally
            {
                busy = false;
            }
        }

        private void ClearNodePatchSession()
        {
            if (nodePatchReport == null)
                return;
            if (nodePatchReport.Phase == NodePatchApplyPhase.FailedRollbackHeld)
            {
                FailNodePatch("补偿未全部完成，不能清除记录；必须先解决未回滚项。");
                return;
            }
            var snapshot = nodePatchReport.HasSnapshot ? nodePatchReport.Snapshot.Describe() : string.Empty;
            nodePatchReport = null;
            nodePatchVerification = null;
            nodePatchPreflight = null;
            nodePatchExpandConfirmed = false;
            nodePatchStatus = snapshot.Length > 0
                ? "已清除节点补丁记录（" + snapshot + "）"
                : "已清除节点补丁记录。";
            nodePatchStatusIsError = false;
        }

        private void FailNodePatch(string message)
        {
            nodePatchStatus = message;
            nodePatchStatusIsError = true;
        }

        private static INodePatchMemoryHost BuildNodePatchGateway()
        {
            if (!Singleton<HEditData>.IsInstance() || Singleton<HEditData>.Instance == null
                || !Singleton<HEditGlobal>.IsInstance() || Singleton<HEditGlobal>.Instance == null
                || Singleton<HEditGlobal>.Instance.nodeControl == null)
                throw new RepairInputException("HEditData 或 NodeControl 不可用，无法创建节点内存网关。");
            return new NodePatchGameMemoryHost(
                Singleton<HEditData>.Instance,
                Singleton<HEditGlobal>.Instance.nodeControl);
        }

        // 回验必须绑定本会话的 plan/bundle，并取应用之后的最新完整 F8 导出；失败只保留差异和恢复入口。
        private void ReadVerification()
        {
            if (busy)
                return;
            if (applyReport == null || !applyReport.HasSnapshot
                || (applyReport.Phase != RepairApplyPhase.AppliedUnsaved
                    && applyReport.Phase != RepairApplyPhase.Verified
                    && applyReport.Phase != RepairApplyPhase.VerifyFailed))
            {
                FailApply("只有内存应用成功后才能读取 F8 回验。");
                return;
            }
            if (!applyReport.AppliedAtUtc.HasValue)
            {
                FailApply("会话缺少应用时间，无法确认回验导出是否在应用之后。");
                return;
            }

            busy = true;
            try
            {
                var root = Path.Combine(Paths.GameRootPath, "UserData", "PluginData", "SceneAudit");
                // 严格晚于应用时刻，避免把应用前的导出当成回验证据。
                var notBefore = applyReport.AppliedAtUtc.Value;
                var newest = RepairApplyEngine.FindNewestBundleDirectory(root, notBefore);
                if (string.IsNullOrEmpty(newest))
                {
                    FailApply("找不到应用之后的新 F8 导出；请在游戏内按 F8 重新导出该 Scene 后再读取回验。");
                    return;
                }

                verification = applyEngine.Verify(applyReport.PlanPath, applyReport.BundlePath, newest);
                if (verification.Passed)
                {
                    applyReport.Phase = RepairApplyPhase.Verified;
                    applyReport.Steps.Add("F8 回验通过：" + newest);
                }
                else
                {
                    applyReport.Phase = RepairApplyPhase.VerifyFailed;
                    applyReport.Steps.Add("F8 回验未通过：" + newest);
                }
                applyStatus = verification.Verdict;
                applyStatusIsError = !verification.Passed;
                log.LogMessage("Repair verify: " + verification.Verdict + " (new bundle=" + newest + ")");
            }
            catch (Exception ex)
            {
                FailApply("回验失败：" + ex.Message);
                log.LogError("Repair verify failed: " + ex);
            }
            finally
            {
                busy = false;
            }
        }

        // 恢复只在节点总览执行：替换受影响内存 Part，并刷新相关节点 UI。
        private void RestoreFromSnapshot()
        {
            if (busy)
                return;
            if (applyReport == null || !applyReport.HasSnapshot)
            {
                FailApply("没有可用的内存快照。");
                return;
            }
            if (!RepairPreflightContext.IsNodeOverviewActive())
            {
                FailApply("请回到 HEdit 节点总览后再执行恢复，避免在其它编辑界面替换 Part。");
                return;
            }

            busy = true;
            try
            {
                var restored = applyEngine.Restore(applyReport, BuildMemoryGateway());
                verification = null;
                applyStatus = restored
                    ? "已从内存快照恢复：" + applyReport.Snapshot.Describe()
                        + "；需要落盘请自行保存或重新加载原始 Scene。"
                    : "恢复失败：" + applyReport.FirstIssue;
                applyStatusIsError = !restored;
                log.LogMessage("Repair restore: " + applyStatus);
                if (restored)
                    ClearResult();
            }
            catch (Exception ex)
            {
                FailApply("恢复失败：" + ex.Message);
                log.LogError("Repair restore failed: " + ex);
            }
            finally
            {
                busy = false;
            }
        }

        // 清除只丢弃内存快照引用；已写入内存的文本改动保留，需要时用户自行保存或重新加载原始 Scene。
        private void ClearApplySession()
        {
            if (applyReport == null)
                return;
            var snapshotText = applyReport.HasSnapshot
                ? applyReport.Snapshot.Describe()
                : string.Empty;
            applyReport = null;
            verification = null;
            applyStatus = snapshotText.Length > 0
                ? "已清除本次应用记录；内存快照已释放（" + snapshotText + "）"
                : "已清除本次应用记录。";
            applyStatusIsError = false;
        }

        private void DrawApply()
        {
            if (applyReport == null && applyStatus.Length == 0)
                return;

            GUILayout.Label("修复状态", styles.Header);
            if (applyStatus.Length > 0)
            {
                var previous = GUI.contentColor;
                GUI.contentColor = applyStatusIsError ? ErrorColor : Color.white;
                GUILayout.Label("最近操作：" + applyStatus, styles.Status);
                GUI.contentColor = previous;
            }
            if (applyReport == null)
                return;

            GUILayout.Label("当前阶段：" + applyReport.StageText + "   目标：" + applyReport.Items.Count + " 项"
                + "   耗时：" + applyReport.ElapsedMilliseconds + " ms", styles.Status);
            GUILayout.Label("Scene：" + Display(applyReport.SceneDisplay), wrapLabel);
            GUILayout.Label("内存快照：" + (applyReport.HasSnapshot
                ? applyReport.Snapshot.Describe()
                : "(无)"), wrapLabel);
            if (applyReport.Phase == RepairApplyPhase.AppliedUnsaved)
                GUILayout.Label("当前结果尚未保存：可恢复内存快照，或在游戏内手动保存 / 重新加载原始 Scene。", wrapLabel);
            if (!RepairPreflightContext.IsNodeOverviewActive())
                GUILayout.Label("当前不在节点总览：已禁止新的写回与应用，内存快照恢复入口保留。", wrapLabel);

            foreach (var step in applyReport.Steps)
                GUILayout.Label("阶段记录：" + step, wrapLabel);
            foreach (var issue in applyReport.Issues)
                GUILayout.Label("问题：" + issue, wrapLabel);

            if (verification == null)
                return;
            GUILayout.Label("F8 回验：" + verification.Verdict + "   新导出：" + verification.NewBundlePath, wrapLabel);
            foreach (var issue in verification.GlobalIssues)
                GUILayout.Label("   回验问题：" + issue, wrapLabel);
            foreach (var item in verification.Items)
            {
                if (item.Passed)
                    continue;
                GUILayout.Label("   " + item.Label + " —— " + string.Join("；", item.Issues.ToArray()), wrapLabel);
            }
        }

        // 仅用于界面显示；Scene 身份由内存网关按实际对象校验，取不到路径不影响应用。
        private static bool TryGetCurrentScenePath(out string display)
        {
            display = string.Empty;
            try
            {
                var scene = UnityEngine.Object.FindObjectOfType<HEditScene>();
                if (scene != null && !string.IsNullOrWhiteSpace(scene.fullPath))
                {
                    display = scene.fullPath;
                    return true;
                }
                if (Singleton<HEditData>.IsInstance() && Singleton<HEditData>.Instance != null
                    && Singleton<HEditData>.Instance.info != null
                    && !string.IsNullOrEmpty(Singleton<HEditData>.Instance.info.title))
                {
                    display = Singleton<HEditData>.Instance.info.title;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static IRepairTargetSink BuildSink()
        {
            if (!Singleton<HEditData>.IsInstance() || Singleton<HEditData>.Instance == null)
                throw new RepairInputException("HEditData 不可用，无法定位写回目标。");
            return new RepairGameTargetSink(Singleton<HEditData>.Instance);
        }

        private static IRepairMemoryHost BuildMemoryGateway()
        {
            if (!Singleton<HEditData>.IsInstance() || Singleton<HEditData>.Instance == null
                || !Singleton<HEditGlobal>.IsInstance() || Singleton<HEditGlobal>.Instance == null
                || Singleton<HEditGlobal>.Instance.nodeControl == null)
                throw new RepairInputException("HEditData 或 NodeControl 不可用，无法创建内存快照。");
            return new RepairGameMemoryGateway(
                Singleton<HEditData>.Instance,
                Singleton<HEditGlobal>.Instance.nodeControl);
        }

        private void FailApply(string message)
        {
            applyStatus = message;
            applyStatusIsError = true;
        }

        private void ExportReport()
        {
            if (report == null)
                return;
            try
            {
                var path = BuildReportPath();
                var text = RepairPreflight.FormatText(report) + Environment.NewLine
                    + RepairApplyFormat.FormatText(applyReport, verification);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(text);
                status = "已导出文字报告。";
                statusIsError = false;
                exportedReportPath = path;
                log.LogMessage("Repair preflight report written: " + path);
            }
            catch (Exception ex)
            {
                Fail("导出报告失败：" + ex.Message);
            }
        }

        // 报告写到清单同目录的新文件，不改动清单或原资料。
        private string BuildReportPath()
        {
            var full = Path.GetFullPath(planPath.Trim());
            var directory = Path.GetDirectoryName(full) ?? Paths.GameRootPath;
            var name = Path.GetFileNameWithoutExtension(full);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            return Path.Combine(directory, name + ".preflight-" + stamp + ".txt");
        }

        private void Fail(string message)
        {
            status = message;
            statusIsError = true;
            report = null;
            exportedReportPath = string.Empty;
        }

        private void DrawResult()
        {
            if (report == null)
                return;

            GUILayout.Label("结论：" + report.Verdict, styles.Status);
            GUILayout.Label("检查时间：" + report.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                + "   耗时：" + report.ElapsedMilliseconds + " ms", wrapLabel);
            GUILayout.Label("清单：" + report.PlanPath + "   |   资料包：" + report.BundleId, wrapLabel);
            foreach (var issue in report.GlobalIssues)
                GUILayout.Label("全局问题：" + issue, wrapLabel);
            foreach (var item in report.Items)
            {
                GUILayout.Label((item.Index + 1) + ". " + item.Label + " —— " + (item.Passed ? "原文一致" : "冲突"), wrapLabel);
                GUILayout.Label("   位置：" + item.Pointer, wrapLabel);
                GUILayout.Label("   原文：" + Preview(item.Before), wrapLabel);
                GUILayout.Label("   新文：" + Preview(item.After), wrapLabel);
                GUILayout.Label("   当前：" + Preview(item.CurrentText), wrapLabel);
                foreach (var issue in item.SourceIssues)
                    GUILayout.Label("   - 资料包：" + issue, wrapLabel);
                foreach (var issue in item.CurrentIssues)
                    GUILayout.Label("   - 当前：" + issue, wrapLabel);
            }
            if (report.Items.Count == 0 && report.GlobalIssues.Count == 0)
                GUILayout.Label("清单没有需要核验的修改。", wrapLabel);
            GUILayout.Label("结论只表示清单原文与当前 Scene 文本一致，不代表脚本运行结果正确。", wrapLabel);
        }

        private static string Preview(string text)
        {
            if (text == null)
                return "(缺失)";
            var flat = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return flat.Length <= 200 ? flat : flat.Substring(0, 200) + "…";
        }

        private static string Display(string value)
        {
            return string.IsNullOrEmpty(value) ? "(无)" : value;
        }
    }
}
