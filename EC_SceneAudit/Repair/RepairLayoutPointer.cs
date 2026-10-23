using System.Globalization;
using System.Text.RegularExpressions;

namespace EC_SceneAudit.Repair
{
    internal sealed class RepairLayoutPosition
    {
        public int NodeIndex;
        public int CutIndex;
        public int TextBoxIndex;
        public int LayoutIndex;
    }

    // 修复清单只允许指向 scene 的 layout.rawText；下标是原始 JSON 数组位置，必须规范十进制。
    internal static class RepairLayoutPointer
    {
        public const string Root = "/nodes/";

        private static readonly Regex Pattern = new Regex(
            "^/nodes/(0|[1-9][0-9]*)/part/cuts/(0|[1-9][0-9]*)/textBoxes/(0|[1-9][0-9]*)/layouts/(0|[1-9][0-9]*)/rawText$",
            RegexOptions.CultureInvariant);

        public static bool TryParse(string pointer, out RepairLayoutPosition position)
        {
            position = null;
            if (string.IsNullOrEmpty(pointer))
                return false;
            var match = Pattern.Match(pointer);
            if (!match.Success || match.Length != pointer.Length)
                return false;
            if (!TryIndex(match.Groups[1].Value, out var nodeIndex)
                || !TryIndex(match.Groups[2].Value, out var cutIndex)
                || !TryIndex(match.Groups[3].Value, out var textBoxIndex)
                || !TryIndex(match.Groups[4].Value, out var layoutIndex))
                return false;
            position = new RepairLayoutPosition
            {
                NodeIndex = nodeIndex,
                CutIndex = cutIndex,
                TextBoxIndex = textBoxIndex,
                LayoutIndex = layoutIndex
            };
            return true;
        }

        private static bool TryIndex(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value >= 0;
        }
    }
}
