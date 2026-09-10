using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;

namespace EC_LogFilter
{
    internal sealed class LogStatistics
    {
        internal const int KnownMask = 63;
        private readonly ConcurrentDictionary<string, SourceState> _sources =
            new ConcurrentDictionary<string, SourceState>(StringComparer.Ordinal);
        private volatile bool _paused;

        internal LogStatistics(IEnumerable<RuleEntry> rules = null)
        {
            if (rules == null) return;
            foreach (RuleEntry rule in rules)
                SetRule(rule.Source, rule.Blocked);
        }

        internal bool Paused { get => _paused; set => _paused = value; }
        internal int SourceCount => _sources.Count;

        internal bool Observe(string sourceName, LogLevel level)
        {
            SourceState state = GetSource(sourceName);
            int bits = (int)level;
            int mask = state.BlockedMask;
            bool blocked = !_paused && bits != 0 && (bits & ~mask) == 0;
            state.Record(bits, blocked);
            return !blocked;
        }

        internal LogLevel GetRule(string sourceName)
        {
            return _sources.TryGetValue(sourceName ?? string.Empty, out SourceState state)
                ? (LogLevel)state.BlockedMask : LogLevel.None;
        }

        internal bool SetRule(string sourceName, LogLevel levels)
        {
            if (((int)levels & ~KnownMask) != 0)
                throw new ArgumentOutOfRangeException(nameof(levels));
            SourceState state = GetSource(sourceName);
            if (state.BlockedMask == (int)levels) return false;
            state.BlockedMask = (int)levels;
            return true;
        }

        internal SourceSnapshot[] Capture()
        {
            // 弱一致枚举不持有整表锁，新来源最迟进入下一次快照。
            var result = new List<SourceSnapshot>();
            foreach (KeyValuePair<string, SourceState> pair in _sources)
                result.Add(pair.Value.Capture());
            return result.ToArray();
        }

        internal List<RuleEntry> ExportRules()
        {
            var result = new List<RuleEntry>();
            foreach (KeyValuePair<string, SourceState> pair in _sources)
            {
                SourceState state = pair.Value;
                LogLevel mask = (LogLevel)state.BlockedMask;
                if (mask != LogLevel.None)
                    result.Add(new RuleEntry { Source = state.Name, Blocked = mask });
            }
            result.Sort((left, right) => StringComparer.Ordinal.Compare(left.Source, right.Source));
            return result;
        }

        private SourceState GetSource(string name)
        {
            name = name ?? string.Empty;
            if (_sources.TryGetValue(name, out SourceState existing)) return existing;
            return _sources.GetOrAdd(name, new SourceState(name));
        }

        private sealed class SourceState
        {
            internal readonly string Name;
            internal volatile int BlockedMask;
            private readonly long[] _levels = new long[6];
            private long _total;
            private long _blocked;
            private long _other;

            internal SourceState(string name) { Name = name; }

            internal void Record(int bits, bool blocked)
            {
                Interlocked.Increment(ref _total);
                for (int i = 0; i < _levels.Length; i++)
                {
                    if ((bits & (1 << i)) != 0)
                        Interlocked.Increment(ref _levels[i]);
                }
                if (bits == 0 || (bits & ~KnownMask) != 0)
                    Interlocked.Increment(ref _other);
                if (blocked) Interlocked.Increment(ref _blocked);
            }

            internal SourceSnapshot Capture()
            {
                var levels = new long[6];
                for (int i = 0; i < levels.Length; i++)
                    levels[i] = Interlocked.Read(ref _levels[i]);
                long blocked = Interlocked.Read(ref _blocked);
                long other = Interlocked.Read(ref _other);
                // 总数先于子项递增，最后读取可避免显示已屏蔽数大于累计数。
                long total = Interlocked.Read(ref _total);
                return new SourceSnapshot(Name, total, blocked, other, levels);
            }
        }
    }

    internal sealed class SourceSnapshot
    {
        internal readonly string Name;
        internal readonly long Total;
        internal readonly long Blocked;
        internal readonly long Other;
        private readonly long[] _levels;

        internal SourceSnapshot(string name, long total, long blocked, long other, long[] levels)
        {
            Name = name;
            Total = total;
            Blocked = blocked;
            Other = other;
            _levels = levels;
        }

        internal long Count(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Fatal: return _levels[0];
                case LogLevel.Error: return _levels[1];
                case LogLevel.Warning: return _levels[2];
                case LogLevel.Message: return _levels[3];
                case LogLevel.Info: return _levels[4];
                case LogLevel.Debug: return _levels[5];
                default: return Other;
            }
        }
    }
}
