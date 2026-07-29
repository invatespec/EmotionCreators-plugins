using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HEdit;
using Manager;
using UnityEngine;
using YS_Node;

namespace EC_NodeSidePanel
{
    // HEdit 节点编辑页侧边面板：列表/搜索/文件夹/滑条/位移 + 画布扩展（原 EC_NodeCanvasExpand）。
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    public sealed class NodeSidePanelPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_NodeSidePanel";
        public const string PluginName = "EC Node Side Panel";
        public const string Version = "2.1.0";

        internal static ManualLogSource Log;
        internal static NodeSidePanelPlugin Instance;

        private const float PanelW = 300f;
        private const float PanelH = 480f;
        private const float Margin = 32f;
        private const float ToggleSize = 28f;
        // 底栏/迷你窗内边距：▣ 四周留白，避免 box 边框裁切或露底
        private const float FooterPad = 4f;
        // 收起迷你窗 = 按钮 + 四周 pad
        private const float MiniSize = ToggleSize + FooterPad * 2f;
        private const float NudgeW = 260f;
        private const float NudgeH = 320f;
        // 子面板上半部给连接区，下半部给位移区
        private const float ConnectH = 172f;
        private const int WinIdMain = 0x4E535031; // NSP1
        private const int WinIdNudge = 0x4E535032;
        // IMGUI 文本控件名：用于 GetNameOfFocusedControl，驱动 isInputNow 等价
        private const string CtrlSearch = "NSP_Search";
        private const string CtrlRename = "NSP_Rename";
        private const string CtrlConnOut = "NSP_ConnOut";
        private const string CtrlConnIn = "NSP_ConnIn";
        private static readonly HashSet<string> TextControlNames = new HashSet<string>
        {
            CtrlSearch, CtrlRename, CtrlConnOut, CtrlConnIn,
        };
        private const float FocusHintPulseDuration = 1.6f;
        private const float FocusHintHoldDuration = 0.4f;
        private const float FocusHintThickness = 3f;
        // 高亮重算间隔：染色幂等且极廉价，轮询顺带盖住选择变化/线重建/原版刷白
        private const float HighlightSyncInterval = 0.2f;
        private const float ConnectMsgDuration = 8f;
        // CJK 字形下沿会被 18f 高的 Label 裁掉，统一用 20f
        private const float LabelH = 20f;

        // 连接待确认方向：防误触，点「连接」只置位，真正写入要再点「确认」
        private enum PendingConnect
        {
            None,
            Out,
            In,
        }

        // 列表高亮：深饱和蓝
        private static readonly Color HighlightBg = new Color(0.12f, 0.42f, 0.95f, 1f);
        private static readonly Color HighlightText = Color.white;
        private static readonly Color PanelOuterBg = new Color32(0x1E, 0x23, 0x29, 0xFF);
        private static readonly Color PanelContentBg = new Color32(0x28, 0x2E, 0x36, 0xFF);
        private static readonly Color PanelFooterBg = new Color32(0x18, 0x1C, 0x21, 0xFF);
        private static readonly Color SeparatorColor = new Color32(0x4A, 0x52, 0x5C, 0xFF);
        private static readonly Color FocusHintColor = new Color32(0xFF, 0xC1, 0x20, 0xFF);

        // ▣ 屏幕 Y（展开/收起一致）：窗底钉 Margin，再上抬 FooterPad
        private static float ToggleScreenY => Screen.height - Margin - ToggleSize - FooterPad;

        internal readonly NodeListModel ListModel = new NodeListModel();
        internal readonly FolderTreeService Folders = new FolderTreeService();
        internal readonly SceneConfigStore ConfigStore = new SceneConfigStore();
        private readonly LineHighlightService _highlight = new LineHighlightService();

        private ConfigEntry<float> _sizeMultiplier;
        private Harmony _harmony;

        private bool _panelOpen;
        private bool _nudgeOpen;
        private bool _wasNodePage;
        private Vector2 _listScroll;
        private string _renameFolderId;
        private string _renameBuffer = string.Empty;
        // 搜索 ▲/▼ 后，下一次列表绘制把 ScrollView 滚到高亮行
        private bool _scrollToHighlight;
        private NodeUI _focusHintNode;
        private float _focusHintStartedAt = -1f;
        // 搜索/重命名 TextField 聚焦时为 true；Hooks 用它 OR 进 isInputNow
        private bool _textInputFocused;

        // 连接区状态
        private float _nextHighlightSyncAt;
        private int _connectSlot;
        private string _connectOutText = string.Empty;
        private string _connectInText = string.Empty;
        // 面板只放短状态；逐条明细太长会撑爆窗，改走 BepInEx 日志
        private string _connectStatus = string.Empty;
        private float _connectStatusAt = -1f;
        private PendingConnect _pending;

        // IMGUI 屏幕坐标（Y 向下），用于吞画布输入
        private Rect _mainPanelGuiRect;
        private Rect _nudgePanelGuiRect;
        private bool _hasMainHit;
        private bool _hasNudgeHit;

        // 无标题栏 box 窗，避免空 title bar 裁切迷你态
        private GUIStyle _panelWindowStyle;

