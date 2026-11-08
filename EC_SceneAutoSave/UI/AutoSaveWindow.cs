using System;
using System.Collections.Generic;
using System.IO;
using EC_SceneAutoSave.Core;
using EC_SceneAutoSave.Storage;
using UnityEngine;

namespace EC_SceneAutoSave.UI
{
    // 面板只显示状态与发出请求；保存与应用都排到下一次 Update 执行，不在 OnGUI 里改场景。
    internal sealed class AutoSaveWindow
    {
        private const int WindowId = 0x45435341;
        private const float Width = 380f;
        private static AutoSaveWindow instance;

        private readonly AutoSaveController controller;
        private readonly AutoSaveSettings settings;
        private readonly WindowStyles styles = new WindowStyles();
        private bool visible;
        private bool unfocusRequested;
        private Rect windowRect = new Rect(0f, 0f, Width, 10f);
        private Rect guiRect;
        private int screenWidth;
        private int screenHeight;
        private bool moved;
        private string confirmDir;
        private List<SessionInfo> describedList;
        private readonly List<string> descriptions = new List<string>();

        public AutoSaveWindow(AutoSaveController controller, AutoSaveSettings settings)
        {
            this.controller = controller;
            this.settings = settings;
            instance = this;
        }

        public bool Visible => visible;

        public static bool IsVisible => instance != null && instance.visible;

        public static bool IsMouseOverWindow()
        {
            if (instance == null || !instance.visible)
                return false;
            return instance.guiRect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
        }

        public void Open()
        {
            visible = true;
            screenWidth = 0;
        }

        public void Close()
        {
            SavePosition();
            visible = false;
            unfocusRequested = true;
            confirmDir = null;
            guiRect = new Rect();
            WindowInputGuard.Reset();
        }

        public void Toggle()
        {
            if (visible) Close();
            else Open();
        }

        public void Dispose()
        {
            Close();
            styles.Dispose();
            if (instance == this)
                instance = null;
        }

