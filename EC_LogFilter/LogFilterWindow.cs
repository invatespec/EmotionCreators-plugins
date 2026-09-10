using System;
using BepInEx.Logging;
using UnityEngine;

namespace EC_LogFilter
{
    internal sealed class LogFilterWindow : IDisposable
    {
        private const int WindowId = 0x454C46;
        private const string FilterControl = "EC_LogFilter.SourceFilter";
        private const float RowHeight = 25;
        private static readonly string[] SortNames = { "累计", "已屏蔽", "名称" };
        private static readonly string[] SelectedSortNames = { "[累计]", "[已屏蔽]", "[名称]" };
        private static readonly string[] AllowedLabels = { "[ ] Debug", "[ ] Info", "[ ] Message", "[ ] Warning", "[ ] Error", "[ ] Fatal" };
        private static readonly string[] BlockedLabels = { "[x] Debug", "[x] Info", "[x] Message", "[x] Warning", "[x] Error", "[x] Fatal" };
        private readonly LogStatistics _statistics;
        private readonly RuleStore _store;
        private readonly LogListModel _model;
        private readonly GUIContent _titleContent = new GUIContent();
        private readonly GUIContent _saveContent = new GUIContent();
        private readonly GUIContent _faultContent = new GUIContent();
        private readonly GUIContent _errorContent = new GUIContent();
        private readonly GUI.WindowFunction _drawContents;
        private WindowStyles _styles;
        private Rect _rect;
        private Rect _listRect;
        private Vector2 _normalized = new Vector2(0.5f, 0.3f);
        private int _screenWidth;
        private int _screenHeight;
        private bool _positionInvalid = true;
        private bool _moved;
        private bool _positionPending;
        private bool _mouseGesture;
        private bool _clearFocus;
        private string _pressedSource;
        private string _shortcutText;
        private float _scrollRow;
        private double _now;

        internal LogFilterWindow(LogStatistics statistics, RuleStore store)
        {
            _statistics = statistics;
            _store = store;
            _model = new LogListModel(statistics);
            _drawContents = DrawContents;
            ShortcutText = "Ctrl+Alt+L";
        }

        internal bool Visible { get; private set; }
        internal bool TextFocused { get; private set; }
        internal string ShortcutText
        {
            get => _shortcutText;
            set
            {
                _shortcutText = value;
                _titleContent.text = "EC_LogFilter  日志统计与屏蔽  [" + value + "]";
            }
        }
        internal string FaultText { get; set; } = string.Empty;

        internal void SetVisible(bool visible)
        {
            if (Visible == visible) return;
            Visible = visible;
            if (visible) _model.RequestRefresh();
            else ReleaseInput();
        }

        internal void ReleaseInput()
        {
            if (TextFocused) _clearFocus = true;
            TextFocused = false;
            _mouseGesture = false;
            _pressedSource = null;
        }

        internal void SetPosition(float x, float y)
        {
            _normalized = new Vector2(Unit(x, 0.5f), Unit(y, 0.3f));
            _positionInvalid = true;
            _moved = false;
            _positionPending = false;
        }

        internal bool TryTakePosition(out Vector2 position)
        {
            position = _normalized;
            bool pending = _positionPending;
            _positionPending = false;
            return pending;
        }

        internal bool ContainsMouse(Vector2 point) => Visible && _rect.Contains(point);
        internal bool BlocksMouse(Vector2 point) => Visible && (_mouseGesture || _rect.Contains(point));
        internal bool WantsMouseInput() => Visible && BlocksMouse(MousePosition());

        internal void Update(double now)
        {
            _now = now;
            if (!Visible) return;
            SyncRect();
            bool pressed = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            if (!pressed)
            {
                _mouseGesture = false;
                FinishMove();
            }
            Vector2 localMouse = MousePosition() - _rect.position;
            _model.Update(now, _mouseGesture || _listRect.Contains(localMouse));
        }