        // 节点页检测缓存：FindObjectOfType 每帧扫全场景极贵
        private NodeSettingCanvas _cachedNodeSetting;
        private float _nextNodePageProbeAt;
        private const float NodePageProbeInterval = 0.25f;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            ConfigStore.Log = Log;
            CanvasExpandService.Log = Log;

            _sizeMultiplier = Config.Bind(
                "Canvas",
                "SizeMultiplier",
                5f,
                new ConfigDescription(
                    "节点画布 rtfGrid 尺寸乘数（相对原版 sizeDelta）",
                    new AcceptableValueRange<float>(1f, 10f)));
            CanvasExpandService.SizeMultiplier = _sizeMultiplier.Value;
            _sizeMultiplier.SettingChanged += (_, __) =>
                CanvasExpandService.SizeMultiplier = _sizeMultiplier.Value;

            Hooks.Plugin = this;
            _harmony = new Harmony(GUID);
            _harmony.PatchAll(typeof(Hooks));
            Log.LogInfo($"{PluginName} v{Version} loaded. canvas multiplier={_sizeMultiplier.Value}");
        }

        private void OnDestroy()
        {
            ConfigStore.Flush(ListModel, Folders);
            ClearHighlight();
            _harmony?.UnpatchSelf();
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            bool onNode = IsNodeEditPage();
            if (_wasNodePage && !onNode)
            {
                ConfigStore.Flush(ListModel, Folders);
                ClearHitRects();
                _textInputFocused = false;
                ClearHighlight();
            }
            _wasNodePage = onNode;
            ConfigStore.TickDebounced(ListModel, Folders);

            // 列表同步放 Update，避免 OnGUI 每事件（Layout/Repaint）跑两遍
            if (onNode && _panelOpen && ListModel.ListDirty)
                RefreshListCaches();

            if (onNode && _panelOpen && _highlight.AnyMode && Time.unscaledTime >= _nextHighlightSyncAt)
                SyncHighlight();
        }

        // 目标空（含点「清除选中」）时 Sync 内部会还原线色并关掉两个开关
        private void SyncHighlight()
        {
            _nextHighlightSyncAt = Time.unscaledTime + HighlightSyncInterval;
            NodeControl control;
            CanvasPanController.TryGetControl(out control);
            if (control == null)
                return;
            _highlight.Sync(control, NodeNudgeService.ResolveTargets(control, ListModel, Folders));
        }

        private void ClearHighlight()
        {
            NodeControl control;
            CanvasPanController.TryGetControl(out control);
            _highlight.Clear(control);
        }

        // 脏时：同步创建序 / 剪枝文件夹 / 重建排序缓存
        private void RefreshListCaches()
        {
            NodeControl control;
            if (!CanvasPanController.TryGetControl(out control) || control == null)
                return;
            ListModel.EnsureCreationOrder(control);
            Folders.PruneMissing(control);
            Folders.RebuildCachesIfNeeded(ListModel, control);
            ListModel.GetUngroupedUserNodes(control, Folders);
            ListModel.ListDirty = false;
        }

        private void OnGUI()
        {
            if (!IsNodeEditPage())
            {
                ClearHitRects();
                _textInputFocused = false;
                return;
            }

            // 恢复默认 IMGUI 外观，不做自定义主题
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
            GUI.color = Color.white;

            DrawFocusHint();
            EnsurePanelWindowStyle();

            // 始终同一 GUI.Window；▣ 只画在 Window 回调内。
            // 展开：窗底钉 Screen.height-Margin；▣ 在 (FooterPad, PanelH-ToggleSize-FooterPad)。
            // 收起：迷你窗 MiniSize（按钮+四周 pad），▣ 居中 → 屏幕坐标与展开一致，无裁切/露底。
            float winW = _panelOpen ? PanelW : MiniSize;
            float winH = _panelOpen ? PanelH : MiniSize;
            float winX = Margin;
            float winY = _panelOpen
                ? Screen.height - Margin - PanelH
                : ToggleScreenY - FooterPad; // 迷你窗包住 ▣
            _mainPanelGuiRect = new Rect(winX, winY, winW, winH);
            _hasMainHit = true;

            GUI.Window(WinIdMain, _mainPanelGuiRect, DrawMainWindow, GUIContent.none, _panelWindowStyle);

            if (_panelOpen && _nudgeOpen)
            {
                _nudgePanelGuiRect = new Rect(winX + PanelW + 4f, winY + PanelH - NudgeH, NudgeW, NudgeH);
                _hasNudgeHit = true;
                GUI.Window(WinIdNudge, _nudgePanelGuiRect, DrawNudgeWindow, GUIContent.none, _panelWindowStyle);
            }
            else
            {
                _hasNudgeHit = false;
            }

            if (_panelOpen)
            {
                _textInputFocused = TextControlNames.Contains(GUI.GetNameOfFocusedControl());
            }
            else
            {
                _textInputFocused = false;
            }

            EatInputOverPanel();
        }

        private void EnsurePanelWindowStyle()
        {
            if (_panelWindowStyle != null)
                return;
            // 无标题栏：用 box 作窗体，避免默认 window 标题栏在迷你态挤掉 ▣
            _panelWindowStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(2, 2, 2, 2),
                alignment = TextAnchor.UpperLeft,
            };
        }

        // 供 Hooks OR 进 HEditGlobal.isInputNow（属性只读，不能直写）
        internal static bool IsTextInputFocused
            => Instance != null && Instance._textInputFocused;

        // 供画布缩放/拖拽判断：鼠标是否在面板命中区（IMGUI 屏幕坐标，Y 向下）
        internal static bool IsMouseOverPanel()
        {
            var inst = Instance;
            if (inst == null || !inst._wasNodePage)
                return false;
            Vector2 guiMouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (inst._hasMainHit && inst._mainPanelGuiRect.Contains(guiMouse))
                return true;
            if (inst._hasNudgeHit && inst._nudgePanelGuiRect.Contains(guiMouse))
                return true;
            return false;
        }

        private void ClearHitRects()
        {
            _hasMainHit = false;
            _hasNudgeHit = false;
        }

        // 面板上吞掉所有鼠标/滚轮事件，防止落到 uGUI/画布。
        // 在控件绘制之后调用：IMGUI 控件已处理完自己的事件；剩余穿透事件在此消费。
        // 画布侧再由 Harmony 挡 GridDrag/NodeDrag/连线 + EventSystem 射线清空双保险。
        private void EatInputOverPanel()
        {
            if (Event.current == null || !IsMouseOverPanel())
                return;

            switch (Event.current.type)
            {
                case EventType.MouseDown:
                case EventType.MouseUp:
                case EventType.MouseDrag:
                case EventType.MouseMove:
                case EventType.ScrollWheel:
                case EventType.ContextClick:
                    Event.current.Use();
                    break;
            }
        }

        private void DrawMainWindow(int id)
        {
            float contentH = PanelH - ToggleSize - FooterPad * 2f - 4f;
            DrawSolid(new Rect(0f, 0f, _mainPanelGuiRect.width, _mainPanelGuiRect.height), PanelOuterBg);
            if (_panelOpen)
            {
                DrawSolid(new Rect(4f, 4f, PanelW - 8f, contentH), PanelContentBg);
                DrawSolid(new Rect(2f, contentH + 4f, PanelW - 4f, PanelH - contentH - 6f), PanelFooterBg);
            }

            // 底栏绝对坐标：▣ 左 / 位移右，同尺寸同行。
            // 收起态 ▣ 居中于 MiniSize 窗内，避免右侧裁切、左侧/底下 box 露边。
            // 必须在 Window 回调内画 ▣（窗外 Button 会被 Window 挡住）。
            float footerY = _panelOpen ? PanelH - ToggleSize - FooterPad : FooterPad;
            if (GUI.Button(new Rect(FooterPad, footerY, ToggleSize, ToggleSize), "▣"))
            {
                if (_panelOpen)
                {
                    // 收起：先落盘，方便用户迁目录后再展开热重载
                    ConfigStore.Flush(ListModel, Folders);
                    ClearHighlight();
                    _panelOpen = false;
                    _nudgeOpen = false;
                }
                else
                {
                    // 展开：按当前场景名重新读盘（手动迁移后的入口）
                    ReloadConfigFromDisk();
                    _panelOpen = true;
                }
            }

            if (!_panelOpen)
                return;

            // 位移开关：与 ▣ 同大、同 Y，靠右
            if (GUI.Button(
                    new Rect(PanelW - FooterPad - ToggleSize, footerY, ToggleSize, ToggleSize),
                    _nudgeOpen ? "◀" : "▶"))
                _nudgeOpen = !_nudgeOpen;
            GUI.Label(
                new Rect(PanelW - FooterPad - ToggleSize - 64f, footerY, 60f, ToggleSize),
                "节点操作");

            // 内容区避开底栏
            GUILayout.BeginArea(new Rect(4f, 4f, PanelW - 8f, contentH));
            GUILayout.Label("节点面板");

            // 搜索行
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(CtrlSearch);
            string newSearch = GUILayout.TextField(ListModel.SearchText ?? string.Empty, GUILayout.ExpandWidth(true));
            if (newSearch != ListModel.SearchText)
            {
                ListModel.SearchText = newSearch;
                ListModel.ResetSearchCursor();
            }
            if (GUILayout.Button("▲", GUILayout.Width(28f)))
                StepSearch(-1);
            if (GUILayout.Button("▼", GUILayout.Width(28f)))
                StepSearch(1);
            GUILayout.EndHorizontal();

            // 排序（单独一行）
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(ListModel.SortMode == SortMode.Created, "时间", GUILayout.Width(48f)))
            {
                if (ListModel.SortMode != SortMode.Created)
                {
                    ListModel.SortMode = SortMode.Created;
                    ListModel.MarkDirty();
                    ConfigStore.MarkDirty();
                }
            }
            if (GUILayout.Toggle(ListModel.SortMode == SortMode.Name, "名称", GUILayout.Width(48f)))
            {
                if (ListModel.SortMode != SortMode.Name)
                {
                    ListModel.SortMode = SortMode.Name;
                    ListModel.MarkDirty();
                    ConfigStore.MarkDirty();
                }
            }
            if (GUILayout.Button(ListModel.Ascending ? "升↑" : "降↓", GUILayout.Width(40f)))
            {
                ListModel.Ascending = !ListModel.Ascending;
                ListModel.MarkDirty();
                ConfigStore.MarkDirty();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // 文件夹操作：选中夹时 +文件夹=子夹；移入=勾选节点进当前高亮夹
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+文件夹", GUILayout.Width(64f)))
            {
                string parent = ListModel.HighlightFolderId; // 有选中夹则建子夹，否则根夹
                var f = Folders.CreateFolder(
                    string.IsNullOrEmpty(parent) ? "新建文件夹" : "子文件夹",
                    parent);
                ListModel.HighlightFolderId = f.Id;
                ListModel.HighlightUid = null;
                ListModel.MarkDirty();
                ConfigStore.MarkDirty();
            }
            if (GUILayout.Button("重命名", GUILayout.Width(52f)))
            {
                if (!string.IsNullOrEmpty(ListModel.HighlightFolderId)
                    && Folders.Folders.TryGetValue(ListModel.HighlightFolderId, out var f))
                {
                    _renameFolderId = f.Id;
                    _renameBuffer = f.Name;
                }
            }
            if (GUILayout.Button("删文件夹", GUILayout.Width(64f)))
            {
                if (!string.IsNullOrEmpty(ListModel.HighlightFolderId))
                {
                    Folders.DeleteFolder(ListModel.HighlightFolderId);
                    ListModel.HighlightFolderId = null;
                    ListModel.MarkDirty();
                    ConfigStore.MarkDirty();
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // 节点归属
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("移入", GUILayout.Width(48f)))
                DoMoveIn();
            if (GUILayout.Button("移出", GUILayout.Width(48f)))
                DoMoveOut();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // 清除选中 / 定位 / 连线高亮（独立一行；定位靠右方便连点）
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("清除选中", GUILayout.Width(72f)))
                ClearSelection();
            if (GUILayout.Button("定位", GUILayout.Width(48f)))
                FocusHighlight();
            if (GUILayout.Toggle(_highlight.InMode, "入线", GUI.skin.button, GUILayout.Width(44f)) != _highlight.InMode)
                ToggleLineHighlight(input: true);
            if (GUILayout.Toggle(_highlight.OutMode, "出线", GUI.skin.button, GUILayout.Width(44f)) != _highlight.OutMode)
                ToggleLineHighlight(input: false);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_renameFolderId))
            {
                GUILayout.BeginHorizontal();
                GUI.SetNextControlName(CtrlRename);
                _renameBuffer = GUILayout.TextField(_renameBuffer ?? string.Empty);
                if (GUILayout.Button("OK", GUILayout.Width(36f)))
                {
                    Folders.Rename(_renameFolderId, _renameBuffer);
                    _renameFolderId = null;
                    ListModel.MarkDirty();
                    ConfigStore.MarkDirty();
                }
                if (GUILayout.Button("X", GUILayout.Width(24f)))
                    _renameFolderId = null;
                GUILayout.EndHorizontal();
            }

            // 列表区：左竖滑条 + 滚动列表（同步已在 Update 脏路径完成）
            NodeControl control;
            Vector2 panPos, maxPan;
            bool hasPan = CanvasPanController.TryGetPanState(out control, out panPos, out maxPan, out _);

            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            // 竖向 pan 滑条（左）；悬停滚轮也可调
            float vVal = 0f;
            if (hasPan && maxPan.y > 0.01f)
                vVal = Mathf.InverseLerp(maxPan.y, -maxPan.y, panPos.y); // 上=1 视觉
            float newV = GUILayout.VerticalSlider(vVal, 1f, 0f, GUILayout.Width(18f), GUILayout.ExpandHeight(true));
            Rect vSliderRect = GUILayoutUtility.GetLastRect();
            if (hasPan && maxPan.y > 0.01f)
            {
                if (TryWheelOnSlider(vSliderRect, ref newV, invert: true) || !Mathf.Approximately(newV, vVal))
                {
                    panPos.y = Mathf.Lerp(maxPan.y, -maxPan.y, newV);
                    CanvasPanController.SetPan(control, panPos);
                }
            }

            // 列表
            _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (control != null)
                DrawNodeList(control);
            else
                GUILayout.Label("无 NodeControl");
            GUILayout.EndScrollView();
            GUILayout.EndHorizontal();

            // 横向 pan 滑条：左=视口在画布最左，右=最右（pan 与视觉反向）
            // 复用上面的 pan 状态，避免再走一遍 TryGetPanState；悬停滚轮也可调
            float hVal = 0.5f;
            if (hasPan && maxPan.x > 0.01f)
                hVal = Mathf.InverseLerp(maxPan.x, -maxPan.x, panPos.x);
            float newH = GUILayout.HorizontalSlider(hVal, 0f, 1f);
            Rect hSliderRect = GUILayoutUtility.GetLastRect();
            if (hasPan && maxPan.x > 0.01f)
            {
                if (TryWheelOnSlider(hSliderRect, ref newH, invert: false) || !Mathf.Approximately(newH, hVal))
                {
                    panPos.x = Mathf.Lerp(maxPan.x, -maxPan.x, newH);
                    CanvasPanController.SetPan(control, panPos);
                }
            }

            GUILayout.EndArea();
        }

        // 滑条命中区内滚轮调 0..1 值。invert：竖条上滚=增大（与视觉上一致）
        // delta.y>0 通常为向下滚；一步约 0.015（notch≈3）
        private static bool TryWheelOnSlider(Rect rect, ref float value01, bool invert)
        {
            if (Event.current == null || Event.current.type != EventType.ScrollWheel)
                return false;
            if (!rect.Contains(Event.current.mousePosition))
                return false;
            float step = Event.current.delta.y * 0.005f;
            if (invert)
                step = -step;
            float next = Mathf.Clamp01(value01 + step);
            if (Mathf.Approximately(next, value01))
            {
                Event.current.Use();
                return false;
            }
            value01 = next;
            Event.current.Use();
            return true;
        }

        private void DrawNudgeWindow(int id)
        {
            DrawSolid(new Rect(0f, 0f, NudgeW, NudgeH), PanelOuterBg);

            NodeControl control;
            if (!CanvasPanController.TryGetControl(out control))
            {
                GUI.Label(new Rect(8f, 4f, NudgeW - 16f, LabelH), "N/A");
                return;
            }

            var targets = NodeNudgeService.ResolveTargets(control, ListModel, Folders);
            DrawConnectSection(control, targets);
            DrawSolid(new Rect(6f, ConnectH - 3f, NudgeW - 12f, 1f), SeparatorColor);
            DrawNudgeSection(targets);
        }

        // 连接区：仅在解析出的目标恰好 1 个时可用，否则整体禁用
        private void DrawConnectSection(NodeControl control, List<NodeUI> targets)
        {
            const float pad = 8f;
            const float btnW = 44f;
            float w = NudgeW - pad * 2f;
            float fieldW = w - 34f - btnW - 2f;
            GUI.Label(new Rect(pad, 4f, w, LabelH), "节点连接");

            NodeUI sel = targets.Count == 1 ? targets[0] : null;
            if (sel == null)
                _pending = PendingConnect.None;
            int slotCount = NodeSlotInfo.OutputCount(sel);
            _connectSlot = slotCount > 0 ? Mathf.Clamp(_connectSlot, 0, slotCount - 1) : 0;

            GUI.enabled = sel != null;

            if (GUI.Button(new Rect(pad, 26f, 22f, 22f), "◀"))
                StepSlot(-1, slotCount);
            GUI.Label(new Rect(pad + 24f, 27f, 44f, LabelH), $"输出 {_connectSlot}");
            if (GUI.Button(new Rect(pad + 68f, 26f, 22f, 22f), "▶"))
                StepSlot(1, slotCount);
            GUI.Label(new Rect(pad + 94f, 27f, w - 94f, LabelH), DescribeSlot(control, sel));

            GUI.Label(new Rect(pad, 53f, 32f, LabelH), "目标");
            GUI.SetNextControlName(CtrlConnOut);
            _connectOutText = GUI.TextField(new Rect(pad + 34f, 52f, fieldW, 22f), _connectOutText ?? string.Empty);
            if (GUI.Button(new Rect(NudgeW - pad - btnW, 52f, btnW, 22f), "连接"))
                _pending = PendingConnect.Out;

            GUI.Label(new Rect(pad, 79f, 32f, LabelH), "来源");
            GUI.SetNextControlName(CtrlConnIn);
            _connectInText = GUI.TextField(new Rect(pad + 34f, 78f, fieldW, 22f), _connectInText ?? string.Empty);
            if (GUI.Button(new Rect(NudgeW - pad - btnW, 78f, btnW, 22f), "连接"))
                _pending = PendingConnect.In;

            GUI.enabled = true;

            GUI.Label(new Rect(pad, 102f, w, LabelH), "多个用 , 分隔  A:2 指定槽");
            DrawConnectConfirm(control, sel, pad);
            GUI.Label(new Rect(pad, 148f, w, LabelH),
                sel == null ? "仅选中单个节点时可用" : CurrentConnectStatus());
        }

        // 确认行：只在待确认时占位，避免空态露两个死按钮
        private void DrawConnectConfirm(NodeControl control, NodeUI sel, float pad)
        {
            if (_pending == PendingConnect.None || sel == null)
                return;

            GUI.Label(new Rect(pad, 125f, 88f, LabelH),
                _pending == PendingConnect.Out ? "确认连目标?" : "确认连来源?");
            if (GUI.Button(new Rect(pad + 90f, 124f, 50f, 22f), "确认"))
            {
                if (_pending == PendingConnect.Out)
                    DoConnectOut(control, sel);
                else
                    DoConnectIn(control, sel);
                _pending = PendingConnect.None;
            }
            if (GUI.Button(new Rect(pad + 144f, 124f, 50f, 22f), "取消"))
                _pending = PendingConnect.None;
        }

        private void DrawNudgeSection(List<NodeUI> targets)
        {
            GUI.Label(new Rect(8f, ConnectH + 2f, NudgeW - 16f, LabelH), "节点位移");
            // 标题栏下多留一点，避免「目标」被截断
            GUI.Label(new Rect(8f, ConnectH + 22f, NudgeW - 16f, LabelH),
                targets.Count > 0 ? $"目标:{targets.Count}" : "未选目标");

            // 固定坐标十字，保证 ↑ 与 ↓ 同列、←↓→ 同行且不被裁切
            const float btnW = 36f;
            const float btnH = 28f;
            const float gap = 4f;
            float col = (NudgeW - (btnW * 3f + gap * 2f)) * 0.5f;
            float row0 = ConnectH + 46f;
            float row1 = row0 + btnH + gap;
            float midX = col + btnW + gap;

            GUI.enabled = targets.Count > 0;
            if (GUI.Button(new Rect(midX, row0, btnW, btnH), "↑"))
                NodeNudgeService.Nudge(targets, Vector2.up);
            if (GUI.Button(new Rect(col, row1, btnW, btnH), "←"))
                NodeNudgeService.Nudge(targets, Vector2.left);
            if (GUI.Button(new Rect(midX, row1, btnW, btnH), "↓"))
                NodeNudgeService.Nudge(targets, Vector2.down);
            if (GUI.Button(new Rect(midX + btnW + gap, row1, btnW, btnH), "→"))
                NodeNudgeService.Nudge(targets, Vector2.right);
            GUI.enabled = true;

            GUI.Label(new Rect(6f, row1 + btnH + 4f, NudgeW - 12f, LabelH), "Ctrl 细调整 / Shift 粗调整");
        }

        private void StepSlot(int dir, int slotCount)
        {
            if (slotCount <= 0)
                return;
            _connectSlot = Mathf.Clamp(_connectSlot + dir, 0, slotCount - 1);
        }

        private string DescribeSlot(NodeControl control, NodeUI sel)
        {
            if (sel == null)
                return string.Empty;
            if (!NodeSlotInfo.IsOutputEnabled(sel, _connectSlot))
                return "未启用";
            string child = NodeSlotInfo.ChildUidAt(sel, _connectSlot);
            return child == null ? "空" : "现:" + NodeSlotInfo.DisplayName(control, child);
        }

        private void DoConnectOut(NodeControl control, NodeUI sel)
        {
            if (sel == null)
                return;
            LinkResult r = NodeLinkService.ConnectOut(control, sel, _connectSlot, _connectOutText);
            SetConnectResult(r.Ok ? "已连接" : "未连接", r.Message, r.Ok);
        }

        private void DoConnectIn(NodeControl control, NodeUI sel)
        {
            if (sel == null)
                return;
            var results = NodeLinkService.ConnectIn(control, sel, _connectInText);
            var parts = new List<string>(results.Count);
            int okCount = 0;
            for (int i = 0; i < results.Count; i++)
            {
                // 符号后补空格：控制台里 ✘ 会和后面的字黏成一坨
                parts.Add((results[i].Ok ? "✔ " : "✘ ") + results[i].Message);
                if (results[i].Ok)
                    okCount++;
            }
            bool all = okCount == results.Count;
            string status = okCount == 0 ? "未连接" : all ? "已连接" : "未完全连接";
            SetConnectResult(status, string.Join("  ", parts.ToArray()), all);
        }

        // 面板只留短状态，明细进日志
        private void SetConnectResult(string status, string detail, bool ok)
        {
            _connectStatus = status ?? string.Empty;
            _connectStatusAt = Time.unscaledTime;
            if (ok)
                Log.LogInfo($"connect: {detail}");
            else
                Log.LogWarning($"connect: {detail}");
        }

        // 过期后清空：旧提示留在面板上会误导下一次操作
        private string CurrentConnectStatus()
        {
            if (_connectStatusAt < 0f || Time.unscaledTime - _connectStatusAt > ConnectMsgDuration)
                return string.Empty;
            return _connectStatus;
        }

        private static void DrawSolid(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private void DrawFocusHint()
        {
            if (Event.current.type != EventType.Repaint || _focusHintNode == null)
                return;

            float elapsed = Time.unscaledTime - _focusHintStartedAt;
            if (elapsed < 0f || elapsed >= FocusHintPulseDuration + FocusHintHoldDuration)
            {
                _focusHintNode = null;
                return;
            }

            NodeControl control;
            Rect viewport;
            Vector2 point;
            if (!CanvasPanController.TryGetControl(out control)
                || !CanvasPanController.TryGetFocusHintGeometry(control, _focusHintNode, out viewport, out point))
                return;

            point.x = Mathf.Clamp(point.x, viewport.xMin, viewport.xMax);
            point.y = Mathf.Clamp(point.y, viewport.yMin, viewport.yMax);
            Color color = FocusHintColor;
            if (elapsed < FocusHintPulseDuration)
            {
                // 0→3π 完成 1.5 次呼吸，让阶段终点正好落在最亮峰值。
                float pulse = 0.5f - 0.5f * Mathf.Cos(elapsed * Mathf.PI * 3f / FocusHintPulseDuration);
                color.a = Mathf.Lerp(0.25f, 1f, pulse);
            }
            float half = FocusHintThickness * 0.5f;
            DrawSolid(new Rect(viewport.xMin, point.y - half, viewport.width, FocusHintThickness), color);
            DrawSolid(new Rect(point.x - half, viewport.yMin, FocusHintThickness, viewport.height), color);
        }

        private void DrawNodeList(NodeControl control)
        {
            // 文件夹树（读缓存）
            var roots = Folders.SortedRootIds(ListModel, control);
            for (int i = 0; i < roots.Count; i++)
                DrawFolder(roots[i], 0, control);

            // 未分组（读缓存）
            var ungrouped = ListModel.GetUngroupedUserNodes(control, Folders);
            for (int i = 0; i < ungrouped.Count; i++)
                DrawNodeRow(ungrouped[i], 0, false);

            // 系统区
            GUILayout.Space(4f);
            GUILayout.Label("─ 系统 ─");
            foreach (var node in ListModel.EnumerateSystemNodes(control))
                DrawNodeRow(node, 0, true);
        }

        private void DrawFolder(string folderId, int depth, NodeControl control)
        {
            if (!Folders.Folders.TryGetValue(folderId, out var folder))
                return;

            GUILayout.BeginHorizontal();
            GUILayout.Space(depth * 12f);
            if (GUILayout.Button(folder.Expanded ? "▼" : "▶", GUILayout.Width(22f)))
            {
                folder.Expanded = !folder.Expanded;
                ConfigStore.MarkDirty();
            }
            bool selected = ListModel.HighlightFolderId == folderId;
            var prevBg = GUI.backgroundColor;
            var prevContent = GUI.contentColor;
            if (selected)
            {
                GUI.backgroundColor = HighlightBg;
                GUI.contentColor = HighlightText;
            }
            // 与节点行的 [S]/[E]/[H]/[A] 同款；emoji 字形 IMGUI 内置字体没有，显示成方块
            if (GUILayout.Button("[D] " + folder.Name, GUILayout.ExpandWidth(true)))
            {
                ListModel.HighlightFolderId = folderId;
                ListModel.HighlightUid = null;
            }
            GUI.backgroundColor = prevBg;
            GUI.contentColor = prevContent;
            GUILayout.EndHorizontal();

            if (!folder.Expanded)
                return;

            var children = Folders.SortedChildIds(folder, ListModel, control);
            for (int i = 0; i < children.Count; i++)
                DrawFolder(children[i], depth + 1, control);

            var nodes = Folders.SortedFolderNodes(folder, ListModel, control);
            for (int i = 0; i < nodes.Count; i++)
                DrawNodeRow(nodes[i], depth + 1, false);
        }

        private void DrawNodeRow(NodeUI node, int depth, bool system)
        {
            if (node?.nodeBase == null)
                return;
            string uid = node.nodeBase.uid;
            string name = node.nodeBase.name ?? uid;
            string tag = KindTag(node.nodeBase.kind);

            GUILayout.BeginHorizontal();
            GUILayout.Space(depth * 12f);
            if (!system)
            {
                bool check = ListModel.CheckedUids.Contains(uid);
                bool newCheck = GUILayout.Toggle(check, GUIContent.none, GUILayout.Width(18f));
                if (newCheck != check)
                {
                    if (newCheck) ListModel.CheckedUids.Add(uid);
                    else ListModel.CheckedUids.Remove(uid);
                }
            }
            else
            {
                GUILayout.Space(18f);
            }

            bool hi = ListModel.HighlightUid == uid;
            var prevBg = GUI.backgroundColor;
            var prevContent = GUI.contentColor;
            if (hi)
            {
                GUI.backgroundColor = HighlightBg;
                GUI.contentColor = HighlightText;
            }
            if (GUILayout.Button($"{tag} {name}", GUILayout.ExpandWidth(true)))
            {
                ListModel.HighlightUid = uid;
                ListModel.HighlightFolderId = null;
            }
            GUI.backgroundColor = prevBg;
            GUI.contentColor = prevContent;
            GUILayout.EndHorizontal();

            // 搜索导航：Repaint 时用该行在 ScrollView 内容区的 Y 滚过去
            if (_scrollToHighlight && hi && Event.current.type == EventType.Repaint)
            {
                Rect row = GUILayoutUtility.GetLastRect();
                // 略上留一点边，避免贴顶
                _listScroll.y = Mathf.Max(0f, row.y - 8f);
                _scrollToHighlight = false;
            }
        }

        private static string KindTag(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Start: return "[S]";
                case NodeKind.End: return "[E]";
                case NodeKind.H: return "[H]";
                case NodeKind.ADV: return "[A]";
                default: return "[?]";
            }
        }

        // 搜索仅改列表选中 + 列表滚到该行；在文件夹内则展开祖先。不自动挪画布（画布用「定位」）。
        private void StepSearch(int dir)
        {
            NodeControl control;
            if (!CanvasPanController.TryGetControl(out control))
                return;
            var node = ListModel.StepSearch(control, dir);
            if (node?.nodeBase == null)
                return;
            if (Folders.ExpandAncestorsOfNode(node.nodeBase.uid))
                ConfigStore.MarkDirty();
            _scrollToHighlight = true;
        }

        private void FocusHighlight()
        {
            NodeControl control;
            if (!CanvasPanController.TryGetControl(out control))
                return;
            if (string.IsNullOrEmpty(ListModel.HighlightUid))
                return;
            NodeUI node;
            if (!control.dictNode.TryGetValue(ListModel.HighlightUid, out node) || node == null)
                return;
            if (CanvasPanController.FocusNodeUI(control, node))
            {
                _focusHintNode = node;
                _focusHintStartedAt = Time.unscaledTime;
            }
        }

        // 移入当前高亮选中的文件夹（不再单独维护目标夹按钮）。
        private void DoMoveIn()
        {
            if (ListModel.CheckedUids.Count == 0)
                return;
            if (string.IsNullOrEmpty(ListModel.HighlightFolderId)
                || !Folders.Folders.ContainsKey(ListModel.HighlightFolderId))
            {
                Log.LogWarning("移入失败：请先在列表中选中目标文件夹。");
                return;
            }
            Folders.MoveNodesIn(ListModel.CheckedUids.ToList(), ListModel.HighlightFolderId);
            ListModel.MarkDirty();
            ConfigStore.MarkDirty();
        }

        private void DoMoveOut()
        {
            if (ListModel.CheckedUids.Count == 0)
                return;
            Folders.MoveNodesOut(ListModel.CheckedUids.ToList());
            ListModel.MarkDirty();
            ConfigStore.MarkDirty();
        }

        private void ClearSelection()
        {
            ListModel.CheckedUids.Clear();
            ListModel.HighlightUid = null;
            ListModel.HighlightFolderId = null;
            ClearHighlight();
        }

        // 按一次开、再按一次关；两个方向独立。切换后立即生效，不等下一个 0.2s tick。
        private void ToggleLineHighlight(bool input)
        {
            if (input)
                _highlight.InMode = !_highlight.InMode;
            else
                _highlight.OutMode = !_highlight.OutMode;

            if (_highlight.AnyMode)
                SyncHighlight();
            else
                ClearHighlight();
        }

        // 场景 Save 前：ActiveKey 跟随当前标题，内存 Flush 到对应路径（不读盘、不迁旧目录）
        internal void RebindConfigKeyAndFlush()
        {
            ConfigStore.ResolveKey(GetSceneTitle());
            ConfigStore.Flush(ListModel, Folders);
        }

        // 展开面板 / RebuildNodeUI：按当前场景名清内存再读盘
        internal void ReloadConfigFromDisk()
        {
            string key = ConfigStore.ResolveKey(GetSceneTitle());
            ClearHighlight();
            ListModel.CreationOrder.Clear();
            ListModel.SyncCreationStructuresFromOrder();
            ListModel.CheckedUids.Clear();
            Folders.Clear();
            if (!string.IsNullOrEmpty(key))
                ConfigStore.LoadInto(key, ListModel, Folders);
            ListModel.MarkDirty();
            Folders.MarkDirty();
            if (CanvasPanController.TryGetControl(out _))
                RefreshListCaches();
        }

        internal void OnSceneNodesRebuilt()
        {
            if (!CanvasPanController.TryGetControl(out _))
                return;
            ReloadConfigFromDisk();
        }

        private static string GetSceneTitle()
        {
            if (Singleton<HEditData>.IsInstance())
                return Singleton<HEditData>.Instance.info?.title;
            return null;
        }

        private static bool IsNodeEditPage()
        {
            var inst = Instance;
            if (inst == null)
                return false;

            // 场景加载/淡入未完成时不显示（对齐 PosePanel；避免 HEdit 加载途闪按钮）
            if (IsSceneBusy())
                return false;

            // 0.25s 探测一次 + 缓存 NodeSettingCanvas，避免每帧 FindObjectOfType
            float now = Time.unscaledTime;
            if (now < inst._nextNodePageProbeAt && inst._cachedNodeSetting != null)
                return IsCgNodeActive(inst._cachedNodeSetting);

            inst._nextNodePageProbeAt = now + NodePageProbeInterval;

            if (!Singleton<HEditGlobal>.IsInstance())
                return false;
            var global = Singleton<HEditGlobal>.Instance;
            if (global == null || global.nodeControl == null)
                return false;

            try
            {
                if (inst._cachedNodeSetting == null)
                    inst._cachedNodeSetting = UnityEngine.Object.FindObjectOfType<NodeSettingCanvas>();
                return IsCgNodeActive(inst._cachedNodeSetting);
            }
            catch
            {
                inst._cachedNodeSetting = null;
                return false;
            }
        }

        private static bool IsCgNodeActive(NodeSettingCanvas nsc)
        {
            if (nsc == null)
                return false;
            try
            {
                var cg = nsc.CgNode;
                return cg != null && cg.interactable && cg.alpha > 0.01f && cg.gameObject.activeInHierarchy;
            }
            catch
            {
                if (Instance != null)
                    Instance._cachedNodeSetting = null;
                return false;
            }
        }

        // HEdit 场景加载中 / 淡入中 / 非 HEdit 主场景 → 视为未就绪
        // IsNowLoadingFade 已包含 IsNowLoading，不必再 OR 一次
        private static bool IsSceneBusy()
        {
            try
            {
                if (!Singleton<Scene>.IsInstance())
                    return true;
                var sc = Singleton<Scene>.Instance;
                if (sc == null || sc.IsNowLoadingFade)
                    return true;
                string load = sc.LoadSceneName;
                return !string.IsNullOrEmpty(load) && load != "HEditScene";
            }
            catch
            {
                return true;
            }
        }
    }
}
