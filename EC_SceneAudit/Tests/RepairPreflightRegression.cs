using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EC_SceneAudit.Export;
using EC_SceneAudit.Repair;

internal static class RepairPreflightRegression
{
    private const string BundleId = "cb8ba68c5b704397bb11cd946dc3b592";
    private const string SceneTitle = "laa2-S-part1";
    private const string UidA = "IrvHPj";
    private const string UidB = "W58ioW";
    private static readonly string ScriptA =
        "死了\n<script>\r\n<event-select>\r\n<goto-part name=\"#S2-1-5\" index=\"24\"/>\r\n</event-select>\r\n</script>";
    private static readonly string ScriptB =
        "<script>\r\n<load-script file=\"edit/scene/laa/Scripts/globalVariables.xml\"/>\r\n</script>";

    private static int assertions;
    private static readonly RepairPreflight Preflight = new RepairPreflight();

    private static int Main(string[] args)
    {
        try
        {
            var output = Path.GetFullPath(args.Length > 0 ? args[0] : ".tmp/repair-preflight-tests");
            var work = Path.Combine(output, "bundle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);

            VerifyPlanReaderRules();
            VerifyJsonReaderRules();

            var bundle = WriteBundle(work, BuildScene(false, false, false));
            VerifyHappyPath(work, bundle);
            VerifyEmptyAfter(work, bundle);
            VerifyReorderStillLocates(work, bundle);
            VerifySceneDrift(work, bundle);
            VerifyAmbiguousTargets(work, bundle);
            VerifySourceBeforeCurrent(work, bundle);
            VerifyAllIndexLevels(work);
            VerifyHashChecks(work);
            VerifyPathEscape(work, bundle);
            VerifyRealSample();

            Console.WriteLine("PASS: " + assertions + " repair preflight assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static RepairBundleSource WriteBundle(string root, SceneSnapshot scene)
    {
        Directory.CreateDirectory(root);
        var sceneJson = JsonSnapshotWriter.Serialize(scene);
        var sceneBytes = new UTF8Encoding(false).GetBytes(sceneJson);
        File.WriteAllBytes(Path.Combine(root, "scene.json"), sceneBytes);

        var manifest = new BundleManifest { bundleId = BundleId, scene = scene.scene, collection = new DependencyCollection() };
        manifest.files.Add(new BundleFile
        {
            fileId = "scene",
            role = "scene",
            path = "scene.json",
            sha256 = ReviewBundleWriter.Hash(sceneBytes),
            byteLength = sceneBytes.Length
        });
        var manifestBytes = new UTF8Encoding(false).GetBytes(JsonSnapshotWriter.SerializeBundle(manifest));
        File.WriteAllBytes(Path.Combine(root, "bundle.json"), manifestBytes);
        File.WriteAllText(Path.Combine(root, "bundle.sha256"), ReviewBundleWriter.Hash(manifestBytes) + Environment.NewLine);
        return RepairBundleReader.Read(root);
    }

    private static void VerifyHappyPath(string work, RepairBundleSource bundle)
    {
        var planPath = Path.Combine(work, "plan.json");
        File.WriteAllText(planPath, PlanJson(
            bundle.BundleId, bundle.ManifestSha256,
            Change("/nodes/0/part/cuts/0/textBoxes/1/layouts/0/rawText", UidA, 0, 1, 0, ScriptA,
                "死了\n<script>\n<event-select>\n<goto-part name=\"S2-1-5\" index=\"24\"/>\n</event-select>\n</script>"),
            Change("/nodes/1/part/cuts/0/textBoxes/1/layouts/0/rawText", UidB, 0, 1, 0, ScriptB, "")),
            new UTF8Encoding(false));
        var report = Preflight.Run(planPath, work, bundle.Scene);
        Check(report.Passed, "happy path passes");
        Check(report.Items.Count == 2 && report.PassedCount == 2, "two items verified");
        Check(report.Items[0].Label.StartsWith("S2-2A", StringComparison.Ordinal), "part name label");
        Check(report.Items[0].Label.Contains("选项"), "option flag from source box");
        Check(report.Items[1].Label.Contains("选项") == false, "non-option box stays plain");
        Check(RepairPreflight.FormatText(report).Contains("原文一致"), "text report states verdict");
        Check(RepairPreflight.FormatText(report).Contains(
            "资料包清单哈希：" + bundle.ManifestSha256 + Environment.NewLine),
            "text report identifies the bundle manifest hash");
        Check(!RepairPreflight.FormatText(report).Contains(Environment.NewLine + "清单哈希："),
            "text report avoids ambiguous hash label");
    }

    private static void VerifyEmptyAfter(string work, RepairBundleSource bundle)
    {
        var plan = RepairPlanReader.Parse(PlanJson(
            bundle.BundleId, bundle.ManifestSha256,
            Change("/nodes/1/part/cuts/0/textBoxes/1/layouts/0/rawText", UidB, 0, 1, 0, ScriptB, "")));
        Check(plan.Changes[0].After.Length == 0, "empty after accepted");
        var report = Preflight.Evaluate(plan, "memory", work, bundle, bundle.Scene);
        Check(report.Passed, "empty after preflight passes");
    }

    private static void VerifyReorderStillLocates(string work, RepairBundleSource bundle)
    {
        var plan = RepairPlanReader.Parse(PlanJson(
            bundle.BundleId, bundle.ManifestSha256,
            Change("/nodes/0/part/cuts/0/textBoxes/1/layouts/0/rawText", UidA, 0, 1, 0, ScriptA, "改了")));
        var reordered = BuildScene(false, false, false);
        Array.Reverse(reordered.nodes);
        foreach (var node in reordered.nodes)
        {
            if (node.part?.cuts == null)
                continue;
            foreach (var cut in node.part.cuts)
                Array.Reverse(cut.textBoxes);
        }
        var report = Preflight.Evaluate(plan, "memory", work, bundle, reordered);
        Check(report.Passed, "source index lookup survives array reordering");
    }

    private static void VerifySceneDrift(string work, RepairBundleSource bundle)
    {
        var pointerA = "/nodes/0/part/cuts/0/textBoxes/1/layouts/0/rawText";
        var plan = RepairPlanReader.Parse(PlanJson(
            bundle.BundleId, bundle.ManifestSha256, Change(pointerA, UidA, 0, 1, 0, ScriptA, "改了")));

        var renamed = BuildScene(true, false, false);
        Check(!Preflight.Evaluate(plan, "memory", work, bundle, renamed).Passed, "current text change is a conflict");

        var otherUid = BuildScene(false, false, false);
        otherUid.nodes[0].uid = "other";
        Check(!Preflight.Evaluate(plan, "memory", work, bundle, otherUid).Passed, "unknown current uid is a conflict");

        var differentTitle = BuildScene(false, false, false);
        differentTitle.scene.title = "another-scene";
        Check(!Preflight.Evaluate(plan, "memory", work, bundle, differentTitle).Passed, "different title is rejected");

        var addedNode = BuildScene(false, false, false);
        var list = new List<NodeSnapshot>(addedNode.nodes);
        list.Add(new NodeSnapshot { uid = "extra", name = "extra", outputs = new OutputSlotSnapshot[0] });
        addedNode.nodes = list.ToArray();
        Check(!Preflight.Evaluate(plan, "memory", work, bundle, addedNode).Passed, "extra node is rejected");

        var warning = BuildScene(false, false, false);
        warning.nodes[0].part.cuts[0].textBoxes[1].layouts[0].warnings =
            new[] { new SnapshotWarning("text-layout-read-failed", "文本布局不可用。") };
        Check(!Preflight.Evaluate(plan, "memory", work, bundle, warning).Passed, "unavailable layout is a conflict");

        var missingLayout = RepairPlanReader.Parse(PlanJson(
            bundle.BundleId, bundle.ManifestSha256,
            Change("/nodes/0/part/cuts/0/textBoxes/1/layouts/5/rawText", UidA, 0, 1, 5, ScriptA, "改了")));
        var missingReport = Preflight.Evaluate(missingLayout, "memory", work, bundle, bundle.Scene);
        Check(!missingReport.Passed && missingReport.Items[0].SourceIssues.Count > 0, "missing layout rejected");

        var wrongCut = RepairPlanReader.Parse(PlanJson(
            bundle.BundleId, bundle.ManifestSha256,
            Change("/nodes/0/part/cuts/0/textBoxes/1/layouts/0/rawText", UidA, 7, 1, 0, ScriptA, "改了")));
        Check(!Preflight.Evaluate(wrongCut, "memory", work, bundle, bundle.Scene).Passed, "cut source index mismatch");

        var wrongBox = RepairPlanReader.Parse(PlanJson(
            bundle.BundleId, bundle.ManifestSha256,
            Change("/nodes/0/part/cuts/0/textBoxes/1/layouts/0/rawText", UidA, 0, 4, 0, ScriptA, "改了")));
        Check(!Preflight.Evaluate(wrongBox, "memory", work, bundle, bundle.Scene).Passed, "text box source index mismatch");

        var wrongUid = RepairPlanReader.Parse(PlanJson(
            bundle.BundleId, bundle.ManifestSha256,
            Change("/nodes/0/part/cuts/0/textBoxes/1/layouts/0/rawText", "nope", 0, 1, 0, ScriptA, "改了")));
        Check(!Preflight.Evaluate(wrongUid, "memory", work, bundle, bundle.Scene).Passed, "node uid mismatch");

        var wrongBefore = RepairPlanReader.Parse(PlanJson(
            bundle.BundleId, bundle.ManifestSha256,
            Change("/nodes/0/part/cuts/0/textBoxes/1/layouts/0/rawText", UidA, 0, 1, 0, ScriptA + "x", "改了")));
        Check(!Preflight.Evaluate(wrongBefore, "memory", work, bundle, bundle.Scene).Passed, "before mismatch against source");

        var wrongSource = RepairPlanReader.Parse(PlanJson(
            "another-bundle", bundle.ManifestSha256, Change(pointerA, UidA, 0, 1, 0, ScriptA, "改了")));
        var wrongSourceReport = Preflight.Evaluate(wrongSource, "memory", work, bundle, bundle.Scene);
        Check(!wrongSourceReport.Passed && wrongSourceReport.GlobalIssues.Count > 0, "bundle id mismatch is global");
    }

    private static void VerifyHashChecks(string work)
    {
        var root = Path.Combine(work, "hash-" + Guid.NewGuid().ToString("N"));
        var bundle = WriteBundle(root, BuildScene(false, false, false));
        var manifestPath = Path.Combine(root, "bundle.json");
        var original = File.ReadAllText(manifestPath);

        File.WriteAllText(manifestPath, original.Replace(BundleId, BundleId + "0"));
        ExpectFailure(() => RepairBundleReader.Read(root), "bundled manifest hash");

        File.WriteAllText(manifestPath, original);
        var scenePath = Path.Combine(root, "scene.json");
        File.WriteAllText(scenePath, File.ReadAllText(scenePath) + " ");
        ExpectFailure(() => RepairBundleReader.Read(root), "scene member hash");

        File.WriteAllText(scenePath, JsonSnapshotWriter.Serialize(BuildScene(false, false, false)));
        var resolved = RepairBundleReader.Read(root);
        Check(resolved.BundleId == bundle.BundleId, "restored bundle reads");
    }

    private static void VerifyAmbiguousTargets(string work, RepairBundleSource bundle)
    {
        var plan = RepairPlanReader.Parse(PlanJson(bundle.BundleId, bundle.ManifestSha256,
            Change(PointerA, UidA, 0, 1, 0, ScriptA, "改")));
        Action<SceneSnapshot>[] mutate = {
            scene => scene.nodes = new[] { scene.nodes[0], scene.nodes[0], scene.nodes[1] },
            scene => scene.nodes[0].part.cuts = new[] { scene.nodes[0].part.cuts[0], scene.nodes[0].part.cuts[0] },
            scene => scene.nodes[0].part.cuts[0].textBoxes[0].sourceIndex = 1,
            scene => scene.nodes[0].part.cuts[0].textBoxes[1].layouts = new[] {
                scene.nodes[0].part.cuts[0].textBoxes[1].layouts[0], scene.nodes[0].part.cuts[0].textBoxes[1].layouts[0] },
            scene => scene.nodes[0].part.uid = "different-part",
            scene => scene.nodes[0].part.cuts[0].textBoxes[1].id++,
            scene => scene.nodes[0].part.cuts[0].textBoxes[1].option = false
        };
        foreach (var change in mutate)
        {
            var current = BuildScene(false, false, false);
            change(current);
            Check(!Preflight.Evaluate(plan, "memory", work, bundle, current).Passed, "ambiguous or changed identity rejected");
        }
        var json = JsonSnapshotWriter.Serialize(bundle.Scene);
        ExpectFailure(() => SceneSnapshotJson.Read(json.Replace("\"warnings\": []", "\"warnings\": {}")), "invalid warnings type");
    }

    private static void VerifySourceBeforeCurrent(string work, RepairBundleSource bundle)
    {
        var path = Path.Combine(work, "callback-plan.json");
        var calls = 0;
        Func<SceneSnapshot> current = () => { calls++; return bundle.Scene; };
        File.WriteAllText(path, PlanJson("wrong-bundle", bundle.ManifestSha256,
            Change(PointerA, UidA, 0, 1, 0, ScriptA, "改")));
        Check(!Preflight.Run(path, work, current).Passed && calls == 0, "wrong source does not read current game");
        File.WriteAllText(path, PlanJson(bundle.BundleId, bundle.ManifestSha256,
            Change(PointerA, UidA, 0, 1, 0, ScriptA + "x", "改")));
        Check(!Preflight.Run(path, work, current).Passed && calls == 0, "bad before does not read current game");
        File.WriteAllText(path, PlanJson(bundle.BundleId, bundle.ManifestSha256,
            Change(PointerA, UidA, 0, 1, 0, ScriptA, "改")), new UTF8Encoding(true));
        Check(Preflight.Run(path, work, current).Passed && calls == 1, "BOM plan reads current once after source validation");
        File.WriteAllText(path, "{}");
        var failed = Preflight.Run(path, work, current);
        Check(!failed.Passed && failed.Items.Count == 0 && calls == 1, "failed rerun contains no previous success");
    }

    private static void VerifyAllIndexLevels(string work)
    {
        var source = BuildScene(false, false, false);
        var node = source.nodes[0];
        var cut = node.part.cuts[0];
        cut.sourceIndex = 58;
        var box = cut.textBoxes[1];
        box.layouts = new[] { new TextLayoutSnapshot { layoutIndex = 1, rawText = "alternate", warnings = new SnapshotWarning[0] }, box.layouts[0] };
        Array.Reverse(cut.textBoxes);
        node.part.cuts = new[] { new CutSnapshot { sourceIndex = 5, textBoxes = new TextBoxSnapshot[0] }, cut };
        Array.Reverse(source.nodes);
        var root = Path.Combine(work, "all-indices");
        var bundle = WriteBundle(root, source);
        var plan = RepairPlanReader.Parse(PlanJson(bundle.BundleId, bundle.ManifestSha256,
            Change("/nodes/1/part/cuts/1/textBoxes/0/layouts/1/rawText", UidA, 58, 1, 0, ScriptA, "改")));
        var current = SceneSnapshotJson.Read(JsonSnapshotWriter.Serialize(source));
        Array.Reverse(current.nodes);
        Array.Reverse(current.nodes[0].part.cuts);
        Array.Reverse(current.nodes[0].part.cuts[0].textBoxes);
        Array.Reverse(current.nodes[0].part.cuts[0].textBoxes[1].layouts);
        Check(Preflight.Evaluate(plan, "memory", root, bundle, current).Passed, "all array slots differ from source indices");
    }

    private static void VerifyPathEscape(string work, RepairBundleSource bundle)
    {
        var root = Path.Combine(work, "escape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var relative in new[] { "../outside.json", "/absolute.json", "sub/../../outside.json" })
        {
            try
            {
                RepairBundleReader.ResolveMemberPath(root, relative);
                Check(false, "escaping path rejected: " + relative);
            }
            catch (RepairInputException)
            {
                Check(true, "escaping path rejected: " + relative);
            }
        }
        ExpectFailure(() => RepairBundleReader.Read(Path.Combine(work, "missing-" + Guid.NewGuid().ToString("N"))),
            "missing bundle directory");
    }

    private static void VerifyPlanReaderRules()
    {
        var noOp = PlanJson(BundleId, Hash, Change(PointerA, UidA, 0, 1, 0, ScriptA, ScriptA));
        ExpectFailure(() => RepairPlanReader.Parse(noOp), "no-op change");

        var duplicate = PlanJson(
            BundleId, Hash,
            Change(PointerA, UidA, 0, 1, 0, ScriptA, "改了"),
            Change(PointerA, UidA, 0, 1, 0, ScriptA, "又改"));
        ExpectFailure(() => RepairPlanReader.Parse(duplicate), "duplicate pointer");

        ExpectFailure(() => RepairPlanReader.Parse(
            PlanJson(BundleId, Hash, Change(PointerA, UidA, 0, 1, 0, ScriptA, "改"))
                .Replace("\"ec-scene-repair-plan\"", "\"other-format\"")),
            "unknown format");
        ExpectFailure(() => RepairPlanReader.Parse(
            PlanJson(BundleId, Hash, Change(PointerA, UidA, 0, 1, 0, ScriptA, "改")).Replace("\"version\": 1", "\"version\": 2")),
            "unsupported version");
        ExpectFailure(() => RepairPlanReader.Parse(
            PlanJson(BundleId, Hash, Change("/nodes/0/part/cuts/0/textBoxes/1/layouts/0/rawText", UidA, 0, 1, 0, ScriptA, "改"))
                .Replace("/nodes/0/part/cuts/0/textBoxes/1/layouts/0/rawText", "/nodes/0/part/cuts/0/textBoxes/1/layouts/0/other")),
            "pointer must end in rawText");
        ExpectFailure(() => RepairPlanReader.Parse(
            PlanJson(BundleId, Hash, Change(PointerA, UidA, 0, 1, 0, ScriptA, "改")).Replace("\"cutSourceIndex\": 0", "\"cutSourceIndex\": 1.5")),
            "non-integer index");
        ExpectFailure(() => RepairPlanReader.Parse(
            PlanJson(BundleId, Hash, Change(PointerA, UidA, 0, 1, 0, ScriptA, "改")).Replace("\"before\":", "\"beforeX\":")),
            "missing before field");
        ExpectFailure(() => RepairPlanReader.Parse(
            PlanJson(BundleId, Hash, Change(PointerA, UidA, 0, 1, 0, ScriptA, "改")).Replace("\"after\": \"改\"", "\"after\": 3")),
            "after must be text");
        ExpectFailure(() => RepairPlanReader.Parse(
            PlanJson(BundleId, Hash, Change(PointerA, UidA, 0, 1, 0, ScriptA, new string('x', 1000001)))),
            "after over limit");
        ExpectFailure(() => RepairPlanReader.Parse(
            PlanJson(BundleId, Hash, Change(PointerA, UidA, 0, 1, 0, ScriptA, "改")).Replace("\"manifestSha256\": \"" + Hash + "\"",
                "\"manifestSha256\": \"short\"")),
            "bad manifest hash format");

        var entries = new List<string>();
        for (var index = 0; index < 1001; index++)
            entries.Add(Change("/nodes/0/part/cuts/" + index + "/textBoxes/1/layouts/0/rawText", UidA, 0, 1, 0, ScriptA, "改"));
        ExpectFailure(() => RepairPlanReader.Parse(PlanJson(BundleId, Hash, entries.ToArray())), "too many changes");

        var integerLike = RepairPlanReader.Parse(
            PlanJson(BundleId, Hash, Change(PointerA, UidA, 0, 1, 0, ScriptA, "改")).Replace("\"layoutIndex\": 0", "\"layoutIndex\": 0.0"));
        Check(integerLike.Changes[0].Target.LayoutIndex == 0, "1.0 counted as integer like JavaScript");

        var extra = RepairPlanReader.Parse(
            PlanJson(BundleId, Hash, Change(PointerA, UidA, 0, 1, 0, ScriptA, "改")).Replace("\"changes\":", "\"note\": \"ignored\",\n  \"changes\":"));
        Check(extra.Changes.Count == 1, "unknown fields ignored");
        Check(RepairPlanReader.Parse(PlanJson(BundleId, Hash)).Changes.Count == 0, "empty changes accepted");
    }

    private static void VerifyJsonReaderRules()
    {
        Check(RepairJson.Parse("{\"a\":null}").Member("a").IsNull, "explicit null kept");
        Check(RepairJson.Parse("{\"a\":null}").Member("b") == null, "missing member distinct from null");
        Check(RepairJson.Parse("[1e2]").Items[0].TryGetInteger(out var expanded) && expanded == 100, "exponent integer");
        ExpectFailure(() => RepairJson.Parse("{\"a\":1,}"), "trailing comma");
        ExpectFailure(() => RepairJson.Parse("\"unterminated"), "unterminated string");
        ExpectFailure(() => RepairJson.Parse(""), "empty json");
        Check(RepairJson.Parse("\"\\u4e2d\"").Text == "中", "unicode escape decoded");
        Check(RepairJson.Parse("{\"a\":1,\"a\":2}").Member("a").Text == "2", "duplicate keys keep last value");
        Check(RepairJson.Parse("\"\\r\\n\\t\\\\\\\"\\uD83D\\uDE00\"").Text == "\r\n\t\\\"\uD83D\uDE00", "all escapes and surrogate pair preserved");
        Check(!RepairLayoutPointer.TryParse(PointerA + "\n", out _), "pointer trailing newline rejected");
        foreach (var malformed in new[] { "[1,]", "01", "1.", "1e", "\"\\x20\"", "true false" })
            ExpectFailure(() => RepairJson.Parse(malformed), "malformed JSON: " + malformed);
        ExpectFailure(() => RepairPlanReader.DecodeUtf8(new byte[] { 0xff }, "fixture"), "invalid UTF8");
    }

    private static void VerifyRealSample()
    {
        var bundlePath = Environment.GetEnvironmentVariable("REPAIR_PREFLIGHT_BUNDLE");
        var planPath = Environment.GetEnvironmentVariable("REPAIR_PREFLIGHT_PLAN");
        if (string.IsNullOrEmpty(bundlePath) || string.IsNullOrEmpty(planPath))
        {
            Console.WriteLine("NOTE: REPAIR_PREFLIGHT_BUNDLE/PLAN not set; real sample checks skipped.");
            return;
        }

        var planBytes = File.ReadAllBytes(planPath);
        var manifestBytes = File.ReadAllBytes(Path.Combine(bundlePath, "bundle.json"));
        var sceneBytes = File.ReadAllBytes(Path.Combine(bundlePath, "scene.json"));
        var report = Preflight.Run(planPath, bundlePath, RepairBundleReader.Read(bundlePath).Scene);
        Check(report.Passed, "real sample plan matches its F8 bundle");
        Check(report.Items.Count == 2 && report.PassedCount == 2, "real sample has two matching changes");
        Check(report.BundleId == "cb8ba68c5b704397bb11cd946dc3b592", "real sample bundle id");
        Check(ReviewBundleWriter.Hash(planBytes) == "a0f64468350e28c35140191611d8e6362042a4a483eb2c1d3c9afa2d8e9b9f20",
            "real sample plan hash");
        Check(HashOf(Path.Combine(bundlePath, "bundle.json")) == HashOfBytes(manifestBytes), "bundle.json untouched");
        Check(HashOf(Path.Combine(bundlePath, "scene.json")) == HashOfBytes(sceneBytes), "scene.json untouched");
    }

    private static SceneSnapshot BuildScene(bool changeCurrentText, bool changeCutOrder, bool changeBoxOrder)
    {
        var scene = new SceneSnapshot
        {
            schemaVersion = "1.0",
            scene = new SceneMetadata { title = SceneTitle, exportedAt = "2026-10-04T13:37:16.2146667Z", gameVersion = "1.0", plugins = new string[0] },
            crossPartReferences = new string[0],
            warnings = new SnapshotWarning[0],
            nodes = new[]
            {
                BuildNode(UidA, "S2-2A", ScriptA, true, changeCurrentText),
                BuildNode(UidB, "S2-3-4", ScriptB, false, changeCurrentText)
            }
        };
        if (changeCutOrder || changeBoxOrder)
            Array.Reverse(scene.nodes[0].part.cuts);
        return scene;
    }

    private static NodeSnapshot BuildNode(string uid, string name, string rawText, bool option, bool changeCurrentText)
    {
        var layout = new TextLayoutSnapshot
        {
            layoutIndex = 0,
            rawText = changeCurrentText ? rawText + " // current change" : rawText,
            scriptBlocks = new ScriptBlockSnapshot[0],
            variableReferences = new VariableReferenceSnapshot[0],
            warnings = new SnapshotWarning[0]
        };
        var filler = new TextLayoutSnapshot { layoutIndex = 0, rawText = "filler", scriptBlocks = new ScriptBlockSnapshot[0], variableReferences = new VariableReferenceSnapshot[0], warnings = new SnapshotWarning[0] };
        return new NodeSnapshot
        {
            uid = uid,
            name = name,
            kind = "ADV",
            outputs = new OutputSlotSnapshot[0],
            part = new PartSnapshot
            {
                uid = uid,
                name = name,
                cuts = new[]
                {
                    new CutSnapshot
                    {
                        sourceIndex = 0,
                        textBoxes = new[]
                        {
                            new TextBoxSnapshot { sourceIndex = 0, id = 23, option = false, layouts = new[] { Clone(filler) } },
                            new TextBoxSnapshot { sourceIndex = 1, id = 24, option = option, layouts = new[] { Clone(layout) } }
                        }
                    }
                }
            }
        };
    }

    private static TextLayoutSnapshot Clone(TextLayoutSnapshot source)
    {
        return new TextLayoutSnapshot
        {
            layoutIndex = source.layoutIndex,
            rawText = source.rawText,
            scriptBlocks = new ScriptBlockSnapshot[0],
            variableReferences = new VariableReferenceSnapshot[0],
            warnings = source.warnings
        };
    }

    private const string PointerA = "/nodes/0/part/cuts/0/textBoxes/1/layouts/0/rawText";
    private const string Hash = "c3c4aa98261024dcbffcf20d794e54db383d25bd377f52591a5400adfa2fbf29";

    private static string PlanJson(string bundleId, string manifestSha256, params string[] changes)
    {
        var builder = new StringBuilder();
        builder.Append("{\n  \"format\": \"ec-scene-repair-plan\",\n  \"version\": 1,\n  \"source\": {\n    \"bundleId\": \"")
            .Append(bundleId).Append("\",\n    \"manifestSha256\": \"").Append(manifestSha256)
            .Append("\"\n  },\n  \"changes\": [");
        for (var index = 0; index < changes.Length; index++)
        {
            if (index > 0)
                builder.Append(',');
            builder.Append('\n').Append(changes[index]);
        }
        builder.Append("\n  ]\n}\n");
        return builder.ToString();
    }

    private static string Change(string pointer, string nodeUid, int cutSourceIndex, int textBoxSourceIndex, int layoutIndex, string before, string after)
    {
        return "    {\n      \"target\": {\n        \"reference\": {\"fileId\": \"scene\", \"kind\": \"json-pointer\", \"pointer\": \""
            + pointer + "\"},\n        \"nodeUid\": \"" + nodeUid + "\",\n        \"cutSourceIndex\": " + cutSourceIndex
            + ",\n        \"textBoxSourceIndex\": " + textBoxSourceIndex + ",\n        \"layoutIndex\": " + layoutIndex
            + "\n      },\n      \"before\": \"" + Escape(before) + "\",\n      \"after\": \"" + Escape(after) + "\"\n    }";
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }
        return builder.ToString();
    }

    private static void ExpectFailure(Action action, string label)
    {
        try
        {
            action();
            Check(false, "expected failure: " + label);
        }
        catch (RepairInputException)
        {
            Check(true, "expected failure: " + label);
        }
    }

    private static string HashOf(string path)
    {
        return HashOfBytes(File.ReadAllBytes(path));
    }

    private static string HashOfBytes(byte[] bytes)
    {
        return ReviewBundleWriter.Hash(bytes);
    }

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition)
            throw new InvalidOperationException(label);
    }
}
