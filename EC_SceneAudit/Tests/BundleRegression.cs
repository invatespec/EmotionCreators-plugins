using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using EC_SceneAudit.Export;

internal static class BundleRegression
{
    private static int assertions;
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    private static int Main(string[] args)
    {
        try
        {
            var output = Path.GetFullPath(args.Length > 0 ? args[0] : ".tmp/scene-review-tests");
            var root = Path.Combine(output, "userdata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            VerifyNodeGeometryMath();
            VerifyCollector(root);
            var writer = new ReviewBundleWriter();
            var snapshot = Snapshot("<script><load-script file=\"a.xml\"/></script>");
            var time = new DateTime(2026, 10, 4, 12, 0, 0);
            var one = writer.Write(snapshot, root, output, time);
            var two = writer.Write(snapshot, root, output, time);
            Check(one != two, "same millisecond exports are isolated");
            VerifyBundle(one);
            VerifyBundle(two);
            if (args.Length == 3)
            {
                var source = JsonSerializer.Deserialize<SceneSnapshot>(File.ReadAllText(args[1]), JsonOptions);
                var replay = writer.Write(source, args[2], output);
                VerifyBundle(replay);
                Console.WriteLine("REPLAY_BUNDLE=" + replay);
            }
            Console.WriteLine("FIXTURE_BUNDLE=" + one);
            Console.WriteLine("PASS: " + assertions + " bundle assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void VerifyCollector(string root)
    {
        File.WriteAllText(Path.Combine(root, "a.xml"),
            "<script><!-- <load-script file=\"unrelated.xml\"/> --><if value1=\"1\"><load-script file=\"b.xml\"/></if></script>");
        File.WriteAllText(Path.Combine(root, "b.xml"), "<script><load-script file=\"a.xml\"/></script>");
        File.WriteAllText(Path.Combine(root, "unrelated.xml"), "<script/>");
        var collector = new ExternalScriptCollector(root);
        var snapshot = Snapshot("<script><load-script file=\"a.xml\"/><load-script file=\"a.xml\"/>"
            + "<load-script file=\"missing.xml\"/><load-script file=\"#dynamic#\"/>"
            + "<load-script file=\"../outside.xml\"/></script>");
        var result = collector.Collect(snapshot);
        Check(result.scripts.Count == 2, "cycle deduplication and unrelated exclusion");
        Check(result.dependencies.Count == 7, "all duplicate and cycle edges retained");
        Check(result.dependencies.Count(d => d.status == "included") == 4, "shared dependencies");
        foreach (var status in new[] { "missing", "dynamic", "outside-userdata" })
            Check(result.dependencies.Any(d => d.status == status), status);
        Check(result.dependencies.Any(d => d.context.Any(c => c.StartsWith("if "))), "condition context");
        foreach (var limits in new[] { new CollectionLimits { files = 1 }, new CollectionLimits { depth = 1 } })
        {
            var limited = new ExternalScriptCollector(root, limits).Collect(snapshot);
            Check(limited.scripts.Count == 1 && limited.dependencies.Any(d => d.status == "limit"), "count/depth limit");
        }
        var small = new ExternalScriptCollector(root, new CollectionLimits { fileBytes = 4 }).Collect(snapshot);
        Check(small.scripts.Count == 0 && small.dependencies.Any(d => d.status == "limit"), "byte limit");

        File.WriteAllText(Path.Combine(root, "bad.xml"), "<script><load-script file=\"unrelated.xml\"/></bad>");
        var bad = collector.Collect(Snapshot("<script><load-script file=\"bad.xml\"/></script>"));
        Check(bad.scripts.Count == 1 && bad.scripts[0].file.parseStatus == "invalid", "invalid XML body retained");
        Check(bad.dependencies.Count == 1 && bad.limitations.Any(), "invalid XML does not expand");
        File.WriteAllBytes(Path.Combine(root, "encoding.xml"), new byte[] { 0xff });
        var encoding = collector.Collect(Snapshot("<script><load-script file=\"encoding.xml\"/></script>"));
        Check(encoding.scripts.Count == 1 && encoding.scripts[0].file.encoding == null, "undecodable bytes retained");
        Check(encoding.limitations.Any(l => l.code == "encoding-unknown"), "encoding state");
        File.WriteAllText(Path.Combine(root, "wide.xml"), "<script/>", new UnicodeEncoding(false, true));
        var wide = collector.Collect(Snapshot("<script><load-script file=\"wide.xml\"/></script>"));
        Check(wide.scripts[0].file.encoding == "utf-16le" && wide.scripts[0].file.parseStatus == "valid", "UTF16 BOM");
        var dtd = collector.Collect(Snapshot("<!DOCTYPE script [<!ENTITY x SYSTEM \"file:///secret\">]><script>&x;</script>"));
        Check(dtd.scripts.Count == 0 && dtd.limitations.Any(), "DTD not resolved");
        var lockedPath = Path.Combine(root, "locked.xml");
        File.WriteAllText(lockedPath, "<script/>");
        using (var locked = File.Open(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedResult = collector.Collect(Snapshot("<script><load-script file=\"locked.xml\"/></script>"));
            Check(lockedResult.dependencies[0].status == "unreadable", "locked dependency");
        }
    }

    private static SceneSnapshot Snapshot(string script)
    {
        var layout = new VariableModScanner().Scan(script, "", null, 1);
        return new SceneSnapshot
        {
            schemaVersion = "1.0",
            scene = new SceneMetadata { title = "测试 Scene", exportedAt = DateTime.UtcNow.ToString("o"), plugins = new string[0] },
            geometry = new SceneGeometrySnapshot
            {
                version = 1,
                coordinateSpace = "grid-ui-top-left-y-down",
                canvas = new GeometryCanvasSnapshot { width = 4080f, height = 2850f },
                nodes = new[] { new GeometryNodeSnapshot
                {
                    uid = "test", status = "exact",
                    frame = new GeometryFrameSnapshot { x = 862f, y = 508f, width = 184f, height = 66f },
                    positionOffset = new Vector2Snapshot { x = 0f, y = 0f },
                    input = new GeometryPointSnapshot { enabled = true, x = 92f, y = 33f },
                    outputs = new[] { new GeometryOutputPointSnapshot { slot = 0, enabled = true, x = 184f, y = 33f } }
                } }
            },
            warnings = new SnapshotWarning[0], crossPartReferences = new string[0],
            nodes = new[] { new NodeSnapshot
            {
                uid = "test", name = "测试 Part", kind = "ADV", outputs = new OutputSlotSnapshot[0],
                part = new PartSnapshot
                {
                    uid = "test", name = "测试 Part", type = "ADV", kind = 1,
                    cuts = new[] { new CutSnapshot { sourceIndex = 0, textBoxes = new[]
                    {
                        new TextBoxSnapshot { sourceIndex = 0, layouts = new[] { layout } }
                    } } }
                }
            } }
        };
    }

    private static void VerifyBundle(string directory)
    {
        var manifestBytes = File.ReadAllBytes(Path.Combine(directory, "bundle.json"));
        Check(ReviewBundleWriter.Hash(manifestBytes) == File.ReadAllText(Path.Combine(directory, "bundle.sha256")).Trim(), "manifest hash");
        using (var json = JsonDocument.Parse(manifestBytes))
        {
            Check(json.RootElement.GetProperty("version").GetInt32() == 1, "bundle version");
            foreach (var file in json.RootElement.GetProperty("files").EnumerateArray())
            {
                var bytes = File.ReadAllBytes(Path.Combine(directory, file.GetProperty("path").GetString()));
                Check(ReviewBundleWriter.Hash(bytes) == file.GetProperty("sha256").GetString(), "member hash");
                Check(bytes.Length == file.GetProperty("byteLength").GetInt32(), "member length");
            }
        }
        using (var summary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "ai-summary.json"))))
            Check(summary.RootElement.GetProperty("sourceEvidenceFile").GetString() == "scene.json", "relative source");
        using (var scene = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "scene.json"))))
        {
            var geometry = scene.RootElement.GetProperty("geometry");
            var node = geometry.GetProperty("nodes")[0];
            Check(node.GetProperty("input").GetProperty("x").GetSingle() == 92f, "input x relative to frame");
            Check(node.GetProperty("input").GetProperty("y").GetSingle() == 33f, "input y relative to frame");
            Check(node.GetProperty("outputs")[0].GetProperty("x").GetSingle() == 184f, "output x relative to frame");
            Check(node.GetProperty("outputs")[0].GetProperty("y").GetSingle() == 33f, "output y relative to frame");
        }
    }

    private static void VerifyNodeGeometryMath()
    {
        var frame = new GeometryFrameSnapshot { x = 862f, y = 508f, width = 184f, height = 66f };
        float x;
        float y;
        Check(NodeGeometryMath.ToRelativePoint(954f, 541f, frame, out x, out y)
            && x == 92f && y == 33f, "absolute port becomes frame-relative");
        var moved = new GeometryFrameSnapshot { x = 1062f, y = 708f, width = 184f, height = 66f };
        float movedX;
        float movedY;
        Check(NodeGeometryMath.ToRelativePoint(1154f, 741f, moved, out movedX, out movedY)
            && movedX == x && movedY == y, "translation preserves relative port");
        float offsetX;
        float offsetY;
        Check(NodeGeometryMath.TryComputePositionOffset(910f, 560f, 900f, -530f, out offsetX, out offsetY)
            && offsetX == 10f && offsetY == 30f, "nonzero native frame offset retained");
        float outsideX;
        float outsideY;
        Check(NodeGeometryMath.ToRelativePoint(5f, 600f, frame, out outsideX, out outsideY)
            && outsideX < 0f && outsideY > frame.height, "outside port is retained without clamping");
    }

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }
}
