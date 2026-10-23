using System.Collections.Generic;

namespace EC_SceneAudit.Export
{
    internal sealed class SnapshotScript
    {
        public string raw;
        public EvidenceReference source;
    }

    internal static class SnapshotScripts
    {
        public static IEnumerable<SnapshotScript> Enumerate(SceneSnapshot snapshot)
        {
            var nodes = snapshot.nodes ?? new NodeSnapshot[0];
            for (var n = 0; n < nodes.Length; n++)
            {
                var cuts = nodes[n]?.part?.cuts ?? new CutSnapshot[0];
                for (var c = 0; c < cuts.Length; c++)
                {
                    foreach (var script in CutScripts(cuts[c], "/nodes/" + n + "/part/cuts/" + c))
                        yield return script;
                }
            }
        }

        private static IEnumerable<SnapshotScript> CutScripts(CutSnapshot cut, string prefix)
        {
            var boxes = cut?.textBoxes ?? new TextBoxSnapshot[0];
            for (var b = 0; b < boxes.Length; b++)
            {
                var layouts = boxes[b]?.layouts ?? new TextLayoutSnapshot[0];
                for (var l = 0; l < layouts.Length; l++)
                {
                    foreach (var script in LayoutScripts(layouts[l], prefix + "/textBoxes/" + b + "/layouts/" + l))
                        yield return script;
                }
            }
        }

        private static IEnumerable<SnapshotScript> LayoutScripts(TextLayoutSnapshot layout, string prefix)
        {
            var blocks = layout?.scriptBlocks ?? new ScriptBlockSnapshot[0];
            for (var s = 0; s < blocks.Length; s++)
                yield return new SnapshotScript
                {
                    raw = blocks[s]?.raw ?? string.Empty,
                    source = new EvidenceReference { fileId = "scene", pointer = prefix + "/scriptBlocks/" + s + "/raw" }
                };
        }
    }
}
