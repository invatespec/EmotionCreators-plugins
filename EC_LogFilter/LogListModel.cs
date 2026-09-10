using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;

namespace EC_LogFilter
{
    internal enum SourceSort { Total, Blocked, Name }

    internal sealed class LogListModel
    {
        internal static readonly LogLevel[] Levels =
            { LogLevel.Debug, LogLevel.Info, LogLevel.Message, LogLevel.Warning, LogLevel.Error, LogLevel.Fatal };
        private readonly LogStatistics _statistics;
        private readonly Dictionary<string, LogRow> _known = new Dictionary<string, LogRow>(StringComparer.Ordinal);
        private readonly List<LogRow> _rows = new List<LogRow>();
        private string _filter = string.Empty;
        private SourceSort _sort;
        private bool _orderDirty = true;
        private bool _hasOrder;
        private double _nextRefresh;

        internal LogListModel(LogStatistics statistics) { _statistics = statistics; }
        internal IReadOnlyList<LogRow> Rows => _rows;
        internal LogRow Selected { get; private set; }
        internal string Summary { get; private set; } = "等待日志";

        internal string Filter
        {
            get => _filter;
            set { if (_filter == value) return; _filter = value ?? string.Empty; _orderDirty = true; }
        }

        internal SourceSort Sort
        {
            get => _sort;
            set { if (_sort == value) return; _sort = value; _orderDirty = true; }
        }

        internal void RequestRefresh() { _nextRefresh = 0; }

        internal void Update(double now, bool holdOrder)
        {
            if (now >= _nextRefresh)
            {
                RefreshCounts();
                _nextRefresh = now + 1;
                _orderDirty = true;
            }
            if (_orderDirty && (!holdOrder || !_hasOrder)) Reorder();
        }

        internal void Select(string name)
        {
            if (_known.TryGetValue(name, out LogRow row)) Selected = row;
        }

        internal void SelectOnRelease(string pressedName, string releasedName)
        {
            if (pressedName != null && string.Equals(pressedName, releasedName, StringComparison.Ordinal))
                Select(releasedName);
        }

        private void RefreshCounts()
        {
            long total = 0;
            long blocked = 0;
            foreach (SourceSnapshot snapshot in _statistics.Capture())
            {
                if (!_known.TryGetValue(snapshot.Name, out LogRow row))
                {
                    row = new LogRow(snapshot.Name);
                    _known.Add(snapshot.Name, row);
                }
                row.Update(snapshot);
                total += snapshot.Total;
                blocked += snapshot.Blocked;
            }
            Summary = "来源 " + _known.Count + "  |  累计 " + LogRow.Number(total) + "  |  已屏蔽 " + LogRow.Number(blocked);
        }

        private void Reorder()
        {
            // 行对象按原始名称保持身份；悬停期间只更新数字，不增删或移动行。
            _rows.Clear();
            foreach (LogRow row in _known.Values)
                if (row.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0) _rows.Add(row);
            _rows.Sort(CompareRows);
            if (Selected == null && _rows.Count > 0) Selected = _rows[0];
            _orderDirty = false;
            _hasOrder = true;
        }

        private int CompareRows(LogRow left, LogRow right)
        {
            int comparison = _sort == SourceSort.Total ? right.Total.CompareTo(left.Total)
                : _sort == SourceSort.Blocked ? right.Blocked.CompareTo(left.Blocked) : 0;
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Name, right.Name);
        }
    }

    internal sealed class LogRow
    {
        internal readonly string Name;
        internal readonly string Label;
        internal readonly string[] LevelCounts = new string[6];
        private readonly long[] _levelValues = new long[6];
        private bool _initialized;
        private long _other;
        internal long Total { get; private set; }
        internal long Blocked { get; private set; }
        internal string TotalText { get; private set; }
        internal string BlockedText { get; private set; }
        internal string Detail { get; private set; }
        internal string OtherText { get; private set; }

        internal LogRow(string name)
        {
            Name = name;
            Label = name.Length == 0 ? "(空名称)" : name.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        internal void Update(SourceSnapshot snapshot)
        {
            bool summaryChanged = !_initialized || Total != snapshot.Total || Blocked != snapshot.Blocked;
            if (!_initialized || Total != snapshot.Total) TotalText = Number(snapshot.Total);
            if (!_initialized || Blocked != snapshot.Blocked) BlockedText = Number(snapshot.Blocked);
            Total = snapshot.Total;
            Blocked = snapshot.Blocked;
            if (summaryChanged) Detail = Total == 0 ? "本次尚未出现日志" : "累计 " + TotalText + "  |  已屏蔽 " + BlockedText;
            if (!_initialized || _other != snapshot.Other) OtherText = "其他等级（始终放行）：" + Number(snapshot.Other);
            _other = snapshot.Other;
            for (int i = 0; i < LevelCounts.Length; i++)
            {
                long count = snapshot.Count(LogListModel.Levels[i]);
                if (!_initialized || _levelValues[i] != count) LevelCounts[i] = Number(count);
                _levelValues[i] = count;
            }
            _initialized = true;
        }

        internal static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
