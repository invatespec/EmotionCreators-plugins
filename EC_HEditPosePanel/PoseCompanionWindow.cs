using BepInEx.Configuration;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EC_HEditPosePanel
{
    internal enum ThumbnailSize
    {
        Small,
        Medium,
        Large
    }

    internal sealed class PoseCompanionWindow
    {
        internal const float Width = 300f;
        internal const float Height = 600f;

        private const int CompanionWindowId = 0x4F51;
        private const int ThumbnailWindowId = 0x4F52;
        private const float ThumbnailFrameWidth = 16f;
        private const float ThumbnailFrameHeight = 40f;
        private const float HeaderHeight = 58f;
        // 命令 + Guide（含展开/收拢/滑条/取消栏）完整露出
        private const float FooterHeight = 205f;
        private const float PoseListHeight = 155f;
        private static readonly Color PanelOuterBg = new Color32(0x1E, 0x23, 0x29, 0xFF);
        private static readonly Color PanelContentBg = new Color32(0x28, 0x2E, 0x36, 0xFF);
        private static readonly Color PanelFooterBg = new Color32(0x18, 0x1C, 0x21, 0xFF);
        private static readonly Color SectionDividerColor = new Color32(0x3A, 0x42, 0x4C, 0xFF);
        private static readonly Color SliderTrackColor = new Color32(0x67, 0x72, 0x7E, 0xFF);
        private static readonly Color SliderThumbColor = new Color32(0xD0, 0xD5, 0xDB, 0xFF);

        private readonly ConfigEntry<bool> _showThumbnails;
        private readonly ConfigEntry<ThumbnailSize> _thumbnailSize;
        private readonly ConfigEntry<float> _thumbnailPositionX;
        private readonly ConfigEntry<float> _thumbnailPositionY;
        private readonly PoseLibrary _library;
        private readonly FkPoseService _fkPoseService;
        private readonly PoseEditContext _poseEditContext;
        private readonly GuideAdjustmentSession _guideSession;

        private bool _expanded;
        private bool _showHandTab = true;
        private Rect _companionRect;
        private Rect _thumbnailRect;
        private bool _thumbnailRectSynced;
        private bool _thumbnailPositionDirty;
        private bool _thumbnailAutoSideKnown;
        private bool _thumbnailAutoOnRight;
        private int _screenWidth;
        private int _screenHeight;
        private Texture2D _thumbnail;
        private string _thumbnailTitle = "缩略图";
        private GUIStyle _windowStyle;
        private List<PoseFolder> _folders = new List<PoseFolder>();
        private int _selectedFolderIndex;
        private string _selectedPoseName;
        private string _status;
        private string _guideStatus;
        private HandSide _guideHandSide = HandSide.Left;
        private GuideAdjustMode _guideMode = GuideAdjustMode.Curl;
        private float _guideValue;
        private Vector2 _folderScrollPosition;
        private Vector2 _poseScrollPosition;
        private string _editText = string.Empty;
        private bool _focusManagementInput;
        private bool _keyboardInputFocused;
        private ManagementMode _managementMode;
        private ManagementMode _pendingSaveMode;
        private enum ManagementMode
        {
            None,
            CreateFolder,
            RenameFolder,
            DeleteFolder,
            RenamePose,
            DeletePose,
            ConfirmOverwritePose,
            SaveLeftHand,
            SaveRightHand,
            SaveSkirt,
            ConfirmOverwriteSave,
            ConfirmReplaceThumbnail
        }

        internal PoseCompanionWindow(
            ConfigEntry<bool> showThumbnails,
            ConfigEntry<ThumbnailSize> thumbnailSize,
            ConfigEntry<float> thumbnailPositionX,
            ConfigEntry<float> thumbnailPositionY,
            PoseLibrary library,
            FkPoseService fkPoseService,
            PoseEditContext poseEditContext,
            GuideAdjustmentSession guideSession)
        {
            _showThumbnails = showThumbnails;
            _thumbnailSize = thumbnailSize;
            _thumbnailPositionX = thumbnailPositionX;
            _thumbnailPositionY = thumbnailPositionY;
            _library = library;
            _fkPoseService = fkPoseService;
            _poseEditContext = poseEditContext;
            _guideSession = guideSession;
            RefreshFolders();
        }

        internal bool ContainsMouse(Vector2 mousePosition)
        {
            if (_expanded && _companionRect.Contains(mousePosition)) return true;
            return CanDrawThumbnail() && _thumbnailRect.Contains(mousePosition);
        }

        internal bool IsKeyboardInputFocused()
            => _expanded && _keyboardInputFocused;

        internal void DrawExpandButton()
        {
            string label = _expanded ? "◀" : "▶";
            string tooltip = _expanded ? "收起姿势面板" : "展开姿势面板";
            if (GUILayout.Button(new GUIContent(label, tooltip), GUILayout.Width(22f), GUILayout.Height(22f)))
            {
                _expanded = !_expanded;
                if (!_expanded) _keyboardInputFocused = false;
            }
        }

        internal void Draw(Rect mainWindowRect)
        {
            EnsureWindowStyle();
            _companionRect = CalculateDockedRect(mainWindowRect, Screen.width, Screen.height);
            if (_expanded)
                GUI.Window(CompanionWindowId, _companionRect, DrawCompanionWindow, GUIContent.none, _windowStyle);

            DrawThumbnailWindow(mainWindowRect);
            EatGameInputUnderVisibleWindows();
        }

        internal void SetThumbnailContent(Texture2D thumbnail, string title)
        {
            if (!object.ReferenceEquals(_thumbnail, thumbnail)) ReleaseThumbnail();
            _thumbnail = thumbnail;
            _thumbnailTitle = string.IsNullOrEmpty(title) ? "缩略图" : title;
            _thumbnailRectSynced = false;
        }

        internal void CloseThumbnail()
        {
            ReleaseThumbnail();
        }

        internal void Reset()
        {
            _guideSession?.Cancel();
            _expanded = false;
            _showHandTab = true;
            _guideHandSide = HandSide.Left;
            _guideMode = GuideAdjustMode.Curl;
            CloseThumbnail();
            _thumbnailPositionDirty = false;
            _selectedFolderIndex = 0;
            _selectedPoseName = null;
            _status = null;
            _guideStatus = null;
            _guideValue = 0f;
            _folderScrollPosition = Vector2.zero;
            _poseScrollPosition = Vector2.zero;
            CancelManagement();
            RefreshFolders();
        }

        internal void Dispose()
        {
            _guideSession?.Cancel();
            ReleaseThumbnail();
            _windowStyle = null;
        }

        internal void SetGuideStatus(string status)
        {
            _guideStatus = status;
            _guideValue = _guideSession != null ? _guideSession.Value : 0f;
        }

        private void RefreshFolders()
        {
            PoseLibraryKind kind = _showHandTab ? PoseLibraryKind.Hand : PoseLibraryKind.Skirt;
            string selectedFolderName = CurrentFolder()?.Name;
            _folders = _library != null ? _library.Scan(kind) : new List<PoseFolder>();
            if (!string.IsNullOrEmpty(selectedFolderName))
            {
                int matchingIndex = _folders.FindIndex(folder => string.Equals(folder.Name, selectedFolderName, System.StringComparison.OrdinalIgnoreCase));
                if (matchingIndex >= 0) _selectedFolderIndex = matchingIndex;
            }
            _selectedFolderIndex = Mathf.Clamp(_selectedFolderIndex, 0, Mathf.Max(0, _folders.Count - 1));
            PoseFolder selectedFolder = CurrentFolder();
            if (selectedFolder == null || !selectedFolder.Entries.Any(entry => entry.Name == _selectedPoseName))
            {
                _selectedPoseName = null;
                ReleaseThumbnail();
            }
        }

        internal static Rect CalculateDockedRect(Rect main, float screenWidth, float screenHeight)
        {
            float rightX = main.xMax;
            float leftX = main.x - Width;
            bool fitsRight = rightX + Width <= screenWidth;
            bool fitsLeft = leftX >= 0f;

            float x;
            if (fitsRight) x = rightX;
            else if (fitsLeft) x = leftX;
            else x = screenWidth - main.xMax >= main.x ? rightX : leftX;

            float y = main.y + main.height * 0.5f - Height;
            return WindowPlacement.ClampToScreen(new Rect(x, y, Width, Height), screenWidth, screenHeight);
        }

        internal static Vector2 GetThumbnailContentSize(ThumbnailSize size)
        {
            switch (size)
            {
                case ThumbnailSize.Medium: return new Vector2(240f, 135f);
                case ThumbnailSize.Large: return new Vector2(320f, 180f);
                default: return new Vector2(160f, 90f);
            }
        }

        private void DrawCompanionWindow(int id)
        {
            float footerY = Height - FooterHeight - 4f;
            DrawSolid(new Rect(0f, 0f, Width, Height), PanelOuterBg);
            DrawSolid(new Rect(4f, 4f, Width - 8f, footerY - 8f), PanelContentBg);
            DrawSolid(new Rect(4f, footerY, Width - 8f, FooterHeight), PanelFooterBg);

            GUILayout.BeginArea(new Rect(8f, 8f, Width - 16f, HeaderHeight));
            DrawHeader();
            DrawTabs();
            GUILayout.EndArea();

            float contentY = HeaderHeight + 12f;
            float contentHeight = footerY - contentY - 4f;
            GUILayout.BeginArea(new Rect(8f, contentY, Width - 16f, contentHeight));
            DrawFolderSection();
            DrawPoseSection();
            GUILayout.EndArea();

            GUILayout.BeginArea(new Rect(8f, footerY + 6f, Width - 16f, FooterHeight - 12f));
            DrawCommandSection();
            DrawGuideSection();
            GUILayout.EndArea();

            string focused = GUI.GetNameOfFocusedControl();
            bool inputMode = _managementMode == ManagementMode.CreateFolder
                || _managementMode == ManagementMode.RenameFolder
                || _managementMode == ManagementMode.RenamePose
                || IsSaveMode(_managementMode);
            _keyboardInputFocused = inputMode && (focused == "PoseFolderManagementInput"
                || focused == "PoseSaveNameInput"
                || focused == "PoseRenameInput");
        }

        private void EnsureWindowStyle()
        {
            if (_windowStyle != null) return;

            _windowStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(2, 2, 2, 2)
            };
        }

        private static void DrawSolid(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static void DrawSectionDivider()
        {
            GUILayout.Space(3f);
            Rect rect = GUILayoutUtility.GetRect(1f, 2f, GUILayout.ExpandWidth(true), GUILayout.Height(2f));
            if (Event.current.type == EventType.Repaint)
                DrawSolid(rect, SectionDividerColor);
            GUILayout.Space(3f);
        }

        private string DrawManagementTextField(string controlName, string value)
        {
            GUI.SetNextControlName(controlName);
            string next = GUILayout.TextField(value ?? string.Empty);
            if (_focusManagementInput && Event.current != null && Event.current.type == EventType.Repaint)
            {
                GUI.FocusControl(controlName);
                _focusManagementInput = false;
            }
            return next;
        }

        private void EatGameInputUnderVisibleWindows()
        {
            Vector2 mousePosition = new Vector2(
                Input.mousePosition.x,
                Screen.height - Input.mousePosition.y);
            bool overCompanion = _expanded && _companionRect.Contains(mousePosition);
            bool overThumbnail = CanDrawThumbnail() && _thumbnailRect.Contains(mousePosition);
            if (!overCompanion && !overThumbnail) return;

            if (IsKeyboardInputFocused())
                Input.ResetInputAxes();
            Event current = Event.current;
            if (current != null && IsMouseEvent(current.type))
                current.Use();
        }

        private static bool IsMouseEvent(EventType eventType)
            => eventType == EventType.MouseDown
                || eventType == EventType.MouseUp
                || eventType == EventType.MouseDrag
                || eventType == EventType.ScrollWheel;

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("姿势库");
            GUILayout.FlexibleSpace();
            string icon = _showThumbnails.Value ? "◉" : "○";
            string tooltip = _showThumbnails.Value ? "关闭缩略图" : "开启缩略图";
            if (GUILayout.Button(new GUIContent(icon, tooltip), GUILayout.Width(24f), GUILayout.Height(22f)))
            {
                _showThumbnails.Value = !_showThumbnails.Value;
                if (!_showThumbnails.Value) CloseThumbnail();
                else RefreshSelectedThumbnail();
            }
            GUILayout.EndHorizontal();
        }

        private void DrawFolderManagement()
        {
            if (_managementMode != ManagementMode.CreateFolder && _managementMode != ManagementMode.RenameFolder
                && _managementMode != ManagementMode.DeleteFolder) return;
            if (_managementMode == ManagementMode.DeleteFolder)
            {
                GUILayout.Label("确认删除文件夹及其中姿势？");
                DrawManagementActions(ConfirmFolderDelete, CancelManagement);
                return;
            }
            GUILayout.BeginHorizontal();
            _editText = DrawManagementTextField("PoseFolderManagementInput", _editText);
            DrawManagementActions(ConfirmFolderEdit, CancelManagement);
            GUILayout.EndHorizontal();
        }

        private void ConfirmFolderEdit()
        {
            bool renaming = _managementMode == ManagementMode.RenameFolder;
            string requestedName = (_editText ?? string.Empty).Trim();
            string error;
            bool ok = _managementMode == ManagementMode.CreateFolder
                ? _library.TryCreateFolder(CurrentKind(), _editText, out error)
                : _library.TryRenameFolder(CurrentFolder(), _editText, out error);
            if (!ok)
            {
                _status = error;
                RefreshFolders();
                return;
            }
            _managementMode = ManagementMode.None;
            _editText = string.Empty;
            _focusManagementInput = false;
            _keyboardInputFocused = false;
            _status = null;
            RefreshFolders();
            if (renaming)
            {
                int renamedIndex = _folders.FindIndex(folder => string.Equals(folder.Name, requestedName, System.StringComparison.OrdinalIgnoreCase));
                if (renamedIndex >= 0) _selectedFolderIndex = renamedIndex;
            }
        }

        private void ConfirmFolderDelete()
        {
            string error;
            if (!_library.TryDeleteFolder(CurrentFolder(), out error))
            {
                _status = error;
                RefreshFolders();
                return;
            }
            _managementMode = ManagementMode.None;
            _selectedFolderIndex = 0;
            _selectedPoseName = null;
            _status = null;
            RefreshFolders();
            ReleaseThumbnail();
        }

        private void DrawManagementActions(System.Action accept, System.Action cancel)
        {
            if (GUILayout.Button("确定", GUILayout.Width(42f))) accept();
            if (GUILayout.Button("取消", GUILayout.Width(42f))) cancel();
        }

        private void CancelManagement()
        {
            _managementMode = ManagementMode.None;
            _pendingSaveMode = ManagementMode.None;
            _editText = string.Empty;
            _focusManagementInput = false;
            _keyboardInputFocused = false;
        }

        private PoseLibraryKind CurrentKind() => _showHandTab ? PoseLibraryKind.Hand : PoseLibraryKind.Skirt;
        private PoseFolder CurrentFolder() => _folders.Count == 0 ? null : _folders[Mathf.Clamp(_selectedFolderIndex, 0, _folders.Count - 1)];
        private PoseEntry CurrentEntry() => CurrentFolder()?.Entries.FirstOrDefault(entry => entry.Name == _selectedPoseName);

        private void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            bool hand = GUILayout.Toggle(_showHandTab, "手势", GUI.skin.button, GUILayout.Height(26f));
            bool skirt = GUILayout.Toggle(!_showHandTab, "裙子", GUI.skin.button, GUILayout.Height(26f));
            GUILayout.EndHorizontal();

            if (hand && !_showHandTab)
            {
                _guideSession?.Cancel();
                _guideValue = 0f;
                _showHandTab = true;
                CloseThumbnail();
                _selectedFolderIndex = 0;
                _selectedPoseName = null;
                _folderScrollPosition = Vector2.zero;
                _poseScrollPosition = Vector2.zero;
                _status = null;
                RefreshFolders();
                CancelManagement();
            }
            else if (skirt && _showHandTab)
            {
                _guideSession?.Cancel();
                _guideValue = 0f;
                _showHandTab = false;
                CloseThumbnail();
                _selectedFolderIndex = 0;
                _selectedPoseName = null;
                _folderScrollPosition = Vector2.zero;
                _poseScrollPosition = Vector2.zero;
                _status = null;
                RefreshFolders();
                CancelManagement();
            }
        }

        private void DrawFolderSection()
        {
            DrawSectionDivider();
            GUILayout.BeginHorizontal();
            GUILayout.Label("文件夹", GUILayout.Width(52f));
            GUILayout.Space(8f);
            DrawFolderActionButtons();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            _folderScrollPosition = GUILayout.BeginScrollView(
                _folderScrollPosition,
                GUIStyle.none,
                GUIStyle.none,
                GUILayout.Height(54f),
                GUILayout.ExpandWidth(true));
            if (_folders.Count == 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("暂无文件夹", GUILayout.Width(90f));
                GUILayout.EndHorizontal();
            }
            else
            {
                for (int i = 0; i < _folders.Count; i++)
                {
                    if (i % 3 == 0) GUILayout.BeginHorizontal();
                    bool selected = i == _selectedFolderIndex;
                    if (GUILayout.Toggle(selected, _folders[i].Name, GUI.skin.button, GUILayout.Width(90f), GUILayout.Height(24f)) && !selected)
                    {
                        _selectedFolderIndex = i;
                        _selectedPoseName = null;
                        _poseScrollPosition = Vector2.zero;
                        _status = null;
                        ReleaseThumbnail();
                        CancelManagement();
                    }
                    if (i % 3 == 2 || i == _folders.Count - 1) GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            DrawFolderManagement();
        }

        private void DrawFolderActionButtons()
        {
            if (GUILayout.Button(new GUIContent("+", "新建文件夹"), GUILayout.Width(24f), GUILayout.Height(22f)))
            {
                _managementMode = ManagementMode.CreateFolder;
                _editText = string.Empty;
                _focusManagementInput = true;
            }
            bool hasFolder = CurrentFolder() != null;
            GUI.enabled = hasFolder;
            if (GUILayout.Button(new GUIContent("✎", "重命名文件夹"), GUILayout.Width(24f), GUILayout.Height(22f)))
            {
                _managementMode = ManagementMode.RenameFolder;
                _editText = CurrentFolder().Name;
                _focusManagementInput = true;
            }
            GUI.enabled = hasFolder && !CurrentFolder().IsDefault;
            if (GUILayout.Button(new GUIContent("−", "删除文件夹"), GUILayout.Width(24f), GUILayout.Height(22f)))
                _managementMode = ManagementMode.DeleteFolder;
            GUI.enabled = true;
        }

        private void DrawPoseSection()
        {
            DrawSectionDivider();
            GUILayout.Label("姿势");
            _poseScrollPosition = GUILayout.BeginScrollView(
                _poseScrollPosition,
                GUIStyle.none,
                GUIStyle.none,
                GUILayout.Height(PoseListHeight),
                GUILayout.ExpandWidth(true));
            PoseFolder folder = _folders.Count > 0 ? _folders[_selectedFolderIndex] : null;
            if (folder == null || folder.Entries.Count == 0)
                GUILayout.Label("暂无姿势");
            else
            {
                foreach (PoseEntry entry in folder.Entries)
                {
                    bool selected = string.Equals(_selectedPoseName, entry.Name, System.StringComparison.Ordinal);
                    GUILayout.BeginHorizontal();
                    bool toggled = GUILayout.Toggle(selected, entry.Name, GUI.skin.button);
                    if (!selected && toggled)
                        SelectPose(entry);
                    else if (selected && !toggled)
                    {
                        ReleaseThumbnail();
                        _selectedPoseName = null;
                        _status = null;
                    }
                    if (GUILayout.Button(new GUIContent("✎", "重命名姿势"), GUILayout.Width(24f), GUILayout.Height(22f)))
                    {
                        _managementMode = ManagementMode.RenamePose;
                        _editText = entry.Name;
                        _selectedPoseName = entry.Name;
                        _focusManagementInput = true;
                    }
                    if (GUILayout.Button(new GUIContent("−", "删除姿势"), GUILayout.Width(24f), GUILayout.Height(22f)))
                    {
                        _managementMode = ManagementMode.DeletePose;
                        _selectedPoseName = entry.Name;
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();
            DrawSectionDivider();
            DrawPoseManagement();
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
        }

        private void DrawPoseManagement()
        {
            if (IsSaveMode(_managementMode)
                || _managementMode == ManagementMode.ConfirmOverwriteSave
                || _managementMode == ManagementMode.ConfirmReplaceThumbnail)
            {
                DrawSaveManagement();
                return;
            }
            if (_managementMode != ManagementMode.RenamePose && _managementMode != ManagementMode.DeletePose
                && _managementMode != ManagementMode.ConfirmOverwritePose) return;
            GUILayout.BeginHorizontal();
            if (_managementMode == ManagementMode.DeletePose) GUILayout.Label("确认删除当前姿势？");
            else if (_managementMode == ManagementMode.ConfirmOverwritePose) GUILayout.Label("姿势已存在，确认覆盖？");
            else
                _editText = DrawManagementTextField("PoseRenameInput", _editText);
            DrawManagementActions(
                _managementMode == ManagementMode.ConfirmOverwritePose ? ConfirmPoseOverwrite : ConfirmPoseManagement,
                CancelManagement);
            GUILayout.EndHorizontal();
        }

        private void ConfirmPoseManagement()
        {
            PoseFolder folder = CurrentFolder();
            PoseEntry entry = folder?.Entries.FirstOrDefault(e => e.Name == _selectedPoseName);
            string error;
            bool ok = _managementMode == ManagementMode.DeletePose
                ? _library.TryDeletePose(entry, out error)
                : _library.TryRenamePose(entry, _editText, false, out error);
            if (!ok)
            {
                if (_managementMode == ManagementMode.RenamePose && error == "姿势已存在，需要确认覆盖。")
                {
                    _managementMode = ManagementMode.ConfirmOverwritePose;
                    return;
                }
                _status = error;
                RefreshFolders();
                return;
            }
            _managementMode = ManagementMode.None;
            _selectedPoseName = null;
            _editText = string.Empty;
            _status = null;
            ReleaseThumbnail();
            RefreshFolders();
        }

        private void DrawSaveManagement()
        {
            GUILayout.BeginHorizontal();
            if (_managementMode == ManagementMode.ConfirmOverwriteSave)
            {
                GUILayout.Label("姿势已存在，确认覆盖？");
                DrawManagementActions(BeginReplaceThumbnailPrompt, CancelManagement);
            }
            else if (_managementMode == ManagementMode.ConfirmReplaceThumbnail)
            {
                GUILayout.Label("是否替换缩略图？");
                if (GUILayout.Button("替换", GUILayout.Width(42f))) ConfirmPoseSave(true, true);
                if (GUILayout.Button("保留", GUILayout.Width(42f))) ConfirmPoseSave(true, false);
                if (GUILayout.Button("取消", GUILayout.Width(42f))) CancelManagement();
            }
            else
            {
                _editText = DrawManagementTextField("PoseSaveNameInput", _editText);
                DrawManagementActions(() => ConfirmPoseSave(false, true), CancelManagement);
            }
            GUILayout.EndHorizontal();
        }

        private void BeginReplaceThumbnailPrompt()
        {
            _managementMode = ManagementMode.ConfirmReplaceThumbnail;
        }

        private void ConfirmPoseSave(bool overwrite, bool replaceThumbnail)
        {
            ManagementMode saveMode = overwrite ? _pendingSaveMode : _managementMode;
            if (!IsSaveMode(saveMode))
            {
                _status = "保存命令状态无效。";
                CancelManagement();
                return;
            }
            string requestedName = (_editText ?? string.Empty).Trim();
            HandSide? hand = saveMode == ManagementMode.SaveLeftHand
                ? HandSide.Left
                : saveMode == ManagementMode.SaveRightHand ? HandSide.Right : (HandSide?)null;
            PoseLibraryKind kind = saveMode == ManagementMode.SaveSkirt
                ? PoseLibraryKind.Skirt : PoseLibraryKind.Hand;
            bool keepThumbnail = overwrite && !replaceThumbnail;
            _status = "正在保存姿势...";
            _fkPoseService.Save(kind, CurrentFolder(), requestedName, hand, overwrite, replaceThumbnail,
                outcome => HandleSaveOutcome(outcome, saveMode, requestedName, keepThumbnail));
        }

        private void HandleSaveOutcome(PoseCommandOutcome outcome, ManagementMode saveMode, string requestedName,
            bool keepThumbnail)
        {
            if (outcome == null) return;
            _status = outcome.Message;
            if (outcome.NeedsOverwrite)
            {
                _pendingSaveMode = saveMode;
                _managementMode = ManagementMode.ConfirmOverwriteSave;
                return;
            }
            // 失败也收起输入条，避免挡住状态提示（如未进手 FK）
            if (!outcome.Success)
            {
                CancelManagement();
                return;
            }

            _managementMode = ManagementMode.None;
            _pendingSaveMode = ManagementMode.None;
            _editText = string.Empty;
            _focusManagementInput = false;
            RefreshFolders();
            PoseFolder folder = CurrentFolder();
            if (folder != null && !string.IsNullOrEmpty(requestedName)
                && folder.Entries.Any(entry => string.Equals(entry.Name, requestedName, System.StringComparison.OrdinalIgnoreCase)))
                _selectedPoseName = folder.Entries.First(entry => string.Equals(entry.Name, requestedName, System.StringComparison.OrdinalIgnoreCase)).Name;
            // 保留旧图：刷新预览；替换/新建：丢掉旧内存纹理，避免展示已删/过期 PNG
            if (keepThumbnail) RefreshSelectedThumbnail();
            else ReleaseThumbnail();
        }

        private static bool IsSaveMode(ManagementMode mode)
            => mode == ManagementMode.SaveLeftHand
                || mode == ManagementMode.SaveRightHand
                || mode == ManagementMode.SaveSkirt;

        private void ConfirmPoseOverwrite()
        {
            PoseFolder folder = CurrentFolder();
            PoseEntry entry = folder?.Entries.FirstOrDefault(e => e.Name == _selectedPoseName);
            string error;
            if (!_library.TryRenamePose(entry, _editText, true, out error))
            {
                _status = error;
                RefreshFolders();
                return;
            }
            _managementMode = ManagementMode.None;
            _selectedPoseName = null;
            _editText = string.Empty;
            _status = null;
            ReleaseThumbnail();
            RefreshFolders();
        }

        private void SelectPose(PoseEntry entry)
        {
            _selectedPoseName = entry.Name;
            ReleaseThumbnail();
            _status = null;
            if (!_showThumbnails.Value) return;
            if (!entry.HasThumbnail)
            {
                _status = "该姿势没有缩略图。";
                return;
            }
            string error;
            _thumbnail = ThumbnailService.LoadPreview(entry.ThumbnailPath, out error);
            if (_thumbnail == null)
            {
                _status = "缩略图无法读取: " + error;
                return;
            }
            _thumbnailTitle = entry.Name;
            _thumbnailRectSynced = false;
        }

        private void RefreshSelectedThumbnail()
        {
            if (!_showThumbnails.Value || string.IsNullOrEmpty(_selectedPoseName)) return;
            PoseEntry entry = CurrentEntry();
            if (entry == null)
            {
                ReleaseThumbnail();
                return;
            }
            SelectPose(entry);
        }

        private void ReleaseThumbnail()
        {
            ThumbnailService.ReleasePreview(ref _thumbnail);
            _thumbnailTitle = "缩略图";
            _thumbnailRectSynced = false;
        }

        private void DrawCommandSection()
        {
            GUILayout.Label("命令");
            bool previous = GUI.enabled;
            try
            {
                bool available = _fkPoseService != null && !_fkPoseService.IsBusy;
                GUI.enabled = previous && available && CurrentFolder() != null;
                GUILayout.BeginHorizontal();
                if (_showHandTab)
                {
                    if (GUILayout.Button("保存左手")) BeginSave(ManagementMode.SaveLeftHand);
                    if (GUILayout.Button("保存右手")) BeginSave(ManagementMode.SaveRightHand);
                }
                else
                {
                    if (GUILayout.Button("保存")) BeginSave(ManagementMode.SaveSkirt);
                }
                GUILayout.EndHorizontal();

                GUI.enabled = previous && available && CurrentEntry() != null;
                GUILayout.BeginHorizontal();
                if (_showHandTab)
                {
                    if (GUILayout.Button("应用左手")) BeginApply(HandSide.Left);
                    if (GUILayout.Button("应用右手")) BeginApply(HandSide.Right);
                }
                else
                {
                    if (GUILayout.Button("应用")) BeginApply(null);
                }
                GUILayout.EndHorizontal();
            }
            finally
            {
                GUI.enabled = previous;
            }
        }

        private void BeginSave(ManagementMode mode)
        {
            _managementMode = mode;
            _pendingSaveMode = ManagementMode.None;
            _editText = string.Empty;
            _focusManagementInput = true;
            _status = null;
        }

        private void BeginApply(HandSide? hand)
        {
            CancelManagement();
            _guideSession?.Cancel();
            _guideValue = 0f;
            _status = "正在应用姿势...";
            _fkPoseService.Apply(CurrentKind(), CurrentEntry(), hand, outcome =>
            {
                if (outcome != null) _status = outcome.Message;
            });
        }

        private void DrawGuideSection()
        {
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Guide 调整");
            GUILayout.FlexibleSpace();
            bool previous = GUI.enabled;
            if (_showHandTab)
            {
                // 单按钮切换左右；切手才回滚（切展开/收拢保留叠加）
                GUI.enabled = previous && _poseEditContext != null && HEditPosePanelPlugin.IsInPoseEdit();
                string handLabel = _guideHandSide == HandSide.Left ? "左手" : "右手";
                if (GUILayout.Button(handLabel, GUILayout.Width(52f), GUILayout.Height(20f)))
                {
                    AbortGuidePreview();
                    _guideHandSide = _guideHandSide == HandSide.Left ? HandSide.Right : HandSide.Left;
                }
                GUI.enabled = previous;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GuideAdjustMode nextMode = _guideMode;
            if (GUILayout.Toggle(nextMode == GuideAdjustMode.Spread, "展开", GUI.skin.button))
                nextMode = GuideAdjustMode.Spread;
            if (GUILayout.Toggle(nextMode == GuideAdjustMode.Curl, "收拢", GUI.skin.button))
                nextMode = GuideAdjustMode.Curl;
            GUILayout.EndHorizontal();
            if (nextMode != _guideMode)
            {
                _guideMode = nextMode;
                if (_guideSession != null && _guideSession.IsActive)
                {
                    _guideSession.SetMode(_guideMode);
                    _guideValue = _guideSession.GetModeValue(_guideMode);
                }
                else _guideValue = 0f;
            }

            bool canEdit = previous && _poseEditContext != null && HEditPosePanelPlugin.IsInPoseEdit();
            GUI.enabled = canEdit;
            float nextValue = GUILayout.HorizontalSlider(_guideValue, -100f, 100f);
            if (GUI.enabled && !Mathf.Approximately(nextValue, _guideValue))
            {
                string error;
                if (!EnsureGuideSession(out error))
                {
                    _guideStatus = error;
                }
                else if (!_guideSession.Preview(nextValue, out error))
                {
                    _guideStatus = error;
                }
                else
                {
                    _guideValue = _guideSession.Value;
                    _guideStatus = null;
                }
            }
            GUI.enabled = previous;

            string modeHint = _guideMode == GuideAdjustMode.Spread
                ? "-100 负向展开 / +100 正向展开"
                : "-100 负向收拢 / +100 正向收拢";
            GUILayout.Label(modeHint);
            if (!string.IsNullOrEmpty(_guideStatus)) GUILayout.Label(_guideStatus);

            bool active = _guideSession != null && _guideSession.IsActive;
            GUI.enabled = previous && active;
            try
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("取消", GUILayout.Height(22f)))
                {
                    _guideSession.Cancel();
                    _guideValue = 0f;
                }
                if (GUILayout.Button("0", GUILayout.Width(42f), GUILayout.Height(22f)))
                {
                    string error;
                    if (!_guideSession.Reset(out error)) _guideStatus = error;
                    _guideValue = 0f;
                }
                if (GUILayout.Button("确认", GUILayout.Height(22f)))
                {
                    string error;
                    if (!_guideSession.Confirm(out error)) _guideStatus = error;
                    _guideValue = 0f;
                }
                GUILayout.EndHorizontal();
            }
            finally
            {
                GUI.enabled = previous;
            }
        }

        private bool EnsureGuideSession(out string error)
        {
            error = null;
            if (_guideSession == null)
            {
                error = "Guide 会话未初始化。";
                return false;
            }
            // 同目标事务内切模式不重建；双模式值叠加在同一基线上
            if (_guideSession.IsActive
                && (!_showHandTab || _guideSession.Side == _guideHandSide)
                && ((_showHandTab && _guideSession.Kind == GuideAdjustmentKind.Hand)
                    || (!_showHandTab && _guideSession.Kind == GuideAdjustmentKind.Skirt)))
            {
                if (_guideSession.Mode != _guideMode) _guideSession.SetMode(_guideMode);
                return true;
            }

            PoseEditSnapshot snapshot;
            if (!_poseEditContext.TryResolve(out snapshot, out error)) return false;

            List<GuideRotationTarget> targets;
            GuideAdjustmentKind kind;
            HandSide? side;
            if (_showHandTab)
            {
                kind = GuideAdjustmentKind.Hand;
                side = _guideHandSide;
                if (!GuideTargetDiscovery.TryCollectHand(snapshot.Bones, _guideHandSide, out targets, out error))
                    return false;
            }
            else
            {
                kind = GuideAdjustmentKind.Skirt;
                side = null;
                if (!GuideTargetDiscovery.TryCollectSkirt(
                    GuideTargetDiscovery.FindSelectedSkirtBone(), snapshot.Bones, out targets, out error))
                    return false;
            }

            GuideAdjustmentProfile profile = GuideAdjustmentProfiles.For(kind, _guideMode);
            return _guideSession.Begin(snapshot, kind, side, _guideMode, targets, profile, out error);
        }

        private void AbortGuidePreview()
        {
            _guideSession?.Cancel();
            _guideValue = 0f;
        }

        private static void DrawDisabledButton(string label, string tooltip)
        {
            bool previous = GUI.enabled;
            GUI.enabled = false;
            try
            {
                GUILayout.Button(new GUIContent(label, tooltip), GUILayout.Width(24f), GUILayout.Height(22f));
            }
            finally
            {
                GUI.enabled = previous;
            }
        }

        private void DrawThumbnailWindow(Rect mainWindowRect)
        {
            if (!CanDrawThumbnail()) return;

            Vector2 contentSize = GetThumbnailContentSize(_thumbnailSize.Value);
            Vector2 windowSize = new Vector2(
                contentSize.x + ThumbnailFrameWidth,
                contentSize.y + ThumbnailFrameHeight);
            SyncThumbnailRect(mainWindowRect, windowSize);

            Rect before = _thumbnailRect;
            _thumbnailRect = GUI.Window(ThumbnailWindowId, _thumbnailRect, DrawThumbnail, _thumbnailTitle);
            _thumbnailRect = WindowPlacement.ClampToScreen(_thumbnailRect, Screen.width, Screen.height);

            if (Input.GetMouseButton(0) && WindowPlacement.PositionChanged(before, _thumbnailRect))
                _thumbnailPositionDirty = true;
            if (!Input.GetMouseButton(0) && _thumbnailPositionDirty)
                SaveThumbnailPosition();
        }

        private void DrawThumbnail(int id)
        {
            Vector2 size = GetThumbnailContentSize(_thumbnailSize.Value);
            Rect content = new Rect(8f, 24f, size.x, size.y);
            GUI.DrawTexture(content, _thumbnail, ScaleMode.ScaleToFit, true);
            GUI.DragWindow(new Rect(0f, 0f, _thumbnailRect.width, 20f));
        }

        private void SyncThumbnailRect(Rect mainWindowRect, Vector2 windowSize)
        {
            bool screenChanged = _screenWidth != Screen.width || _screenHeight != Screen.height;
            bool sizeChanged = !Mathf.Approximately(_thumbnailRect.width, windowSize.x)
                || !Mathf.Approximately(_thumbnailRect.height, windowSize.y);
            Rect docked = CalculateDockedRect(mainWindowRect, Screen.width, Screen.height);
            bool companionOnRight = docked.x >= mainWindowRect.xMax;
            bool autoSideChanged = !_thumbnailAutoSideKnown || _thumbnailAutoOnRight != companionOnRight;
            if (_thumbnailRectSynced && !screenChanged && !sizeChanged
                && (_thumbnailPositionX.Value >= 0f || !autoSideChanged)) return;

            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
            if (_thumbnailPositionX.Value >= 0f && _thumbnailPositionY.Value >= 0f)
            {
                _thumbnailRect = WindowPlacement.FromNormalized(
                    _thumbnailPositionX.Value,
                    _thumbnailPositionY.Value,
                    windowSize.x,
                    windowSize.y,
                    Screen.width,
                    Screen.height);
            }
            else
            {
                float x = WindowPlacement.CalculateThumbnailAutoX(
                    docked.x, docked.width, companionOnRight, windowSize.x);
                _thumbnailRect = new Rect(x, docked.y, windowSize.x, windowSize.y);
                _thumbnailAutoOnRight = companionOnRight;
                _thumbnailAutoSideKnown = true;
            }

            _thumbnailRect = WindowPlacement.ClampToScreen(_thumbnailRect, Screen.width, Screen.height);
            _thumbnailRectSynced = true;
        }

        private bool CanDrawThumbnail()
            => _expanded && _showThumbnails.Value && _thumbnail != null;

        private void SaveThumbnailPosition()
        {
            Vector2 normalized = WindowPlacement.ToNormalized(_thumbnailRect, Screen.width, Screen.height);
            _thumbnailPositionX.Value = normalized.x;
            _thumbnailPositionY.Value = normalized.y;
            _thumbnailPositionDirty = false;
        }
    }

    internal static class WindowPlacement
    {
        internal static float CalculateThumbnailAutoX(
            float companionX, float companionWidth, bool companionOnRight, float previewWidth)
        {
            return companionOnRight
                ? companionX - previewWidth
                : companionX + companionWidth;
        }

        internal static Rect FromNormalized(
            float normalizedX,
            float normalizedY,
            float width,
            float height,
            float screenWidth,
            float screenHeight)
        {
            return ClampToScreen(
                new Rect(normalizedX * screenWidth, normalizedY * screenHeight, width, height),
                screenWidth,
                screenHeight);
        }

        internal static Vector2 ToNormalized(Rect rect, float screenWidth, float screenHeight)
        {
            float x = screenWidth > 0f ? rect.x / screenWidth : 0f;
            float y = screenHeight > 0f ? rect.y / screenHeight : 0f;
            return new Vector2(Mathf.Clamp01(x), Mathf.Clamp01(y));
        }

        internal static Rect ClampToScreen(Rect rect, float screenWidth, float screenHeight)
        {
            float maxX = Mathf.Max(0f, screenWidth - rect.width);
            float maxY = Mathf.Max(0f, screenHeight - rect.height);
            rect.x = Mathf.Clamp(rect.x, 0f, maxX);
            rect.y = Mathf.Clamp(rect.y, 0f, maxY);
            return rect;
        }

        internal static bool PositionChanged(Rect before, Rect after)
            => !Mathf.Approximately(before.x, after.x)
                || !Mathf.Approximately(before.y, after.y);
    }
}
