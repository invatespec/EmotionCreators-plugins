using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace EC_LogFilter
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class LogFilterPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.ec.logfilter";
        public const string PluginName = "EC_LogFilter";
        public const string PluginVersion = "1.0.0";
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private ConfigEntry<KeyboardShortcut> _shortcut;
        private ConfigEntry<float> _positionX;
        private ConfigEntry<float> _positionY;
        private KeyCode _mainKey;
        private KeyCode[] _modifiers = new KeyCode[0];
        private LogStatistics _statistics;
        private RuleStore _store;
        private LogEventInterceptor _interceptor;
        private WindowInputGuard _inputGuard;
        private LogFilterWindow _window;
        private string _reportedError = string.Empty;
        private bool _writingPosition;
        private bool _shutdown;

        private void Awake()
        {
            try
            {
                BindConfig();
                string path = Path.Combine(Paths.GameRootPath, "UserData", "PluginData", PluginName, "rules.xml");
                _store = new RuleStore(path);
                _statistics = new LogStatistics(_store.Load());
                _window = new LogFilterWindow(_statistics, _store);
                OnShortcutChanged(null, EventArgs.Empty);
                OnPositionChanged(null, EventArgs.Empty);
                _interceptor = new LogEventInterceptor(_statistics, PluginGuid + ".logging");
                _interceptor.Install();
                _inputGuard = new WindowInputGuard(_window, PluginGuid + ".input");
                _inputGuard.Install();
                UpdateStatus();
                Logger.LogInfo(PluginName + " " + PluginVersion + " 已启动，窗口快捷键：" + _shortcut.Value);
            }
            catch (Exception ex)
            {
                _interceptor?.Dispose();
                _inputGuard?.Dispose();
                Logger.LogError("EC_LogFilter 初始化失败，未启用过滤：" + ex);
                enabled = false;
            }
        }

        private void BindConfig()
        {
            _shortcut = Config.Bind("Window", "ToggleShortcut",
                new KeyboardShortcut(KeyCode.L, KeyCode.LeftControl, KeyCode.LeftAlt), "打开或关闭日志过滤窗口。");
            _positionX = Config.Bind("Window", "PositionX", 0.5f,
                new ConfigDescription("窗口水平位置：0 为左侧，1 为右侧。", new AcceptableValueRange<float>(0, 1)));
            _positionY = Config.Bind("Window", "PositionY", 0.3f,
                new ConfigDescription("窗口垂直位置：0 为顶部，1 为底部。", new AcceptableValueRange<float>(0, 1)));
            _shortcut.SettingChanged += OnShortcutChanged;
            _positionX.SettingChanged += OnPositionChanged;
            _positionY.SettingChanged += OnPositionChanged;
        }

        private void Update()
        {
            if (_shutdown || _window == null) return;
            double now = _clock.Elapsed.TotalSeconds;
            if (ShortcutPressed()) _window.SetVisible(!_window.Visible);
            _window.Update(now);
            if (_window.TryTakePosition(out Vector2 position)) SavePosition(position);
            if (_store.NeedsSave(now)) _store.TrySave(_statistics.ExportRules());
            UpdateStatus();
        }

        private void OnGUI() { _window?.Draw(); }
        private void OnDisable() { _window?.SetVisible(false); }
        private void OnApplicationFocus(bool focused) { if (!focused) _window?.ReleaseInput(); }
        private void OnApplicationQuit() { Shutdown(); }
        private void OnDestroy() { Shutdown(); }

        private bool ShortcutPressed()
        {
            if (_mainKey == KeyCode.None || !Input.GetKeyDown(_mainKey)) return false;
            foreach (KeyCode modifier in _modifiers)
                if (!ModifierHeld(modifier)) return false;
            return true;
        }

        private static bool ModifierHeld(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftControl: case KeyCode.RightControl:
                    return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                case KeyCode.LeftAlt: case KeyCode.RightAlt:
                    return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                case KeyCode.LeftShift: case KeyCode.RightShift:
                    return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                default: return Input.GetKey(key);
            }
        }

        private void OnShortcutChanged(object sender, EventArgs args)
        {
            _mainKey = _shortcut.Value.MainKey;
            _modifiers = _shortcut.Value.Modifiers.ToArray();
            if (_window != null) _window.ShortcutText = _shortcut.Value.ToString();
        }

        private void OnPositionChanged(object sender, EventArgs args)
        {
            if (!_writingPosition) _window?.SetPosition(_positionX.Value, _positionY.Value);
        }

        private void SavePosition(Vector2 position)
        {
            bool autoSave = Config.SaveOnConfigSet;
            _writingPosition = true;
            Config.SaveOnConfigSet = false;
            try
            {
                _positionX.Value = position.x;
                _positionY.Value = position.y;
                Config.Save();
            }
            catch (Exception ex) { Logger.LogWarning("窗口位置保存失败：" + ex.Message); }
            finally { Config.SaveOnConfigSet = autoSave; _writingPosition = false; }
        }

        private void UpdateStatus()
        {
            string fault = _interceptor.IsRunning ? string.Empty : "日志拦截已停止：" + _interceptor.ErrorDetail;
            if (!_inputGuard.IsInstalled) fault += " 输入保护不可用：" + _inputGuard.ErrorDetail;
            _window.FaultText = fault;
            string error = fault.Length > 0 ? fault : _store.ErrorDetail;
            if (error.Length > 0 && error != _reportedError) Logger.LogWarning(error);
            _reportedError = error;
        }

        private void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            if (_store != null && _statistics != null && _store.CanSave && _store.HasPendingChanges)
                _store.TrySave(_statistics.ExportRules());
            _interceptor?.Dispose();
            _inputGuard?.Dispose();
            _window?.Dispose();
            if (_shortcut != null) _shortcut.SettingChanged -= OnShortcutChanged;
            if (_positionX != null) _positionX.SettingChanged -= OnPositionChanged;
            if (_positionY != null) _positionY.SettingChanged -= OnPositionChanged;
        }
    }
}
