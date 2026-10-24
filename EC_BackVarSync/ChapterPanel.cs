using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace EC_BackVarSync
{
    /// <summary>
    /// 剧本/章节两级存档管理面板（自绘 IMGUI，参考 EC_LogFilter：固定尺寸深色扁平）。
    /// 一级页：剧本格子（+剧本自动建号并聚焦改名；格角 × 删除，点确认区外撤回）。
    /// 二级页：章节/存档树（+章节/+存档自动建号并聚焦改名；章节与存档均可改名、删除均二步确认）。
    /// 显隐：联动 EC_back 展开/收起，F9 任何状态可开关；位置默认 EC_back 下方，可拖走，F1 可重置。
    /// </summary>
    internal sealed class ChapterPanel
    {
        private const int WindowId = 7801;
        private const float WindowWidth = 400f;
        private const float WindowHeight = 256f;
        private const float TitleH = 26f;
        private const float ToolbarH = 24f;
        private const float FooterH = 24f;
        private const int TreeVisibleRows = 5;
        private const float CellGap = 6f;
        private const string TfPlay = "BVS_tf_play";
        private const string TfName = "BVS_tf_name";
        private const string TfPrefix = "BVS_tf";

        // 显隐状态机：可见 = (锚点有效 && 未被 F9 抑制) || F9 手动显示
        private bool _manualShow;
        private bool _linkSuppressed;
        private bool _prevAnchorActive;
        private bool _lastVisible;

        private Rect _windowRect;
        private Vector2 _normalized = new Vector2(0.03f, 0.18f); // 0~1 可用区域占比，分辨率变化按比例重算
        private int _screenW, _screenH;
        private bool _unfocusRequested;

        private PanelStyles _styles;

        // 一级页：剧本格子
        private readonly List<PlayInfo> _plays = new List<PlayInfo>();
        private float _playGridScroll;
        private string _playNameInput = "";
        private bool _focusPlayInput;

        // 二级页：章节/存档树
        private bool _inDetail;
        private string _currentPlay = "";        // 详情页剧本；返回一级页后作为改名目标
        private string _selectedChapter = "";
        private string _selectedSave = "";       // 选中存档文件名（无扩展名）
        private readonly List<ChapterNode> _chapters = new List<ChapterNode>();
        private readonly HashSet<string> _collapsed = new HashSet<string>();
        private readonly List<TreeRow> _rows = new List<TreeRow>();
        private float _treeScroll;
        private string _nameInput = "";
        private bool _focusNameInput;

        // 删除二步确认（点击确认区外任意位置撤回）
        private string _confirmDeletePlay = "";
        private string _confirmDeleteChapter = "";
        private string _confirmDeleteSave = "";  // "章节/存档"
        private Rect? _confirmRect;              // 本帧确认控件区域（MouseDown 撤回判断用）

        private string _status = "";
        private string _autoTimeCache;
        private string _lastSceneTitle = "";
        private bool _dataLoaded;
        private bool _refreshPending;
        private string _enterOnControl;             // 本事件为回车且落在该控件：绘制前提交（TextField 会吃掉 Return 事件）

        /// <summary>面板任一输入框持有键盘焦点时为 true（Update 侧据此暂停前进键）。</summary>
        internal static bool TextEditing { get; private set; }

        public ChapterPanel(VarStore store, ManualLogSource log, Func<KeyCode> toggleKey,
                            Func<Rect?> getFollowAnchor,
                            ConfigEntry<float> cfgX, ConfigEntry<float> cfgY,
                            ConfigEntry<bool> cfgDock)
        {
            Store = store;
            Log = log;
            ToggleKey = toggleKey;
            AnchorGetter = getFollowAnchor;
            CfgX = cfgX;
            CfgY = cfgY;
            CfgDock = cfgDock;
            _normalized = new Vector2(cfgX.Value, cfgY.Value);
            SyncRect();
        }

        private VarStore Store { get; }
        private ManualLogSource Log { get; }
        private Func<KeyCode> ToggleKey { get; }
        private Func<Rect?> AnchorGetter { get; }
        private ConfigEntry<float> CfgX { get; }
        private ConfigEntry<float> CfgY { get; }
        private ConfigEntry<bool> CfgDock { get; }

        #region 显隐与位置

        public void TickKey()
        {
            if (!Input.GetKeyDown(ToggleKey()))
                return;

            if (_lastVisible)
                HidePanel();
            else
                ShowPanel();
        }

        private void ShowPanel()
        {
            _manualShow = true;
            _linkSuppressed = false;
        }

        private void HidePanel()
        {
            _manualShow = false;
            _linkSuppressed = true;
            _unfocusRequested = true;
        }

        // 恢复停靠：F1 插件管理里把 DockUnderECBack 重置/改回 true 即可（ConfigurationManager 每项自带重置）

        #endregion

        public bool OnGUI()
        {
            if (_unfocusRequested)
            {
                GUI.UnfocusWindow();
                _unfocusRequested = false;
            }

            Rect? anchor = AnchorGetter?.Invoke();
            bool anchorNow = anchor.HasValue;
            if (anchorNow && !_prevAnchorActive)
                _linkSuppressed = false;
            _prevAnchorActive = anchorNow;

            bool visible = (anchorNow && !_linkSuppressed) || _manualShow;

            if (visible && !_lastVisible)
            {
                _autoTimeCache = Store.GetAutoSaveTime(VarStore.GetSceneTitle());
                if (CfgDock.Value && anchorNow)
                {
                    _windowRect.x = anchor.Value.x;
                    _windowRect.y = anchor.Value.yMax + 4f;
                    _normalized = new Vector2(
                        _windowRect.x / Mathf.Max(1f, Screen.width - _windowRect.width),
                        _windowRect.y / Mathf.Max(1f, Screen.height - _windowRect.height));
                }
            }
            _lastVisible = visible;

            if (!visible)
            {
                TextEditing = false;
                return false;
            }

            string sceneTitle = VarStore.GetSceneTitle();
            if (sceneTitle != _lastSceneTitle || !_dataLoaded)
            {
                _lastSceneTitle = sceneTitle;
                _dataLoaded = true;
                RefreshAll();
            }

            if (_styles == null)
                _styles = new PanelStyles();

            if (_focusPlayInput)
            {
                GUI.FocusControl(TfPlay);
                _focusPlayInput = false;
            }
            else if (_focusNameInput)
            {
                GUI.FocusControl(TfName);
                _focusNameInput = false;
            }

            Vector2 before = _windowRect.position;
            if (_screenW != Screen.width || _screenH != Screen.height)
                SyncRect(); // 分辨率变化：按归一化位置重算，天然防溢出
            Color prevColor = GUI.color;
            Color prevBg = GUI.backgroundColor;
            Color prevContent = GUI.contentColor;
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
            try
            {
                _windowRect = GUI.Window(WindowId, _windowRect, DrawContents, string.Empty, _styles.Window);
            }
            finally
            {
                GUI.color = prevColor;
                GUI.backgroundColor = prevBg;
                GUI.contentColor = prevContent;
            }

            _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Mathf.Max(0f, Screen.width - _windowRect.width));
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Mathf.Max(0f, Screen.height - _windowRect.height));
            if (_windowRect.position != before)
            {
                // 拖动即永久放弃自动停靠；位置按可用区域百分比持久化（分辨率无关）
                CfgDock.Value = false;
                _normalized = new Vector2(
                    _windowRect.x / Mathf.Max(1f, Screen.width - _windowRect.width),
                    _windowRect.y / Mathf.Max(1f, Screen.height - _windowRect.height));
                CfgX.Value = Mathf.Clamp01(_normalized.x);
                CfgY.Value = Mathf.Clamp01(_normalized.y);
            }

            if (Event.current.type == EventType.MouseDown
                && !_windowRect.Contains(Event.current.mousePosition)
                && GUIUtility.keyboardControl != 0)
            {
                _unfocusRequested = true;
            }

            IMGUIInputBlocker.SetArea(_windowRect);
            TextEditing = GUI.GetNameOfFocusedControl().StartsWith(TfPrefix);
            return true;
        }

        #region 窗口骨架

        private void DrawContents(int id)
        {
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
            // 自绘浅色输入框下光标可能继承皮肤设置导致不可见，这里显式补回
            GUI.skin.settings.cursorColor = Color.black;

            // 点击确认区以外任意位置撤回删除确认（用上一帧的确认区域判断）
            if (Event.current.type == EventType.MouseDown
                && (!_confirmRect.HasValue || !_confirmRect.Value.Contains(Event.current.mousePosition)))
            {
                _confirmDeletePlay = "";
                _confirmDeleteChapter = "";
                _confirmDeleteSave = "";
            }
            _confirmRect = null;

            // 捕获回车提交：TextField 的文本编辑器会消费 Return 事件，必须在绘制输入框前处理
            _enterOnControl = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                ? GUI.GetNameOfFocusedControl()
                : null;

            float w = _windowRect.width;
            float h = _windowRect.height;

            PanelStyles.Band(new Rect(0f, 0f, w, h), PanelStyles.BgColor);
            PanelStyles.Band(new Rect(0f, 0f, w, TitleH), PanelStyles.TitleBgColor);

            DrawTitleBar(w);

            float bodyTop = TitleH + ToolbarH + 6f;
            float bodyH = h - bodyTop - FooterH - 6f;
            if (!_inDetail)
            {
                DrawPlayToolbar(new Rect(6f, TitleH + 3f, w - 12f, ToolbarH));
                DrawPlayGrid(new Rect(6f, bodyTop, w - 12f, bodyH));
                DrawFooterPlays(new Rect(6f, h - FooterH - 3f, w - 12f, FooterH));
            }
            else
            {
                DrawDetailToolbar(new Rect(6f, TitleH + 3f, w - 12f, ToolbarH));
                DrawTree(new Rect(6f, bodyTop, w - 12f, bodyH));
                DrawFooterDetail(new Rect(6f, h - FooterH - 3f, w - 12f, FooterH));
            }

            if (_refreshPending)
            {
                _refreshPending = false;
                RefreshAll();
            }
        }

        private void DrawTitleBar(float w)
        {
            if (_inDetail)
            {
                if (Btn(new Rect(4f, 3f, 24f, TitleH - 6f), "←", null))
                    BackToPlays();
                GUI.Label(new Rect(32f, 2f, w - 128f, TitleH - 4f), _currentPlay, _styles.Title);
            }
            else
            {
                GUI.Label(new Rect(8f, 2f, w - 100f, TitleH - 4f), "EC_BackVarSync 剧本 (F9)", _styles.Title);
            }
            if (Btn(new Rect(w - 26f, 3f, 22f, TitleH - 6f), "X", null))
                HidePanel();
            GUI.DragWindow(new Rect(0f, 0f, w - 30f, TitleH));
        }

        #endregion

        #region 一级页：剧本格子

        private void DrawPlayToolbar(Rect area)
        {
            float x = area.x;
            GUI.Label(new Rect(x, area.y, 56f, area.height), "剧本名称", _styles.Muted);
            x += 58f;
            TryCommitEnter(TfPlay, RenamePlayFromInput);
            GUI.SetNextControlName(TfPlay);
            _playNameInput = GUI.TextField(new Rect(x, area.y + 1f, 150f, area.height - 2f), _playNameInput, 40, _styles.Text);
            x += 154f;
            GUI.enabled = _currentPlay.Length > 0 && _playNameInput != _currentPlay;
            if (Btn(new Rect(x, area.y, 44f, area.height), "改名", RenamePlayFromInput))
            { }
            GUI.enabled = true;

            // +剧本 放最右侧
            if (Btn(new Rect(area.xMax - 48f, area.y, 48f, area.height), "+剧本", CreatePlayAuto))
            { }
        }

        private void RenamePlayFromInput()
        {
            if (_currentPlay.Length == 0)
            {
                SetStatus("先点击一个剧本格子进入过，再改名。");
                return;
            }
            if (_playNameInput == _currentPlay)
                return;

            if (Store.RenamePlay(_currentPlay, _playNameInput, out string renamed))
            {
                _currentPlay = renamed;
                _refreshPending = true;
                SetStatus($"已改名为: {renamed}");
            }
            else
                SetStatus("改名失败：名称无效或已存在。");
        }

        private void CreatePlayAuto()
        {
            string name = NextAutoName("剧本", ExistingPlayNames());
            if (Store.CreatePlay(name, out string sanitized))
            {
                _currentPlay = sanitized;   // 作为改名目标
                _playNameInput = sanitized;
                _focusPlayInput = true;
                _refreshPending = true;
                SetStatus($"已创建 {sanitized}，输入框可改名。");
            }
            else
                SetStatus("创建失败，详见日志。");
        }

        private void DrawPlayGrid(Rect area)
        {
            const int cols = 2;
            const int rows = 3;
            int totalCells = _plays.Count + 1;
            int totalRows = (totalCells + cols - 1) / cols;
            bool hasScroll = totalRows > rows;

            // 滚动条占独立列，避免压住右列格子
            float gridWidth = hasScroll ? area.width - 16f : area.width;
            float cellW = (gridWidth - CellGap) / cols;
            float cellH = (area.height - CellGap * (rows - 1)) / rows;

            if (Event.current.type == EventType.ScrollWheel && area.Contains(Event.current.mousePosition))
            {
                _playGridScroll += Event.current.delta.y * 0.1f;
                Event.current.Use();
            }
            _playGridScroll = Mathf.Clamp(_playGridScroll, 0f, Math.Max(0f, totalRows - rows));
            if (hasScroll)
            {
                _playGridScroll = GUI.VerticalSlider(
                    new Rect(area.xMax - 12f, area.y + 2f, 10f, area.height - 4f),
                    _playGridScroll, 0f, totalRows - rows, _styles.Slider, _styles.Thumb);
            }

            int firstRow = Mathf.FloorToInt(_playGridScroll);
            for (int row = firstRow; row < Math.Min(firstRow + rows, totalRows); row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    int index = row * cols + col;
                    if (index >= totalCells)
                        break;

                    Rect cell = new Rect(
                        area.x + col * (cellW + CellGap),
                        area.y + (row - firstRow) * (cellH + CellGap),
                        cellW, cellH);

                    if (index < _plays.Count)
                        DrawPlayCell(_plays[index], cell);
                    else
                        DrawAddPlayCell(cell);
                }
            }
        }

        private void DrawPlayCell(PlayInfo info, Rect cell)
        {
            bool confirming = info.Name == _confirmDeletePlay;
            // 改名中的剧本格琥珀色高亮
            bool renaming = info.Name == _currentPlay && GUI.GetNameOfFocusedControl() == TfPlay;
            Color bg = confirming ? PanelStyles.DangerColor
                : renaming ? PanelStyles.RenameColor
                : PanelStyles.CellColor;
            PanelStyles.Band(cell, bg);

            if (confirming)
            {
                _confirmRect = cell;
                if (Btn(new Rect(cell.x + 6f, cell.y, cell.width - 12f, cell.height), "确认删除？(含章节/存档)", null, _styles.CellDanger))
                {
                    if (Store.DeletePlay(info.Name))
                    {
                        _confirmDeletePlay = "";
                        if (_currentPlay == info.Name)
                        {
                            _currentPlay = "";
                            _playNameInput = "";
                            BackToPlays();
                        }
                        _refreshPending = true;
                        SetStatus($"剧本已删除: {info.Name}");
                    }
                    else
                        SetStatus("删除失败，详见日志。");
                }
                return;
            }

            if (Btn(new Rect(cell.x + 4f, cell.y + 4f, cell.width - 46f, cell.height - 8f),
                    $"{info.Name}\n{info.ChapterCount} 章", null, _styles.Cell))
            {
                EnterPlay(info.Name);
            }
            // ✎ 快速改名（免进剧本再返回）
            if (Btn(new Rect(cell.xMax - 40f, cell.y + 2f, 18f, 18f), "✎", null, _styles.Tiny))
            {
                _currentPlay = info.Name;
                _playNameInput = info.Name;
                _focusPlayInput = true;
            }
            if (Btn(new Rect(cell.xMax - 20f, cell.y + 2f, 18f, 18f), "×", null, _styles.Tiny))
                _confirmDeletePlay = info.Name;
        }

        private void DrawAddPlayCell(Rect cell)
        {
            PanelStyles.Band(cell, PanelStyles.CellColor);
            if (Btn(new Rect(cell.x + 4f, cell.y + 4f, cell.width - 8f, cell.height - 8f), "+ 新剧本", null, _styles.Cell))
                CreatePlayAuto();
        }

        private void DrawFooterPlays(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y, area.width - 12f, area.height),
                string.IsNullOrEmpty(_status) ? $"共 {_plays.Count} 个剧本" : _status, _styles.Muted);
        }

        #endregion

        #region 二级页：章节/存档树

        private void DrawDetailToolbar(Rect area)
        {
            float x = area.x;
            GUI.Label(new Rect(x, area.y, 34f, area.height), "名称", _styles.Muted);
            x += 36f;
            TryCommitEnter(TfName, RenameSelectedFromInput);
            GUI.SetNextControlName(TfName);
            _nameInput = GUI.TextField(new Rect(x, area.y + 1f, 150f, area.height - 2f), _nameInput, 40, _styles.Text);
            x += 154f;
            GUI.enabled = CanRenameSelected();
            if (Btn(new Rect(x, area.y, 44f, area.height), "改名", RenameSelectedFromInput))
            { }
            GUI.enabled = true;

            // +章节 / +存档 放最右侧，与改名隔开
            if (Btn(new Rect(area.xMax - 100f, area.y, 46f, area.height), "+章节", CreateChapterAuto))
            { }
            if (Btn(new Rect(area.xMax - 50f, area.y, 50f, area.height), "+存档", CreateSaveAuto))
            { }
        }

        private bool CanRenameSelected()
        {
            if (_selectedSave.Length > 0)
                return _nameInput != _selectedSave;
            if (_selectedChapter.Length > 0)
                return _nameInput != _selectedChapter;
            return false;
        }

        private void RenameSelectedFromInput()
        {
            if (_selectedSave.Length > 0)
            {
                if (Store.RenameSave(_currentPlay, _selectedChapter, _selectedSave, _nameInput, out string renamed))
                {
                    _selectedSave = renamed;
                    _nameInput = renamed;
                    _refreshPending = true;
                    SetStatus($"存档已改名: {renamed}");
                }
                else
                    SetStatus("存档改名失败：名称无效或已存在。");
                return;
            }

            if (_selectedChapter.Length > 0)
            {
                if (Store.RenameChapter(_currentPlay, _selectedChapter, _nameInput, out string renamed))
                {
                    _selectedChapter = renamed;
                    _nameInput = renamed;
                    _refreshPending = true;
                    SetStatus($"章节已改名: {renamed}");
                }
                else
                    SetStatus("章节改名失败：名称无效或已存在。");
                return;
            }

            SetStatus("先点选章节或存档再改名。");
        }

        private void CreateChapterAuto()
        {
            string name = NextAutoName("章节", ExistingChapterNames());
            if (Store.CreateChapter(_currentPlay, name, out string sanitized))
            {
                _selectedChapter = sanitized;
                _selectedSave = "";
                _nameInput = sanitized;
                _focusNameInput = true;
                _refreshPending = true;
                SetStatus($"已创建 {sanitized}，输入框可改名。");
            }
            else
                SetStatus("章节创建失败，详见日志。");
        }

        private void CreateSaveAuto()
        {
            string chapter = _selectedChapter.Length > 0 ? _selectedChapter : ChapterOfSelectedSave();
            if (chapter.Length == 0)
            {
                SetStatus("先点选一个章节，再 +存档。");
                return;
            }

            string name = NextAutoName("存档", ExistingSaveNames(chapter));
            VarStore.TryGetCurrentPosition(out string partUid, out string partName, out int cutIndex);
            if (Store.SaveChapter(_currentPlay, chapter, name,
                VarStore.GetSceneTitle(), partUid, partName, cutIndex, out string fileName))
            {
                _selectedChapter = chapter;
                _selectedSave = fileName;
                _nameInput = fileName;
                _focusNameInput = true;
                _refreshPending = true;
                SetStatus($"已存档 {fileName}（当前变量），输入框可改名。");
            }
            else
                SetStatus("存档失败（变量快照或写盘失败，详见日志）。");
        }

        private void DrawTree(Rect area)
        {
            PanelStyles.Band(area, PanelStyles.TreeBgColor);

            if (_rows.Count == 0)
            {
                GUI.Label(new Rect(area.x + 6f, area.y + 4f, area.width - 12f, 30f),
                    "暂无章节。点右上 +章节 创建。", _styles.Muted);
                return;
            }

            // 行高按可见 5 行铺满，行内垂直居中
            float rowH = area.height / TreeVisibleRows;
            int visibleRows = Math.Min(TreeVisibleRows, _rows.Count);
            int maxScroll = Math.Max(0, _rows.Count - TreeVisibleRows);
            if (Event.current.type == EventType.ScrollWheel && area.Contains(Event.current.mousePosition))
            {
                _treeScroll += Event.current.delta.y * 0.3f;
                Event.current.Use();
            }
            _treeScroll = Mathf.Clamp(_treeScroll, 0f, maxScroll);
            if (maxScroll > 0)
            {
                _treeScroll = GUI.VerticalSlider(
                    new Rect(area.xMax - 12f, area.y + 2f, 10f, area.height - 4f),
                    _treeScroll, 0f, maxScroll, _styles.Slider, _styles.Thumb);
            }

            int first = Mathf.FloorToInt(_treeScroll);
            int end = Math.Min(first + visibleRows, _rows.Count);
            for (int i = first; i < end; i++)
                DrawTreeRow(_rows[i], new Rect(area.x + 2f, area.y + (i - first) * rowH, area.width - 16f, rowH - 1f));
        }

        private void DrawTreeRow(TreeRow row, Rect area)
        {
            if (row.Kind == TreeRowKind.Chapter)
            {
                DrawChapterRow(row, area);
                return;
            }
            DrawSaveRow(row, area);
        }

        private void DrawChapterRow(TreeRow row, Rect area)
        {
            bool selected = row.Chapter == _selectedChapter && _selectedSave.Length == 0;
            if (selected)
                PanelStyles.Band(area, PanelStyles.SelectedColor);

            bool confirming = row.Chapter == _confirmDeleteChapter;
            if (confirming)
            {
                _confirmRect = area;
                PanelStyles.Band(area, PanelStyles.DangerColor);
            }

            bool expanded = !_collapsed.Contains(row.Chapter);
            if (Btn(new Rect(area.x, area.y + area.height / 2f - 10f, 16f, 20f), expanded ? "▼" : "▶", null, _styles.Tiny))
                ToggleCollapse(row.Chapter);

            string label = confirming ? $"{row.Chapter}  确认删除？(含存档)" : row.Chapter;
            if (Btn(new Rect(area.x + 20f, area.y, area.width - 42f, area.height), label, null, _styles.Chapter))
            {
                if (confirming)
                {
                    if (Store.DeleteChapter(_currentPlay, row.Chapter))
                    {
                        _confirmDeleteChapter = "";
                        if (_selectedChapter == row.Chapter)
                        {
                            _selectedChapter = "";
                            _selectedSave = "";
                            _nameInput = "";
                        }
                        _refreshPending = true;
                        SetStatus($"章节已删除: {row.Chapter}");
                    }
                    else
                        SetStatus("删除失败，详见日志。");
                }
                else
                {
                    _selectedChapter = row.Chapter;
                    _selectedSave = "";
                    _nameInput = row.Chapter;
                }
            }

            if (Btn(new Rect(area.xMax - 20f, area.y + 2f, 18f, area.height - 4f), "×", null, _styles.Tiny))
                _confirmDeleteChapter = confirming ? "" : row.Chapter;
        }

        private void DrawSaveRow(TreeRow row, Rect area)
        {
            string key = row.Chapter + "/" + row.Entry.FileName;
            bool selected = key == _selectedChapter + "/" + _selectedSave;
            bool confirming = key == _confirmDeleteSave;
            if (selected)
                PanelStyles.Band(area, PanelStyles.SelectedColor);
            if (confirming)
            {
                _confirmRect = area;
                PanelStyles.Band(area, PanelStyles.DangerColor);
            }

            // 行主体：点选存档（改名目标）
            if (Btn(new Rect(area.x + 18f, area.y, area.width - 146f, area.height), null,
                    () => { }, _styles.Row))
            {
                _selectedChapter = row.Chapter;
                _selectedSave = row.Entry.FileName;
                _nameInput = row.Entry.FileName;
            }
            GUI.Label(new Rect(area.x + 22f, area.y, area.width - 158f, area.height),
                "· " + row.Entry.FileName + (confirming ? "  确认删除？" : ""), _styles.Label);
            GUI.Label(new Rect(area.xMax - 144f, area.y, 78f, area.height),
                ShortTime(row.Entry.TimeLocal), _styles.Muted);

            if (Btn(new Rect(area.xMax - 62f, area.y + 2f, 36f, area.height - 4f), "读取",
                    () => LoadSave(row)))
            { }

            if (Btn(new Rect(area.xMax - 22f, area.y + 2f, 20f, area.height - 4f), confirming ? "✓" : "删",
                    () =>
                    {
                        if (!confirming)
                            return;
                        if (Store.DeleteChapterSave(_currentPlay, row.Chapter, row.Entry.FileName))
                        {
                            _confirmDeleteSave = "";
                            if (_selectedSave == row.Entry.FileName)
                            {
                                _selectedSave = "";
                                _nameInput = "";
                            }
                            _refreshPending = true;
                            SetStatus($"存档已删除: {row.Entry.FileName}");
                        }
                        else
                            SetStatus("删除失败，详见日志。");
                    }))
            {
                if (!confirming)
                    _confirmDeleteSave = key;
            }
        }

        private void DrawFooterDetail(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y, area.width - 100f, area.height), _status, _styles.Muted);
            Btn(new Rect(area.xMax - 90f, area.y, 90f, area.height), "读取自动存档", () =>
            {
                string sceneTitle = VarStore.GetSceneTitle();
                if (Store.RestoreAuto(sceneTitle))
                {
                    SetStatus("自动存档已读取（变量已恢复）。");
                    Log.LogMessage($"自动存档已读取（场景: {sceneTitle}）");
                }
                else
                    SetStatus("自动存档读取失败或不存在。");
            });
        }

        #endregion

        #region 操作与数据

        /// <summary>按钮统一入口：点击后登记焦点释放，防 InputHotkeyBlock 拦截 F9。</summary>
        private bool Btn(Rect rect, string label, Action onClick, GUIStyle style = null)
        {
            if (!GUI.Button(rect, label, style ?? _styles.Button))
                return false;

            _unfocusRequested = true;
            onClick?.Invoke();
            return true;
        }

        /// <summary>回车提交：在绘制输入框之前消费 Return 事件并执行改名。</summary>
        private void TryCommitEnter(string controlName, Action commit)
        {
            if (_enterOnControl != controlName)
                return;

            _enterOnControl = null;
            Event.current.Use();
            _unfocusRequested = true;
            commit();
        }

        private void EnterPlay(string play)
        {
            _inDetail = true;
            _currentPlay = play;
            _playNameInput = play;
            _selectedChapter = "";
            _selectedSave = "";
            _nameInput = "";
            _confirmDeleteChapter = "";
            _confirmDeleteSave = "";
            _refreshPending = true;
        }

        private void BackToPlays()
        {
            _inDetail = false;
            _selectedChapter = "";
            _selectedSave = "";
            _confirmDeleteChapter = "";
            _confirmDeleteSave = "";
            _refreshPending = true;
        }

        private void ToggleCollapse(string chapter)
        {
            if (!_collapsed.Add(chapter))
                _collapsed.Remove(chapter);
            _refreshPending = true;
        }

        private void LoadSave(TreeRow row)
        {
            if (Store.LoadChapterSave(_currentPlay, row.Chapter, row.Entry.FileName))
            {
                _selectedChapter = row.Chapter;
                _selectedSave = row.Entry.FileName;
                _nameInput = row.Entry.FileName;
                SetStatus($"已读取: {row.Entry.FileName}（变量已恢复）");
                Log.LogMessage($"章节存档已读取: {_currentPlay}/{row.Chapter}/{row.Entry.FileName}");
            }
            else
                SetStatus($"读取失败: {row.Entry.FileName}");
        }

        private void RefreshAll()
        {
            _plays.Clear();
            foreach (string play in Store.ListPlays())
                _plays.Add(new PlayInfo(play, Store.ListChapters(play).Count));
            _plays.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            if (_confirmDeletePlay.Length > 0 && !HasPlay(_confirmDeletePlay))
                _confirmDeletePlay = "";

            if (_inDetail && _currentPlay.Length > 0)
            {
                if (!HasPlay(_currentPlay))
                {
                    _currentPlay = "";
                    _playNameInput = "";
                    BackToPlays();
                }
                else
                {
                    _chapters.Clear();
                    foreach (string chapter in Store.ListChapters(_currentPlay))
                        _chapters.Add(new ChapterNode(chapter, Store.ListSaves(_currentPlay, chapter)));

                    if (_selectedChapter.Length > 0 && !HasChapter(_selectedChapter))
                    {
                        _selectedChapter = "";
                        _selectedSave = "";
                        _nameInput = "";
                    }
                    if (_selectedSave.Length > 0 && !HasSave(_selectedChapter, _selectedSave))
                    {
                        _selectedSave = "";
                        _nameInput = "";
                    }
                    RebuildRows();
                }
            }

            _autoTimeCache = Store.GetAutoSaveTime(VarStore.GetSceneTitle());
        }

        private bool HasPlay(string play)
        {
            foreach (PlayInfo info in _plays)
                if (info.Name == play)
                    return true;
            return false;
        }

        private bool HasChapter(string chapter)
        {
            foreach (ChapterNode node in _chapters)
                if (node.Name == chapter)
                    return true;
            return false;
        }

        private bool HasSave(string chapter, string saveName)
        {
            foreach (ChapterNode node in _chapters)
                if (node.Name == chapter)
                    foreach (SaveEntry entry in node.Saves)
                        if (entry.FileName == saveName)
                            return true;
            return false;
        }

        private void RebuildRows()
        {
            _rows.Clear();
            foreach (ChapterNode chapter in _chapters)
            {
                _rows.Add(new TreeRow { Kind = TreeRowKind.Chapter, Chapter = chapter.Name });
                if (_collapsed.Contains(chapter.Name))
                    continue;

                foreach (SaveEntry entry in chapter.Saves)
                    _rows.Add(new TreeRow { Kind = TreeRowKind.Save, Chapter = chapter.Name, Entry = entry });
            }
            _treeScroll = Mathf.Clamp(_treeScroll, 0f, Math.Max(0f, _rows.Count - 1));
        }

        private IEnumerable<string> ExistingPlayNames()
        {
            foreach (PlayInfo info in _plays)
                yield return info.Name;
        }

        private IEnumerable<string> ExistingChapterNames()
        {
            foreach (ChapterNode node in _chapters)
                yield return node.Name;
        }

        private IEnumerable<string> ExistingSaveNames(string chapter)
        {
            foreach (ChapterNode node in _chapters)
                if (node.Name == chapter)
                    foreach (SaveEntry entry in node.Saves)
                        yield return entry.FileName;
        }

        private string ChapterOfSelectedSave()
        {
            if (_selectedSave.Length == 0)
                return "";
            foreach (ChapterNode node in _chapters)
                foreach (SaveEntry entry in node.Saves)
                    if (entry.FileName == _selectedSave)
                        return node.Name;
            return "";
        }

        /// <summary>自动编号：前缀 + 最小可用序号（剧本1/章节2/存档3）。</summary>
        private static string NextAutoName(string prefix, IEnumerable<string> existing)
        {
            var used = new HashSet<string>(existing);
            for (int i = 1; i < 1000; i++)
            {
                string candidate = prefix + i;
                if (!used.Contains(candidate))
                    return candidate;
            }
            return prefix + DateTime.Now.ToString("HHmmss");
        }

        private static string ShortTime(string fullTime)
        {
            return fullTime != null && fullTime.Length > 16 ? fullTime.Substring(5, 11) : (fullTime ?? "");
        }

        private void SetStatus(string text)
        {
            _status = text;
        }

        /// <summary>按归一化位置与当前分辨率重算窗口矩形（对齐 EC_LogFilter 的防溢出方案）。</summary>
        private void SyncRect()
        {
            _screenW = Screen.width;
            _screenH = Screen.height;
            float width = Mathf.Min(WindowWidth, _screenW);
            float height = Mathf.Min(WindowHeight, _screenH);
            _windowRect = new Rect(
                (_screenW - width) * Mathf.Clamp01(_normalized.x),
                (_screenH - height) * Mathf.Clamp01(_normalized.y),
                width, height);
        }

        #endregion

        private enum TreeRowKind { Chapter, Save }

        private sealed class TreeRow
        {
            public TreeRowKind Kind;
            public string Chapter;
            public SaveEntry Entry;
        }

        private sealed class PlayInfo
        {
            public readonly string Name;
            public readonly int ChapterCount;
            public PlayInfo(string name, int chapterCount) { Name = name; ChapterCount = chapterCount; }
        }

        private sealed class ChapterNode
        {
            public readonly string Name;
            public readonly List<SaveEntry> Saves;
            public ChapterNode(string name, List<SaveEntry> saves) { Name = name; Saves = saves; }
        }

        /// <summary>扁平深色自绘样式（参考 EC_LogFilter.WindowStyles）。</summary>
        private sealed class PanelStyles
        {
            internal static readonly Color BgColor = new Color(0.09f, 0.11f, 0.14f, 1f);
            internal static readonly Color TitleBgColor = new Color(0.15f, 0.19f, 0.25f, 1f);
            internal static readonly Color TreeBgColor = new Color(0.12f, 0.14f, 0.18f, 1f);
            internal static readonly Color SelectedColor = new Color(0.19f, 0.30f, 0.43f, 1f);
            internal static readonly Color CellColor = new Color(0.15f, 0.18f, 0.24f, 1f);
            internal static readonly Color DangerColor = new Color(0.32f, 0.13f, 0.13f, 1f);
            internal static readonly Color RenameColor = new Color(0.34f, 0.27f, 0.11f, 1f);

            internal readonly GUIStyle Window = new GUIStyle();
            internal readonly GUIStyle Title;
            internal readonly GUIStyle Label;
            internal readonly GUIStyle Muted;
            internal readonly GUIStyle Button;
            internal readonly GUIStyle Row;
            internal readonly GUIStyle Chapter;
            internal readonly GUIStyle Text;
            internal readonly GUIStyle Tiny;
            internal readonly GUIStyle Cell;
            internal readonly GUIStyle CellDanger;
            internal readonly GUIStyle Slider = new GUIStyle();
            internal readonly GUIStyle Thumb = new GUIStyle();

            internal PanelStyles()
            {
                Label = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = false,
                    clipping = TextClipping.Clip,
                    padding = new RectOffset(3, 3, 0, 0)
                };
                Paint(Label, false);
                Muted = new GUIStyle(Label)
                {
                    normal = { textColor = new Color(0.62f, 0.67f, 0.74f) },
                    hover = { textColor = new Color(0.62f, 0.67f, 0.74f) }
                };
                Title = new GUIStyle(Label) { fontSize = 13, fontStyle = FontStyle.Bold };
                Row = new GUIStyle(Label) { hover = { textColor = new Color(1f, 0.85f, 0.4f) } };
                Chapter = new GUIStyle(Row) { fontSize = 14 };
                Cell = new GUIStyle(Label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                    hover = { textColor = new Color(1f, 0.85f, 0.4f) }
                };
                CellDanger = new GUIStyle(Cell)
                {
                    normal = { textColor = new Color(1f, 0.62f, 0.55f) },
                    hover = { textColor = new Color(1f, 0.62f, 0.55f) }
                };
                Button = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 12,
                    alignment = TextAnchor.MiddleCenter,
                    padding = new RectOffset(2, 2, 0, 0),
                    border = new RectOffset()
                };
                Paint(Button, true);
                // 输入框底用浅灰而非纯白：光标颜色继承全局皮肤，浅灰底保证黑/白光标均可见
                Text = new GUIStyle(GUI.skin.textField) { fontSize = 12 };
                Paint(Text, true, new Color(0.90f, 0.91f, 0.93f));
                Tiny = new GUIStyle(Button) { fontSize = 10, padding = new RectOffset(0, 0, 0, 0) };
                Thumb.fixedHeight = 14f;
                Paint(Thumb, true);
            }

            private static void Paint(GUIStyle style, bool background, Color? bgTint = null)
            {
                foreach (GUIStyleState state in new[]
                {
                    style.normal, style.hover, style.active, style.focused,
                    style.onNormal, style.onHover, style.onActive, style.onFocused
                })
                {
                    state.background = background ? Texture2D.whiteTexture : null;
                    state.textColor = background
                        ? new Color(0.10f, 0.13f, 0.18f)
                        : new Color(0.92f, 0.94f, 0.97f);
                }
                if (bgTint.HasValue)
                {
                    style.normal.background = MakeTexture(bgTint.Value);
                    style.focused.background = MakeTexture(bgTint.Value);
                }
            }

            private static Texture2D MakeTexture(Color color)
            {
                var tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, color);
                tex.Apply();
                return tex;
            }

            internal static void Band(Rect rect, Color color)
            {
                Color previous = GUI.color;
                GUI.color = color;
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = previous;
            }
        }
    }
}