        public void OnGUI()
        {
            // 焦点在下一事件开头释放，点击当帧直接 UnfocusWindow 会让 InputHotkeyBlock 误判。
            if (unfocusRequested || (visible && Event.current.type == EventType.MouseDown
                && !guiRect.Contains(Event.current.mousePosition)))
            {
                GUIUtility.keyboardControl = 0;
                GUI.UnfocusWindow();
                unfocusRequested = false;
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
                Place();
                guiRect = windowRect;
                WindowInputGuard.Sample();
                var before = windowRect.position;
                windowRect = GUILayout.Window(WindowId, windowRect, Draw, GUIContent.none, styles.Window, GUILayout.Width(Width));
                windowRect = Clamp(windowRect);
                moved |= before != windowRect.position;
                guiRect = visible ? windowRect : new Rect();
                if (Event.current.rawType == EventType.MouseUp)
                    SavePosition();
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

        // 分辨率变化或重新打开时按归一化坐标重新展开。
        private void Place()
        {
            if (screenWidth == Screen.width && screenHeight == Screen.height)
                return;
            screenWidth = Screen.width;
            screenHeight = Screen.height;
            windowRect.x = (screenWidth - windowRect.width) * Mathf.Clamp01(settings.WindowX.Value);
            windowRect.y = (screenHeight - windowRect.height) * Mathf.Clamp01(settings.WindowY.Value);
            windowRect = Clamp(windowRect);
            moved = false;
        }

        // 窗口原点必须落在整数像素上：按比例换算出的小数坐标会让字形跨像素插值，文字发虚。
        private static Rect Clamp(Rect rect)
        {
            rect.x = Mathf.Round(Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, Screen.width - rect.width)));
            rect.y = Mathf.Round(Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, Screen.height - rect.height)));
            return rect;
        }

        private void SavePosition()
        {
            if (!moved)
                return;
            moved = false;
            settings.WindowX.Value = Mathf.Clamp01(windowRect.x / Mathf.Max(1f, screenWidth - windowRect.width));
            settings.WindowY.Value = Mathf.Clamp01(windowRect.y / Mathf.Max(1f, screenHeight - windowRect.height));
        }

        private void Draw(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("场景自动保存", styles.Header);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("关闭", styles.Button, GUILayout.Width(64f)))
                Close();
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            DrawStatus();
            DrawRecoverable();
            if (!string.IsNullOrEmpty(controller.Notice))
            {
                GUILayout.Space(6f);
                GUILayout.Label(controller.Notice, styles.Warning);
            }
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 32f));
        }

        private void DrawStatus()
        {
            var state = controller.State;
            GUILayout.Label("状态：" + controller.StatusText, styles.Label);
            if (state == null)
            {
                if (controller.Mode == AutoSaveMode.Inactive)
                    GUILayout.Label("读档或原生保存一次后开始自动保存。", styles.Label);
                return;
            }
            GUILayout.Label("基准：" + Path.GetFileName(state.Base.Path), styles.Label);
            GUILayout.Label("会话：" + (state.Dir == null ? "尚无改动" : Path.GetFileName(state.Dir)), styles.Label);
            if (state.Seq > 0 && state.LastCommitLocal != default(DateTime))
                GUILayout.Label($"上次自动保存：{state.LastCommitLocal:HH:mm:ss}，{state.LastPartCount} 个 Part，"
                    + $"{FormatBytes(state.LastBytes)}，{state.LastMillis} ms", styles.Label);
            if (!string.IsNullOrEmpty(state.LastError))
                GUILayout.Label(state.LastError, styles.Warning);
            if (controller.Mode != AutoSaveMode.Active)
                return;
            GUILayout.Label(controller.WaitHint ?? string.Empty, styles.Label);
            if (GUILayout.Button("立即保存", styles.Button))
                controller.RequestSave();
        }

        private void DrawRecoverable()
        {
            var list = controller.Recoverable;
            if (list.Count == 0)
                return;
            Describe(list);
            GUILayout.Space(8f);
            GUILayout.Label("可恢复的自动保存（基于当前存档）", styles.Header);
            var hint = controller.ApplyHint;
            if (hint != null)
                GUILayout.Label(hint, styles.Warning);
            var canApply = hint == null && controller.Mode == AutoSaveMode.Active && !controller.Applying;
            for (var i = 0; i < list.Count; i++)
            {
                var dir = list[i].Dir;
                GUILayout.BeginHorizontal();
                GUILayout.Label(descriptions[i], styles.Label);
                GUI.enabled = canApply;
                var confirming = confirmDir == dir;
                if (GUILayout.Button(confirming ? "确认应用" : "应用", styles.Button, GUILayout.Width(88f)))
                {
                    if (confirming)
                    {
                        confirmDir = null;
                        controller.RequestApply(dir);
                    }
                    else
                    {
                        confirmDir = dir;
                    }
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
        }

        // 列表只在控制器刷新时换新实例，描述文本随之重建，切换列表时取消待确认。
        private void Describe(List<SessionInfo> list)
        {
            if (ReferenceEquals(list, describedList))
                return;
            describedList = list;
            confirmDir = null;
            descriptions.Clear();
            foreach (var session in list)
                descriptions.Add(DescribeSession(session));
        }

        private static string DescribeSession(SessionInfo session)
        {
            var manifest = session.Manifest;
            var ticks = manifest.UpdatedTicksUtc;
            var time = ticks > 0 && ticks <= DateTime.MaxValue.Ticks
                ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm:ss")
                : "未知时间";
            var parts = new List<string> { manifest.RecordedPartCount + " 个 Part" };
            if (manifest.Charas.Count > 0)
                parts.Add(manifest.Charas.Count + " 个角色");
            if (!string.IsNullOrEmpty(manifest.GraphBlob))
                parts.Add("节点图");
            if (!string.IsNullOrEmpty(manifest.MapsBlob))
                parts.Add("地图");
            if (!string.IsNullOrEmpty(manifest.InfoBlob))
                parts.Add("场景信息");
            return time + "  " + string.Join("、", parts.ToArray()) + (session.FromBackup ? "（备份清单）" : string.Empty);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024 * 1024)
                return (bytes / 1024f).ToString("0.0") + " KB";
            return (bytes / 1024f / 1024f).ToString("0.0") + " MB";
        }
    }
}
