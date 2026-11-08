using System.Collections.Generic;
using UnityEngine;
using ADVCreate;
using HEdit;
using AdvPart = HEdit.ADVPart;
using Cut = HEdit.ADVPart.Cut;

namespace EC_ADVCutSync
{
    // 只显示状态与发请求；执行/还原/跳转都排到 Update（Tick），不在 OnGUI 里改场景数据
    internal sealed class CutSyncWindow
    {
        private const int WindowId = 0x41435359;
        private const int FilterPopupId = 0x4143535A;
        private const float Width = 480f;
        private const float StatusSeconds = 8f;
        private static readonly CutSyncWindow instance = new CutSyncWindow();

        private readonly WindowStyles styles = new WindowStyles();
        private bool visible;
        private bool unfocusRequested;
        private bool moved;
        private Rect windowRect = new Rect(0f, 0f, Width, 10f);
        private Rect guiRect;
        private int screenWidth;
        private int screenHeight;
        private Vector2 scroll;
        private AnalysisResult analysis;
        private AdvPart seenPart;
        private Cut seenCut;
        private string seenSig;
        private float nextPoll;
        private Pending pendingOp = Pending.None;
        private Pending confirmOp = Pending.None;
        private AdvPart jumpPart;
        private int jumpIndex = -1;
        private int jumpChar = -1;
        private ItemKind? jumpLocate;
        private int expandedCut = -1;
        private int expandedChar = -2;
        private string status;
        private float statusUntil;
        private bool filterOpen;
        private Rect popupRect = new Rect();         // 筛选面板的屏幕坐标（供输入守卫与外点关闭）

        private enum Pending { None, Execute, Restore }

        public static bool IsVisible => instance.visible;
        public static bool HasKeyboardFocus => instance.visible && GUIUtility.keyboardControl != 0;

        public static bool IsMouseOverWindow()
        {
            if (!instance.visible)
                return false;
            var point = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            // 筛选面板浮在主窗口外，同样要被输入守卫覆盖
            return instance.guiRect.Contains(point) || (instance.filterOpen && instance.popupRect.Contains(point));
        }

        public static void ToggleFromButton() => instance.Toggle();
        public static void Reset() => instance.ResetState();
        public static void DrawGui() => instance.Draw();
        public static void Tick() => instance.RunPending();

        private static AdvPart CurrentPart()
        {
            if (!Singleton<ADVPartUICtrl>.IsInstance())
                return null;
            var ctrl = Singleton<ADVPartUICtrl>.Instance;
            // 离开 Part 编辑器回节点总览时 advPart 仍残留，pause=true 的非编辑态不响应
            if (ctrl.pause)
                return null;
            return ctrl.advPart;
        }

        // 游戏的确认框/退出页面以 additive 场景弹出，非空即有模态页面覆盖中
        private static bool DialogOverlayOpen()
            => Singleton<Manager.Scene>.IsInstance()
               && !string.IsNullOrEmpty(Singleton<Manager.Scene>.Instance.AddSceneName);

        private void Toggle()
        {
            if (visible) Close();
            else Open();
        }

        private void Open()
        {
            visible = true;
            screenWidth = 0;
            seenSig = null;
            // 记住的筛选面板开合状态
            SetFilterOpen(CutSyncPlugin.FilterOpen != null && CutSyncPlugin.FilterOpen.Value);
        }

        private void Close()
        {
            SavePosition();
            visible = false;
            unfocusRequested = true;
            confirmOp = Pending.None;
            guiRect = new Rect();
            WindowInputGuard.Reset();
        }

        private void ResetState()
        {
            Close();
            analysis = null;
            status = null;
            scroll = Vector2.zero;
            UndoStore.Last = null;
            styles.Dispose();
        }

        private void SetStatus(string text)
        {
            status = text;
            statusUntil = Time.time + StatusSeconds;
        }