        internal void Draw()
        {
            if (_clearFocus)
            {
                if (GUI.GetNameOfFocusedControl() == FilterControl) GUI.FocusControl(null);
                _clearFocus = false;
            }
            if (!Visible) return;
            SyncRect();
            if (_styles == null) _styles = new WindowStyles();
            Event current = Event.current;
            bool inside = _rect.Contains(current.mousePosition);
            if (current.rawType == EventType.MouseDown)
            {
                _pressedSource = null;
                if (inside) _mouseGesture = true;
            }
            if (TextFocused && current.rawType == EventType.MouseDown && !FilterRect().Contains(current.mousePosition))
                GUI.FocusControl(null);
            DrawWindow();
            TextFocused = Visible && GUI.GetNameOfFocusedControl() == FilterControl;
            ConsumeInput(current, inside);
        }

        private void DrawWindow()
        {
            Color previousColor = GUI.color;
            Color previousBackground = GUI.backgroundColor;
            Color previousContent = GUI.contentColor;
            bool previousEnabled = GUI.enabled;
            Rect before = _rect;
            try
            {
                GUI.color = Color.white;
                GUI.backgroundColor = Color.white;
                GUI.contentColor = Color.white;
                GUI.enabled = true;
                _rect = GUI.Window(WindowId, _rect, _drawContents, string.Empty, _styles.Window);
                _rect.x = Mathf.Clamp(_rect.x, 0, Mathf.Max(0, Screen.width - _rect.width));
                _rect.y = Mathf.Clamp(_rect.y, 0, Mathf.Max(0, Screen.height - _rect.height));
                if (before.position != _rect.position) _moved = true;
            }
            finally
            {
                GUI.color = previousColor;
                GUI.backgroundColor = previousBackground;
                GUI.contentColor = previousContent;
                GUI.enabled = previousEnabled;
            }
        }

        private void DrawContents(int id)
        {
            // Window 回调有独立的绘制状态，控件配色在回调内明确设置。
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
            float width = _rect.width;
            WindowStyles.Band(new Rect(0, 0, width, _rect.height), new Color(0.09f, 0.11f, 0.14f, 1));
            WindowStyles.Band(new Rect(0, 0, width, 32), new Color(0.15f, 0.19f, 0.25f, 1));
            GUI.Label(new Rect(10, 3, width - 65, 26), _titleContent, _styles.Title);
            if (GUI.Button(new Rect(width - 40, 4, 30, 24), "X", _styles.Button)) SetVisible(false);
            DrawToolbar();
            float bodyHeight = Mathf.Max(80, _rect.height - 116);
            float listWidth = (width - 32) * 0.55f;
            _listRect = new Rect(10, 104, listWidth, bodyHeight);
            DrawList(_listRect);
            DrawDetails(new Rect(listWidth + 22, 104, width - listWidth - 32, bodyHeight));
            DrawErrorTooltip();
            GUI.DragWindow(new Rect(0, 0, width - 48, 32));
        }

        private void DrawToolbar()
        {
            float width = _rect.width;
            GUI.Label(new Rect(10, 38, 44, 25), "筛选", _styles.Label);
            GUI.SetNextControlName(FilterControl);
            string filter = GUI.TextField(new Rect(56, 38, width - 282, 25), _model.Filter, 200, _styles.Text);
            if (filter != _model.Filter) { _model.Filter = filter; _scrollRow = 0; }
            if (GUI.Button(new Rect(width - 220, 38, 50, 25), "清空", _styles.Button))
            { _model.Filter = string.Empty; _scrollRow = 0; }
            bool paused = GUI.Toggle(new Rect(width - 162, 38, 152, 25), _statistics.Paused,
                _statistics.Paused ? "已暂停屏蔽" : "暂停屏蔽", _styles.Button);
            _statistics.Paused = paused;
            GUI.Label(new Rect(10, 70, width - 294, 24), _model.Summary, _styles.Muted);
            GUI.Label(new Rect(width - 284, 70, 48, 24), "排序", _styles.Muted);
            for (int i = 0; i < SortNames.Length; i++)
            {
                bool selected = _model.Sort == (SourceSort)i;
                if (GUI.Toggle(new Rect(width - 234 + i * 76, 70, 72, 24), selected,
                    selected ? SelectedSortNames[i] : SortNames[i], _styles.Button))
                    _model.Sort = (SourceSort)i;
            }
        }

