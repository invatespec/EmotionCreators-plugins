using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace EC_BackVarSync
{
    /// <summary>
    /// EC_back 联动桥：反射缓存其公开静态状态，按名 patch 三个存/读/清入口。
    /// EC_back 未安装或方法改名时自动降级；挂接依据见 research/ec-back-hook-points.md。
    /// </summary>
    internal sealed class ECBackBridge
    {
        private const string TypeName = "EC_back.EC_backClass";

        /// <summary>Harmony postfix 必须静态，经此引用回桥实例。</summary>
        private static ECBackBridge _active;

        private readonly ManualLogSource _log;
        private readonly VarStore _store;

        private FieldInfo _bookmarkDirField;  // public static ConfigEntry<string>
        private FieldInfo _titleField;        // public static string（已净化的场景标题）
        private FieldInfo _bmarkField;        // public static string[]（"init" = 空槽）
        private FieldInfo _forceFlagField;    // public static bool

        // 面板跟随：EC_back 主窗口展开（hide_state.DISP）时把面板挂到它下面
        private FieldInfo _hstateField;       // public static hide_state（DISP=0/MINI=1/HIDE=2）
        private FieldInfo _windowRectField;   // public static Rect
        private FieldInfo _showFField;        // public static bool（截图期间 false）
        private FieldInfo _initFField;        // public static bool

        /// <summary>jump_bookmark 前后各读一次，false→true 才视为本次点击跳转有效。</summary>
        private static bool _forceBefore;

        public bool IsLinked { get; private set; }

        public ECBackBridge(ManualLogSource log, VarStore store)
        {
            _log = log;
            _store = store;
        }

        public bool TryLink(Harmony harmony)
        {
            Type type = AccessTools.TypeByName(TypeName);
            if (type == null)
            {
                _log.LogInfo("未检测到 EC_back，书签联动未启用（面板/自动存档/前进控制不受影响）");
                return false;
            }

            _bookmarkDirField = AccessTools.Field(type, "bookmark_dir");
            _titleField = AccessTools.Field(type, "title");
            _bmarkField = AccessTools.Field(type, "bmark");
            _forceFlagField = AccessTools.Field(type, "force_flag");
            _hstateField = AccessTools.Field(type, "hstate");
            _windowRectField = AccessTools.Field(type, "_windowRect");
            _showFField = AccessTools.Field(type, "show_f");
            _initFField = AccessTools.Field(type, "init_f");

            int patched = 0;
            patched += Patch(harmony, type, "rec_bookmark_one", nameof(RecBookmarkOnePostfix),
                "存书签时保存变量");
            patched += Patch(harmony, type, "jump_bookmark", nameof(JumpBookmarkPostfix),
                "读书签时恢复变量");
            patched += Patch(harmony, type, "write_bookmark_all", nameof(WriteBookmarkAllPostfix),
                "清理空槽位变量文件");

            if (patched == 0)
            {
                _log.LogWarning("EC_back 已加载但挂接点均未找到（版本不兼容？），书签联动不可用");
                return false;
            }

            _active = this;
            IsLinked = true;
            _log.LogInfo($"EC_back 书签联动已启用（挂接 {patched}/3 个入口）");
            return true;
        }

        private int Patch(Harmony harmony, Type type, string methodName, string handlerName, string purpose)
        {
            MethodInfo original = AccessTools.Method(type, methodName);
            if (original == null)
            {
                _log.LogWarning($"EC_back.{methodName} 未找到，{purpose}功能不可用");
                return 0;
            }

            MethodInfo handler = AccessTools.Method(typeof(ECBackBridge), handlerName);
            if (methodName == "jump_bookmark")
            {
                // 跳转有效性判断需要前后对比 force_flag，配一对 prefix
                MethodInfo prefix = AccessTools.Method(typeof(ECBackBridge), nameof(JumpBookmarkPrefix));
                harmony.Patch(original, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(handler));
            }
            else
            {
                harmony.Patch(original, postfix: new HarmonyMethod(handler));
            }
            return 1;
        }

        #region Harmony 挂接点（静态 + 防御）

        private static void RecBookmarkOnePostfix(int index)
        {
            try { _active?.OnRecBookmark(index); }
            catch (Exception ex) { _active?._log.LogWarning($"存书签联动异常: {ex.Message}"); }
        }

        private static void JumpBookmarkPrefix()
        {
            try { _forceBefore = _active != null && _active.ReadForceFlag(); }
            catch { _forceBefore = false; }
        }

        private static void JumpBookmarkPostfix(int index)
        {
            try { _active?.OnJumpBookmark(index); }
            catch (Exception ex) { _active?._log.LogWarning($"读书签联动异常: {ex.Message}"); }
        }

        private static void WriteBookmarkAllPostfix()
        {
            try { _active?.OnWriteBookmarkAll(); }
            catch (Exception ex) { _active?._log.LogWarning($"清理槽位联动异常: {ex.Message}"); }
        }

        #endregion

        private void OnRecBookmark(int index)
        {
            string bookmarkDir = GetBookmarkDir();
            string title = GetEcBackTitle();
            if (bookmarkDir == null || title == null)
                return;

            VarStore.TryGetCurrentPosition(out var partUid, out var partName, out var cutIndex);
            bool ok = _store.SaveBookmarkVars(bookmarkDir, title, index,
                VarStore.GetSceneTitle(), partUid, partName, cutIndex);

            if (ok)
                _log.LogMessage($"书签 {index} 变量已保存");
            else
                _log.LogWarning($"书签 {index} 变量保存失败");
        }

        private void OnJumpBookmark(int index)
        {
            // 原方法校验失败不置 force_flag；仅本次点击把 false→true 视为跳转有效。
            // 已有跳转挂起时（force 本就为 true）不恢复，宁可少恢复不可错恢复。
            if (_forceBefore || !ReadForceFlag())
                return;

            string bookmarkDir = GetBookmarkDir();
            string title = GetEcBackTitle();
            if (bookmarkDir == null || title == null)
                return;

            if (_store.TryRestoreBookmarkVars(bookmarkDir, title, index))
                _log.LogMessage($"书签 {index} 变量已恢复");
            else
                _log.LogInfo($"书签 {index} 无变量文件，跳过恢复");
        }

        private void OnWriteBookmarkAll()
        {
            string bookmarkDir = GetBookmarkDir();
            string title = GetEcBackTitle();
            if (bookmarkDir == null || title == null)
                return;

            var bmark = _bmarkField?.GetValue(null) as string[];
            _store.CleanupBookmarkVars(bookmarkDir, title, bmark);
        }

        #region 反射读取

        /// <summary>
        /// EC_back 主窗口展开（DISP、非截图中）时返回其窗口矩形。
        /// 用途：面板显隐联动（锚点出现/消失）与显示时的默认停靠位置；收起/截图/异常返回 false。
        /// </summary>
        public bool TryGetFollowAnchor(out Rect windowRect)
        {
            windowRect = default;

            if (!IsLinked || _hstateField == null || _windowRectField == null)
                return false;

            try
            {
                if (!ReadStaticBool(_initFField) || !ReadStaticBool(_showFField))
                    return false;
                if (Convert.ToInt32(_hstateField.GetValue(null)) != 0) // DISP=0 MINI=1 HIDE=2
                    return false;

                windowRect = (Rect)_windowRectField.GetValue(null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadStaticBool(FieldInfo field)
        {
            return field != null && (bool)field.GetValue(null);
        }

        private bool ReadForceFlag()
        {
            return _forceFlagField != null && (bool)_forceFlagField.GetValue(null);
        }

        private string GetBookmarkDir()
        {
            var entry = _bookmarkDirField?.GetValue(null) as ConfigEntry<string>;
            var value = entry?.Value;
            if (string.IsNullOrEmpty(value))
            {
                _log.LogWarning("EC_back bookmark_dir 不可读，书签联动跳过");
                return null;
            }
            return value;
        }

        private string GetEcBackTitle()
        {
            var title = _titleField?.GetValue(null) as string;
            if (string.IsNullOrEmpty(title))
            {
                // GUI 按钮触发时 title 必已初始化；为空说明异常状态，跳过本帧
                _log.LogWarning("EC_back title 为空，书签联动跳过");
                return null;
            }
            return title;
        }

        #endregion
    }
}