        private void Draw()
        {
            // 退出确认等模态弹窗是 additive 场景（AddSceneName 非空）：打开期间暂停绘制，
            // 让游戏弹窗置顶；不改 visible，弹窗关闭后原样恢复
            if (DialogOverlayOpen())
            {
                guiRect = new Rect();
                popupRect = new Rect();
                return;
            }
            // 焦点在下一事件开头释放，点击当帧直接 UnfocusWindow 会让 InputHotkeyBlock 误判
            if (unfocusRequested || (visible && Event.current.type == EventType.MouseDown
                && !guiRect.Contains(Event.current.mousePosition) && !popupRect.Contains(Event.current.mousePosition)))
            {
                // 焦点仍要在点击外部时释放；筛选弹层只由筛选按钮决定开合
                GUIUtility.keyboardControl = 0;
                GUI.UnfocusWindow();
                unfocusRequested = false;
                if (Event.current.type == EventType.MouseDown && !guiRect.Contains(Event.current.mousePosition)
                    && !popupRect.Contains(Event.current.mousePosition))
                {
                    confirmOp = Pending.None;
                }
            }
            if (!visible)
                return;

            var color = GUI.color;
            var background = GUI.backgroundColor;
            var content = GUI.contentColor;
            var guiEnabled = GUI.enabled;
            try
            {
                GUI.color = GUI.backgroundColor = GUI.contentColor = Color.white;
                GUI.enabled = true;
                styles.Ensure();
                Place();
                guiRect = windowRect;
                WindowInputGuard.Sample();
                var before = windowRect.position;
                windowRect = GUILayout.Window(WindowId, windowRect, DrawWindow, GUIContent.none, styles.Window, GUILayout.Width(Width));
                windowRect = Clamp(windowRect);
                moved |= before != windowRect.position;
                guiRect = windowRect;
                if (Event.current.rawType == EventType.MouseUp)
                    SavePosition();
                // 只吃主窗口范围内的残余鼠标事件；筛选弹层区域留给弹层窗口自己处理
                var mousePoint = Event.current.mousePosition;
                if (guiRect.Contains(mousePoint) && !popupRect.Contains(mousePoint)
                    && (Event.current.isMouse || Event.current.type == EventType.ScrollWheel))
                    Event.current.Use();
                DrawFilterPopup();
            }
            finally
            {
                GUI.color = color;
                GUI.backgroundColor = background;
                GUI.contentColor = content;
                GUI.enabled = guiEnabled;
            }
        }

        // 分辨率变化或重新打开时按归一化坐标重新展开
        private void Place()
        {
            if (screenWidth == Screen.width && screenHeight == Screen.height)
                return;
            screenWidth = Screen.width;
            screenHeight = Screen.height;
            var wx = CutSyncPlugin.WindowX != null ? CutSyncPlugin.WindowX.Value : 0.02f;
            var wy = CutSyncPlugin.WindowY != null ? CutSyncPlugin.WindowY.Value : 0.05f;
            windowRect.x = (screenWidth - windowRect.width) * Mathf.Clamp01(wx);
            windowRect.y = (screenHeight - windowRect.height) * Mathf.Clamp01(wy);
            windowRect = Clamp(windowRect);
            moved = false;
        }