        private void DrawList(Rect area)
        {
            WindowStyles.Band(area, new Color(0.12f, 0.14f, 0.18f, 1));
            int visibleRows = Mathf.Max(1, Mathf.FloorToInt((area.height - 28) / RowHeight));
            int maxScroll = Math.Max(0, _model.Rows.Count - visibleRows);
            if (Event.current.type == EventType.ScrollWheel && area.Contains(Event.current.mousePosition))
            { _scrollRow += Event.current.delta.y * 3; Event.current.Use(); }
            _scrollRow = Mathf.Clamp(_scrollRow, 0, maxScroll);
            if (maxScroll > 0)
                _scrollRow = GUI.VerticalSlider(new Rect(area.xMax - 14, area.y + 28, 12, area.height - 30),
                    _scrollRow, 0, maxScroll, _styles.Slider, _styles.Thumb);
            float rowWidth = area.width - 18;
            GUI.Label(new Rect(area.x + 4, area.y, rowWidth - 140, 24), "来源", _styles.Muted);
            GUI.Label(new Rect(area.x + rowWidth - 140, area.y, 70, 24), "累计", _styles.Right);
            GUI.Label(new Rect(area.x + rowWidth - 70, area.y, 70, 24), "已屏蔽", _styles.Right);
            int first = Mathf.FloorToInt(_scrollRow);
            int end = Math.Min(first + visibleRows, _model.Rows.Count);
            for (int i = first; i < end; i++)
                DrawRow(_model.Rows[i], new Rect(area.x + 2, area.y + 27 + (i - first) * RowHeight, rowWidth, RowHeight));
            if (_model.Rows.Count == 0)
                GUI.Label(new Rect(area.x + 4, area.y + 30, area.width - 8, 24), "没有匹配的日志来源", _styles.Muted);
        }

        private void DrawRow(LogRow row, Rect area)
        {
            if (ReferenceEquals(_model.Selected, row))
                WindowStyles.Band(area, new Color(0.19f, 0.30f, 0.43f, 1));
            // 滚动会复用同一位置的控件 ID，松开时必须核对按下的来源身份。
            if (Event.current.type == EventType.MouseDown && area.Contains(Event.current.mousePosition))
                _pressedSource = row.Name;
            if (GUI.Button(area, string.Empty, _styles.Row)) _model.SelectOnRelease(_pressedSource, row.Name);
            GUI.Label(new Rect(area.x + 2, area.y, area.width - 142, area.height), row.Label, _styles.Label);
            GUI.Label(new Rect(area.xMax - 140, area.y, 70, area.height), row.TotalText, _styles.Right);
            GUI.Label(new Rect(area.xMax - 70, area.y, 70, area.height), row.BlockedText, _styles.Right);
        }

        private void DrawDetails(Rect area)
        {
            LogRow row = _model.Selected;
            if (row == null)
            {
                GUI.Label(new Rect(area.x, area.y, area.width, 25), "选择左侧来源以设置规则", _styles.Label);
                DrawRuleStatus(new Rect(area.x, area.y + 32, area.width, Mathf.Max(0, area.height - 32)));
                return;
            }
            GUI.Label(new Rect(area.x, area.y, area.width, 25), row.Label, _styles.Title);
            GUI.Label(new Rect(area.x, area.y + 26, area.width, 24), row.Detail, _styles.Muted);
            LogLevel mask = _statistics.GetRule(row.Name);
            for (int i = 0; i < LogListModel.Levels.Length; i++)
                mask = DrawLevel(row, mask, i, new Rect(area.x, area.y + 54 + i * RowHeight, area.width, 24));
            if (_statistics.SetRule(row.Name, mask)) _store.MarkChanged(_now);
            float buttonsY = area.y + 208;
            if (GUI.Button(new Rect(area.x, buttonsY, area.width, 25), "全部勾选", _styles.Button)) SetRule(row.Name, LogLevel.All);
            if (GUI.Button(new Rect(area.x, buttonsY + 30, area.width, 25), "全部取消", _styles.Button)) SetRule(row.Name, LogLevel.None);
            GUI.Label(new Rect(area.x, buttonsY + 59, area.width, 24), row.OtherText, _styles.Muted);
            DrawRuleStatus(new Rect(area.x, buttonsY + 88, area.width, Mathf.Max(0, area.height - 296)));
        }

