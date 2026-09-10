using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace EC_LogFilter
{
    internal sealed class LogEventInterceptor : IDisposable
    {
        private static LogEventInterceptor _active;
        private readonly Harmony _harmony;
        private readonly LogStatistics _statistics;
        private volatile bool _running;
        private volatile Exception _fault;

        internal LogEventInterceptor(LogStatistics statistics, string harmonyId)
        {
            _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
            _harmony = new Harmony(harmonyId);
        }

        internal bool IsInstalled { get; private set; }
        internal bool IsRunning => _running;
        internal string ErrorDetail => _fault == null ? string.Empty : _fault.Message;

        internal bool Install()
        {
            if (IsInstalled) return _running;
            if (_active != null && _active != this)
            {
                _fault = new InvalidOperationException("日志拦截器已经存在。");
                return false;
            }
            try
            {
                MethodInfo target = typeof(BepInEx.Logging.Logger).GetMethod("InternalLogEvent",
                    BindingFlags.Static | BindingFlags.NonPublic, null,
                    new[] { typeof(object), typeof(LogEventArgs) }, null);
                if (target == null) throw new MissingMethodException("找不到 BepInEx 日志分发入口。");
                _active = this;
                _running = true;
                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(LogEventInterceptor), nameof(Prefix)));
                IsInstalled = true;
                return true;
            }
            catch (Exception ex)
            {
                _fault = ex;
                Dispose();
                return false;
            }
        }

        private static bool Prefix(LogEventArgs __1)
        {
            LogEventInterceptor owner = _active;
            if (owner == null || !owner._running) return true;
            try
            {
                if (__1 == null || __1.Source == null) return true;
                return owner._statistics.Observe(__1.Source.SourceName, __1.Level);
            }
            catch (Exception ex)
            {
                // 此处不能记录日志；主线程读取故障，后续事件直接放行。
                owner._fault = ex;
                owner._running = false;
                return true;
            }
        }

        public void Dispose()
        {
            _running = false;
            if (_active == this) _active = null;
            try { _harmony.UnpatchSelf(); }
            catch (Exception ex) { if (_fault == null) _fault = ex; }
            IsInstalled = false;
        }
    }
}