        // 窗口原点必须落在整数像素上：小数坐标会让字形跨像素插值，文字发虚
        private static Rect Clamp(Rect rect)
        {
            rect.x = Mathf.Round(Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, Screen.width - rect.width)));
            rect.y = Mathf.Round(Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, Screen.height - rect.height)));
            return rect;
        }

        private void SavePosition()
        {
            if (!moved || screenWidth <= 0)
                return;
            moved = false;
            if (CutSyncPlugin.WindowX != null)
                CutSyncPlugin.WindowX.Value = Mathf.Clamp01(windowRect.x / Mathf.Max(1f, screenWidth - windowRect.width));
            if (CutSyncPlugin.WindowY != null)
                CutSyncPlugin.WindowY.Value = Mathf.Clamp01(windowRect.y / Mathf.Max(1f, screenHeight - windowRect.height));
        }

        private void DrawWindow(int id)
        {
            var width = windowRect.width;
            // 多层底带（EC_LogFilter 风格）：整窗底色 + 标题带，避免纯黑背景
            WindowStyles.Band(new Rect(0f, 0f, width, Mathf.Max(windowRect.height, 40f)), WindowStyles.BaseBg);
            WindowStyles.Band(new Rect(0f, 0f, width, 32f), WindowStyles.HeaderBg);

            // 标题与关闭按钮固定在标题带内（GUILayout 自适应会把按钮挤到带外）
            GUI.Label(new Rect(10f, 3f, width - 84f, 26f), "ADV Cut 同步/维持化", styles.Header);
            if (GUI.Button(new Rect(width - 70f, 4f, 58f, 24f), "关闭", styles.Button))
                Close();
            GUILayout.Space(34f);

            var part = CurrentPart();
            if (part == null || part.cuts == null)
            {
                GUILayout.Label("请先打开 ADV Part 编辑器。", styles.Label);
                return;
            }

            var cuts = part.cuts;
            var charCount = cuts.Count > 0 ? cuts[0].charStates.Count : 0;
            GUILayout.Label("Part：" + cuts.Count + " cut / " + charCount + " 角色", styles.Label);
            GUILayout.Label("中间 cut（" + analysis.MiddleCount + " 个）显式值：", styles.Label);
            GUILayout.Label("穿搭 " + analysis.CoordExplicit + "｜服装 " + analysis.ClothesExplicit
                + "｜饰品 " + analysis.AccessoryExplicit + "｜汁液 " + analysis.LiquidExplicit
                + "｜音声 " + analysis.VoiceExplicit + "｜BGM " + analysis.BgmExplicit, styles.Label);
            GUILayout.Space(6f);

            GUI.enabled = analysis.NeedsOptimize;
            if (GUILayout.Button(analysis.NeedsOptimize
                    ? (confirmOp == Pending.Execute ? "确认：同步尾cut + 维持化" : "同步尾cut + 维持化")
                    : "无需优化",
                styles.Button, GUILayout.Height(26f)))
            {
                if (confirmOp == Pending.Execute)
                {
                    confirmOp = Pending.None;
                    pendingOp = Pending.Execute;
                }
                else
                {
                    confirmOp = Pending.Execute;
                }
            }
            GUI.enabled = true;

            var snap = UndoStore.Last;
            var canRestore = snap != null && snap.Owner == part;
            GUI.enabled = canRestore;
            if (GUILayout.Button(!canRestore ? "无还原点" : (confirmOp == Pending.Restore ? "确认：还原上次操作" : "还原上次操作"),
                styles.Button, GUILayout.Height(26f)) && canRestore)
            {
                if (confirmOp == Pending.Restore)
                {
                    confirmOp = Pending.None;
                    pendingOp = Pending.Restore;
                }
                else
                {
                    confirmOp = Pending.Restore;
                }
            }
            GUI.enabled = true;
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("状态变化点（点击跳转）", styles.Header);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(filterOpen ? "筛选▲" : "筛选▼", styles.Button, GUILayout.Width(56f), GUILayout.Height(20f)))
                SetFilterOpen(!filterOpen);
            GUILayout.EndHorizontal();
            GUILayout.Space(2f);

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.MinHeight(212f));
            ValidateFilterSelection();
            if (analysis.Entries.Count == 0)
                GUILayout.Label("无变化点（各 cut 间状态一致）。", styles.Label);
            foreach (var entry in analysis.Entries)
            {
                if (FilterMatches(entry))
                    DrawEntry(part, entry);
            }
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(status) && Time.time < statusUntil)
                GUILayout.Label(status, styles.Warning);

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 32f));
        }

        // 行点击 = 跳转（延后到 Update 执行）；"详情"展开该 cut 该角色的旧值→新值明细
        private void DrawEntry(AdvPart part, AnchorEntry entry)
        {
            var expanded = expandedCut == entry.CutIndex && expandedChar == entry.CharIndex;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("cut " + (entry.CutIndex + 1) + "　" + entry.Summary, styles.Row, GUILayout.Height(24f)))
            {
                jumpPart = part;
                jumpIndex = entry.CutIndex;
            }
            if (GUILayout.Button(expanded ? "收起" : "详情", styles.Button, GUILayout.Width(48f), GUILayout.Height(24f)))
            {
                if (expanded)
                {
                    expandedCut = -1;
                    expandedChar = -2;
                }
                else
                {
                    expandedCut = entry.CutIndex;
                    expandedChar = entry.CharIndex;
                }
            }
            GUILayout.EndHorizontal();
            if (expanded)
            {
                // 明细行可点击：跳转并把原生面板定位到对应栏目；子行相对父行缩进
                foreach (var detail in entry.Details)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(16f);
                    if (GUILayout.Button("　" + detail.Text, styles.Detail, GUILayout.Height(22f)))
                    {
                        jumpPart = part;
                        jumpIndex = entry.CutIndex;
                        jumpChar = entry.CharIndex;
                        jumpLocate = detail.Kind;
                    }
                    GUILayout.EndHorizontal();
                }
            }
        }

        private void SetFilterOpen(bool open)
        {
            filterOpen = open;
            if (!open)
                popupRect = new Rect();
            if (CutSyncPlugin.FilterOpen != null)
                CutSyncPlugin.FilterOpen.Value = open;
        }

        // 筛选口径：-2 全部，-1 仅 BGM，>=0 仅该角色（CharIndex 口径一致）
        private bool FilterMatches(AnchorEntry entry)
        {
            var sel = CutSyncPlugin.FilterSelection != null ? CutSyncPlugin.FilterSelection.Value : -2;
            return sel == -2 || entry.CharIndex == sel;
        }

        // 筛选对象已无变化点时回落到"全部"；popup 关闭时同样生效，避免列表被过滤成空
        private void ValidateFilterSelection()
        {
            var sel = CutSyncPlugin.FilterSelection != null ? CutSyncPlugin.FilterSelection.Value : -2;
            if (sel == -2 || CountEntries(sel) > 0)
                return;
            if (CutSyncPlugin.FilterSelection != null)
                CutSyncPlugin.FilterSelection.Value = -2;
        }

        // 筛选面板：贴筛选按钮右侧、与其顶对齐；只列出有变化点的 BGM/角色
        private void DrawFilterPopup()
        {
            if (!filterOpen || analysis == null)
            {
                popupRect = new Rect();
                return;
            }

            // 候选：全部 + BGM（若有）+ 有变化点的角色（按索引序）
            var options = new List<FilterOption>();
            options.Add(new FilterOption { Value = -2, Label = "全部" });
            var bgmCount = CountEntries(-1);
            if (bgmCount > 0)
                options.Add(new FilterOption { Value = -1, Label = "BGM (" + bgmCount + ")" });
            for (var i = 0; i < analysis.Part.cuts[0].charStates.Count; i++)
            {
                var count = CountEntries(i);
                if (count > 0)
                    options.Add(new FilterOption { Value = i, Label = CutAnchorAnalysis.NameOf(i) + " (" + count + ")" });
            }

            // 本帧已在 ValidateFilterSelection 校验过筛选对象
            var sel = CutSyncPlugin.FilterSelection != null ? CutSyncPlugin.FilterSelection.Value : -2;

            const float popupWidth = 140f;
            // 窗口样式上下 padding 共约 22px，需计入否则末行被裁
            var height = options.Count * 24f + 8f + 22f;
            // 停靠式定位（EC_HEditPosePanel 姿势库伴生面板同方案）：贴主窗口右缘，放不下贴左侧，
            // 两侧都放不下选空间大的一侧；垂直居中，夹紧屏幕。不依赖窗口内控件坐标。
            popupRect = CalculateDockedRect(guiRect, popupWidth, height);
            GUI.Window(FilterPopupId, popupRect, DrawFilterPopupContent, GUIContent.none, styles.Window);
            return;

            void DrawFilterPopupContent(int id)
            {
                WindowStyles.Band(new Rect(0f, 0f, popupWidth, height), WindowStyles.BaseBg);
                foreach (var option in options)
                {
                    var selected = option.Value == sel;
                    if (GUILayout.Button((selected ? "✔ " : "") + option.Label, styles.Detail, GUILayout.Height(22f)))
                    {
                        // 只切换筛选对象，不收起面板；开合只由筛选按钮决定
                        if (CutSyncPlugin.FilterSelection != null)
                            CutSyncPlugin.FilterSelection.Value = option.Value;
                    }
                }
            }
        }

        // 筛选弹层停靠矩形：右缘优先，放不下贴左，都不行选空间大的一侧；垂直居中并夹紧屏幕
        private static Rect CalculateDockedRect(Rect main, float width, float height)
        {
            var rightX = main.xMax;
            var leftX = main.x - width;
            var fitsRight = rightX + width <= Screen.width;
            var fitsLeft = leftX >= 0f;
            float x;
            if (fitsRight)
                x = rightX;
            else if (fitsLeft)
                x = leftX;
            else
                x = Screen.width - main.xMax >= main.x ? rightX : leftX;
            var y = main.y + Mathf.Max(0f, (main.height - height) * 0.5f);
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - height));
            // 原点取整：小数坐标会让字形跨像素插值，文字发虚
            return new Rect(Mathf.Round(x), Mathf.Round(y), width, height);
        }

        private sealed class FilterOption
        {
            public int Value;
            public string Label;
        }

        private int CountEntries(int charIndex)
        {
            var count = 0;
            foreach (var entry in analysis.Entries)
            {
                if (entry.CharIndex == charIndex)
                    count++;
            }
            return count;
        }

        private void RunPending()
        {
            // 低频轮询当前 cut 的数据签名：跳转/原生编辑后自动重算变化点与待优化判定
            if (visible && Time.time >= nextPoll)
            {
                nextPoll = Time.time + 0.5f;
                PollAnalysis();
            }

            if (jumpIndex >= 0)
            {
                var part = jumpPart;
                var index = jumpIndex;
                var locateChar = jumpChar;
                var locateKind = jumpLocate;
                jumpIndex = -1;
                jumpPart = null;
                jumpChar = -1;
                jumpLocate = null;
                var ok = EditorJump.JumpTo(part, index, locateChar, locateKind);
                SetStatus(ok
                    ? "已跳转到 cut " + (index + 1) + (locateKind.HasValue ? "，面板已定位" : "")
                    : "跳转失败（Part 已切换或 cut 不存在）");
            }

            var op = pendingOp;
            if (op == Pending.None)
                return;
            pendingOp = Pending.None;
            var current = CurrentPart();
            if (current == null)
                return;
            if (op == Pending.Execute)
                Execute(current);
            else
                Restore(current);
            RefreshAnalysis(current, Singleton<ADVPartUICtrl>.IsInstance() ? Singleton<ADVPartUICtrl>.Instance.cut : null);
        }

        private void Execute(AdvPart part)
        {
            var result = CutSyncService.Run(part);
            if (result == null)
            {
                SetStatus("cut 少于 2 个，无可执行");
                return;
            }
            if (!result.Any)
            {
                SetStatus("无需改动：" + result.Describe());
                return;
            }
            UndoStore.Last = result.Snapshot;
            SetStatus("完成：" + result.Describe());
            RefreshEditorView(part, result.Snapshot.AffectedCuts);
        }

        private void Restore(AdvPart part)
        {
            var snap = UndoStore.Last;
            if (snap == null)
            {
                SetStatus("没有可还原的操作");
                return;
            }
            if (snap.Owner != part)
            {
                UndoStore.Last = null;
                SetStatus("已切换 Part，还原点失效");
                return;
            }
            var affected = snap.Restore();
            UndoStore.Last = null;
            SetStatus("已还原上次操作");
            RefreshEditorView(part, affected);
        }

        // 当前选中 cut 被改动时强制重载编辑器画面与面板
        private static void RefreshEditorView(AdvPart part, List<AdvPart.Cut> affected)
        {
            if (!Singleton<ADVPartUICtrl>.IsInstance())
                return;
            var ctrl = Singleton<ADVPartUICtrl>.Instance;
            if (ctrl.advPart != part || ctrl.cut == null)
                return;
            foreach (var cut in affected)
            {
                if (cut == ctrl.cut)
                {
                    ctrl.ReloadCut();
                    return;
                }
            }
        }

        private void RefreshAnalysis(AdvPart part, Cut currentCut)
        {
            analysis = CutAnchorAnalysis.Analyze(part);
            seenPart = part;
            seenCut = currentCut;
            seenSig = CutSignature(currentCut);
            confirmOp = Pending.None;
        }

        // 签名只含当前会被原生面板编辑的字段；内容级变化靠 cut 切换或重开面板兜底
        private void PollAnalysis()
        {
            var part = CurrentPart();
            if (part == null || part.cuts == null)
            {
                seenPart = null;
                seenCut = null;
                seenSig = null;
                return;
            }
            var cut = Singleton<ADVPartUICtrl>.Instance.cut;
            var sig = CutSignature(cut);
            if (ReferenceEquals(part, seenPart) && ReferenceEquals(cut, seenCut) && sig == seenSig)
                return;
            RefreshAnalysis(part, cut);
        }

        private static string CutSignature(Cut cut)
        {
            if (cut == null)
                return string.Empty;
            var sb = new System.Text.StringBuilder();
            sb.Append(cut.cameraImageEffectInfo.bgmInfo.id).Append(';');
            foreach (var cs in cut.charStates)
            {
                sb.Append(cs.visible ? 'v' : 'h');
                sb.Append(cs.coordinate.type).Append('|').Append(cs.coordinate.name).Append('|')
                    .Append(cs.coordinate.version).Append('|').Append(cs.coordinate.data != null ? cs.coordinate.data.Length : -1).Append('|');
                foreach (var v in cs.clothes)
                    sb.Append(v).Append(',');
                foreach (var v in cs.accessory)
                    sb.Append(v).Append(',');
                foreach (var v in cs.liquid)
                    sb.Append(v).Append(',');
                sb.Append(cs.voice.type).Append(';');
            }
            return sb.ToString();
        }
    }
}