        private LogLevel DrawLevel(LogRow row, LogLevel mask, int index, Rect area)
        {
            LogLevel level = LogListModel.Levels[index];
            bool blocked = (mask & level) != 0;
            bool next = GUI.Toggle(new Rect(area.x, area.y, 128, area.height), blocked,
                blocked ? BlockedLabels[index] : AllowedLabels[index], _styles.Button);
            GUI.Label(new Rect(area.x + 134, area.y, area.width - 134, area.height), row.LevelCounts[index], _styles.Right);
            return next ? mask | level : mask & ~level;
        }

        private void SetRule(string source, LogLevel mask)
        {
            if (_statistics.SetRule(source, mask)) _store.MarkChanged(_now);
        }

        private void DrawRuleStatus(Rect area)
        {
            bool canRetry = _store.CanSave && _store.HasPendingChanges;
            _saveContent.text = _store.Status;
            _saveContent.tooltip = _store.ErrorDetail;
            GUI.Label(new Rect(area.x, area.y, Mathf.Max(0, area.width - (canRetry ? 92 : 0)),
                Mathf.Min(44, area.height)), _saveContent, _styles.Wrap);
            if (canRetry && GUI.Button(new Rect(area.xMax - 86, area.y, 86, 24), "重试保存", _styles.Button))
                _store.RequestRetry(_now);
            if (string.IsNullOrEmpty(FaultText)) return;
            _faultContent.text = FaultText;
            _faultContent.tooltip = FaultText;
            GUI.Label(new Rect(area.x, area.y + 46, area.width, Mathf.Max(0, area.height - 46)), _faultContent, _styles.Wrap);
        }

        private void DrawErrorTooltip()
        {
            if (Event.current.type != EventType.Repaint || string.IsNullOrEmpty(GUI.tooltip)) return;
            if (GUI.tooltip != _store.ErrorDetail && GUI.tooltip != FaultText) return;
            _errorContent.text = GUI.tooltip;
            float height = Mathf.Clamp(_styles.Wrap.CalcHeight(_errorContent, _rect.width - 32) + 12, 36, 132);
            Rect tooltip = new Rect(10, _rect.height - height - 10, _rect.width - 20, height);
            WindowStyles.Band(tooltip, new Color(0.04f, 0.06f, 0.09f, 1));
            GUI.Label(new Rect(tooltip.x + 6, tooltip.y + 6, tooltip.width - 12, tooltip.height - 12), _errorContent, _styles.Wrap);
        }

        private void ConsumeInput(Event current, bool inside)
        {
            if (TextFocused) Input.ResetInputAxes();
            bool mouseEvent = current.type == EventType.MouseDown || current.type == EventType.MouseUp
                || current.type == EventType.MouseDrag || current.type == EventType.ScrollWheel;
            bool keyEvent = TextFocused && (current.type == EventType.KeyDown || current.type == EventType.KeyUp);
            if ((mouseEvent && (inside || _mouseGesture)) || keyEvent) current.Use();
            if (current.rawType == EventType.MouseUp) { _mouseGesture = false; _pressedSource = null; FinishMove(); }
        }

        private void SyncRect()
        {
            if (!_positionInvalid && _screenWidth == Screen.width && _screenHeight == Screen.height) return;
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
            float width = Mathf.Min(780, _screenWidth);
            float height = Mathf.Min(560, _screenHeight);
            _rect = new Rect((_screenWidth - width) * _normalized.x, (_screenHeight - height) * _normalized.y, width, height);
            _positionInvalid = false;
            _moved = false;
        }

        private void FinishMove()
        {
            if (!_moved) return;
            _normalized = new Vector2(_rect.x / Mathf.Max(1, _screenWidth - _rect.width), _rect.y / Mathf.Max(1, _screenHeight - _rect.height));
            _positionPending = true;
            _moved = false;
        }

        private Rect FilterRect() => new Rect(_rect.x + 56, _rect.y + 38, _rect.width - 282, 25);
        private static Vector2 MousePosition() => new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        private static float Unit(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp01(value);
        public void Dispose() { SetVisible(false); _styles = null; }
    }
}
