using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EC_SceneAudit.Export;
using EC_SceneAudit.Repair;

internal static class NodePatchRegression
{
    private const string BundleId = "cb8ba68c5b704397bb11cd946dc3b592";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static int assertions;

    private static int Main()
    {
        try
        {
            VerifyParser();
            VerifyStrictReader();
            VerifySelfTargetContract();
            VerifyPreflight();
            VerifyFullNodeStatePreflight();
            VerifyFalseWriteCompensation();
            VerifyExceptionCompensation();
            VerifyCompensationFailureHeld();
            VerifyCanvasPartialFailureReported();
            VerifyRestoreFailureReported();
            VerifyHeldSessionSemantics();
            VerifyRestoreSessionGate();
            VerifyRollbackHeldRetry();
            VerifyVerifier();
            Console.WriteLine("PASS: " + assertions + " node patch assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void VerifyParser()
    {
        var plan = NodePatchReader.Parse(PatchJson());
        Check(plan.Connections.Count == 1, "connection parsed");
        Check(plan.Layout != null && plan.Layout.Nodes.Count == 1, "layout parsed");
        Check(plan.Connections[0].After.TargetUid == "C", "connection target parsed");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"after\":{\"targetUid\":\"C\"}", "\"after\":{\"targetUid\":\"\"}")),
            "empty after rejected");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"after\":{\"targetUid\":\"C\"}", "\"after\":{\"targetUid\":\"A\"}")),
            "ordinary self target rejected by reader");
        var legacySelf = NodePatchReader.Parse(PatchJson().Replace("\"targetUid\":\"B\"", "\"targetUid\":\"A\""));
        Check(legacySelf.Connections[0].Before.TargetUid == "A" && legacySelf.Connections[0].After.TargetUid == "C",
            "before self evidence can be retargeted by reader");
    }

    private static void VerifySelfTargetContract()
    {
        var source = BuildScene();
        var current = BuildScene();
        var bundle = new RepairBundleSource { BundleId = BundleId, ManifestSha256 = Hash, Scene = source };

        var invalid = SamplePlan();
        invalid.Connections[0].After.TargetUid = "A";
        var invalidReport = new NodePatchPreflight().Evaluate(invalid, "patch.json", "bundle", bundle, current, true);
        Check(!invalidReport.Passed, "ordinary self target rejected by preflight");
        Check(invalidReport.Items.Exists(item => item.Issues.Exists(issue => issue.Contains("来源节点自身"))),
            "ordinary self target preflight reports source node");

        source.nodes[0].outputs[0].targetUid = "A";
        current.nodes[0].outputs[0].targetUid = "A";
        var legacy = SamplePlan();
        legacy.Connections[0].Before.TargetUid = "A";
        var legacyReport = new NodePatchPreflight().Evaluate(legacy, "patch.json", "bundle", bundle, current, true);
        Check(legacyReport.Passed, "before self evidence can be retargeted by preflight");
    }

    private static void VerifyPreflight()
    {
        var source = BuildScene();
        var current = BuildScene();
        var bundle = new RepairBundleSource
        {
            BundleId = BundleId,
            ManifestSha256 = Hash,
            Scene = source
        };
        var plan = SamplePlan();
        var report = new NodePatchPreflight().Evaluate(plan, "patch.json", "bundle", bundle, current, false);
        Check(report.Passed, "same scene and original slot pass preflight");
        Check(!report.RequiresCanvasExpansion, "same canvas does not require expansion");

        var oversized = SamplePlan();
        oversized.Layout.Canvas.Width = 5000f;
        var noApi = new NodePatchPreflight().Evaluate(oversized, "patch.json", "bundle", bundle, current, false);
        Check(noApi.RequiresCanvasExpansion && !noApi.CanvasSupportsAbsoluteResize, "expansion requirement detected");
        Check(!noApi.Passed && noApi.Items.Exists(item => item.Issues.Count > 0), "missing resize API blocks oversized layout");
    }

    // 严格 reader：未知字段、invalid 枚举、坏哈希与超限都在入口拒绝；正式枚举名来自 F8 导出的 EndConditionType.ToString()。
    private static void VerifyStrictReader()
    {
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"format\": \"ec-scene-node-patch\"", "\"format\": \"ec-scene-node-patch\", \"extra\": 1")), "unknown root field rejected");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"manifestSha256\": \"" + Hash + "\"", "\"manifestSha256\": \"" + Hash + "\", \"extra\": 1")), "unknown source field rejected");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"sourceUid\":\"A\"", "\"sourceUid\":\"A\",\"extra\":1")), "unknown connection field rejected");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"targetUid\":\"B\"", "\"targetUid\":\"B\",\"extra\":1")), "unknown before field rejected");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"after\":{\"targetUid\":\"C\"}", "\"after\":{\"targetUid\":\"C\",\"extra\":1}")), "unknown after field rejected");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"canvas\":{\"width\":5000,\"height\":3000}", "\"canvas\":{\"width\":5000,\"height\":3000,\"extra\":1}")), "unknown canvas field rejected");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"uid\":\"A\"", "\"uid\":\"A\",\"extra\":1")), "unknown layout node field rejected");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"before\":{\"x\":100,\"y\":-100}", "\"before\":{\"x\":100,\"y\":-100,\"extra\":1}")), "unknown position field rejected");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"conditionType\":\"ConnectPart\"", "\"conditionType\":\"none\"")), "sample conditionType none rejected");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace("\"conditionType\":\"ConnectPart\"", "\"conditionType\":\"not-a-type\"")), "unknown conditionType rejected");
        Check(NodePatchReader.Parse(PatchJson().Replace("\"conditionType\":\"ConnectPart\"", "\"conditionType\":\"NotUsed\"")).Connections[0].Before.ConditionType == "NotUsed", "NotUsed enum accepted");
        Check(NodePatchReader.Parse(PatchJson().Replace("\"conditionType\":\"ConnectPart\"", "\"conditionType\":\"AllRoot\"")).Connections[0].Before.ConditionType == "AllRoot", "AllRoot enum accepted");
        ExpectFailure(() => NodePatchReader.Parse(PatchJson().Replace(Hash, new string('z', 64))), "non-hex sha256 rejected");
        ExpectFailure(() => NodePatchReader.Parse(ManyConnectionsJson(NodePatchReader.MaxConnections + 1)), "connection limit enforced");
        ExpectFailure(() => NodePatchReader.Parse(ManyLayoutNodesJson(NodePatchReader.MaxLayoutNodes + 1)), "layout node limit enforced");
    }

    // 当前节点集合、类型、位置与五槽状态必须与来源包一致，连接-only 补丁也要核验未目标位置。
    private static void VerifyFullNodeStatePreflight()
    {
        var source = BuildScene();
        var bundle = new RepairBundleSource { BundleId = BundleId, ManifestSha256 = Hash, Scene = source };

        var slotDrift = BuildScene();
        slotDrift.nodes[1].outputs[1].conditionType = "NotUsed";
        slotDrift.nodes[1].outputs[1].targetUid = "C";
        var slotReport = new NodePatchPreflight().Evaluate(SamplePlan(), "patch.json", "bundle", bundle, slotDrift, true);
        Check(!slotReport.Passed, "untargeted slot drift blocks preflight");
        Check(slotReport.GlobalIssues.Exists(issue => issue.Contains("未目标槽位")), "slot drift reported as untargeted slot");

        var positionDrift = BuildScene();
        positionDrift.nodes[2].position.x = 999f;
        var positionReport = new NodePatchPreflight().Evaluate(SamplePlan(), "patch.json", "bundle", bundle, positionDrift, true);
        Check(!positionReport.Passed, "untargeted position drift blocks preflight");
        Check(positionReport.Items.Exists(item => item.Issues.Exists(issue => issue.Contains("位置"))), "position drift reported");

        // 连接-only 补丁没有布局项，未目标位置也必须由全局核验拦住。
        var connectionOnly = SamplePlan();
        connectionOnly.Layout = null;
        var connectionDrift = BuildScene();
        connectionDrift.nodes[2].position.x = 999f;
        var connectionReport = new NodePatchPreflight().Evaluate(connectionOnly, "patch.json", "bundle", bundle, connectionDrift, true);
        Check(!connectionReport.Passed, "connection-only untargeted position drift blocks preflight");
        Check(connectionReport.GlobalIssues.Exists(issue => issue.Contains("位置")), "connection-only position drift reported globally");

        var badUid = SamplePlan();
        badUid.Layout.Nodes.Add(new NodePlacementPatch
        {
            Uid = "ZZ",
            Before = new NodePositionValue { X = 0f, Y = 0f },
            After = new NodePositionValue { X = 1f, Y = 1f }
        });
        var uidReport = new NodePatchPreflight().Evaluate(badUid, "patch.json", "bundle", bundle, BuildScene(), true);
        Check(!uidReport.Passed, "layout placement with unknown uid blocks preflight");
        Check(uidReport.Items.Exists(item => item.Issues.Exists(issue => issue.Contains("不存在的节点"))), "unknown layout uid reported");
    }

    private static void VerifyFalseWriteCompensation()
    {
        var plan = SamplePlan();
        var host = new FakeHost();
        NodePatchSnapshot snapshot;
        string error;
        Check(NodePatchMemoryCapture.TryCapture(plan, host, out snapshot, out error), "capture small snapshot");
        host.FailSlotAfterWriteOnce = true;
        float width;
        float height;
        bool rollbackFailed;
        Check(!NodePatchTransaction.TryApply(plan, snapshot, host, false, 0f, 0f, out width, out height, out error, out rollbackFailed),
            "false-after-write fails transaction");
        Check(!rollbackFailed && host.SlotTarget("A", 0) == "B", "current failed slot is compensated");
        Check(host.Position("A") == 100f, "node position is compensated");
    }

    // 写入抛异常时当前项也必须补偿：异常路径与 false 路径一致。
    private static void VerifyExceptionCompensation()
    {
        var plan = SamplePlan();
        var host = new FakeHost();
        NodePatchSnapshot snapshot;
        string error;
        Check(NodePatchMemoryCapture.TryCapture(plan, host, out snapshot, out error), "capture exception snapshot");
        host.ThrowSlotAfterWriteOnce = true;
        float width;
        float height;
        bool rollbackFailed;
        Check(!NodePatchTransaction.TryApply(plan, snapshot, host, false, 0f, 0f, out width, out height, out error, out rollbackFailed),
            "exception-after-write fails transaction");
        Check(!rollbackFailed, "exception compensation succeeds");
        Check(host.SlotTarget("A", 0) == "B", "exception current slot compensated");
        Check(host.Position("A") == 100f, "exception node position compensated");
    }

    private static void VerifyCompensationFailureHeld()
    {
        var plan = SamplePlan();
        var host = new FakeHost();
        NodePatchSnapshot snapshot;
        string error;
        Check(NodePatchMemoryCapture.TryCapture(plan, host, out snapshot, out error), "capture held snapshot");
        host.FailSlotAfterWriteOnce = true;
        host.FailCompensation = true;
        float width;
        float height;
        bool rollbackFailed;
        Check(!NodePatchTransaction.TryApply(plan, snapshot, host, false, 0f, 0f, out width, out height, out error, out rollbackFailed),
            "compensation failure keeps failure result");
        Check(rollbackFailed && host.SlotTarget("A", 0) == "C", "failed compensation leaves after value for held session");
    }

    // 扩展 API 可能已改动画布后返回失败：失败报告必须给出实际画布，不能谎称仍是旧尺寸。
    private static void VerifyCanvasPartialFailureReported()
    {
        var plan = SamplePlan();
        var host = new FakeHost();
        NodePatchSnapshot snapshot;
        string error;
        Check(NodePatchMemoryCapture.TryCapture(plan, host, out snapshot, out error), "capture canvas snapshot");
        host.ResizeAppliesThenFails = true;
        float width;
        float height;
        bool rollbackFailed;
        Check(!NodePatchTransaction.TryApply(plan, snapshot, host, true, 5000f, 3000f, out width, out height, out error, out rollbackFailed),
            "partial canvas resize failure fails transaction");
        Check(!rollbackFailed, "partial canvas failure still compensates node/slot");
        Check(width == 5000f && height == 3000f, "failure report shows actual partially resized canvas");
        Check(error.Contains("5000"), "failure message names actual canvas");
        Check(host.Position("A") == 100f && host.SlotTarget("A", 0) == "B", "canvas failure leaves node/slot at before");
    }

    // 手动恢复失败要如实报告，不把补偿失败当成成功。
    private static void VerifyRestoreFailureReported()
    {
        var plan = SamplePlan();
        var host = new FakeHost();
        NodePatchSnapshot snapshot;
        string error;
        Check(NodePatchMemoryCapture.TryCapture(plan, host, out snapshot, out error), "capture restore-failure snapshot");
        host.FailCompensationAlways = true;
        bool rollbackFailed;
        Check(!NodePatchTransaction.TryRestore(snapshot, host, out error, out rollbackFailed), "restore failure reported");
        Check(rollbackFailed && !string.IsNullOrEmpty(error), "restore compensation failure is not masked");
    }

    // 补偿失败保留快照与写回锁（HasSession 覆盖 FailedRollbackHeld，普通 Failed 不持有）。
    private static void VerifyHeldSessionSemantics()
    {
        var report = new NodePatchApplyReport { Snapshot = new NodePatchSnapshot(new object(), new object(), new object()) };
        report.Phase = NodePatchApplyPhase.FailedRollbackHeld;
        Check(report.HasSession, "rollback-held keeps write lock");
        report.Phase = NodePatchApplyPhase.Failed;
        Check(!report.HasSession, "ordinary failure releases write lock");
        report.Phase = NodePatchApplyPhase.AppliedUnsaved;
        Check(report.HasSession, "applied-unsaved holds write lock");
        report.Phase = NodePatchApplyPhase.Restored;
        Check(!report.HasSession, "restored releases write lock");
    }

    // 恢复成功后会话结束；再次调用不能回写或把 Restored 改回有效会话。
    private static void VerifyRestoreSessionGate()
    {
        var plan = SamplePlan();
        var host = new FakeHost();
        NodePatchSnapshot snapshot;
        string error;
        Check(NodePatchMemoryCapture.TryCapture(plan, host, out snapshot, out error), "capture restore gate snapshot");
        float width;
        float height;
        bool rollbackFailed;
        Check(NodePatchTransaction.TryApply(plan, snapshot, host, false, 0f, 0f, out width, out height, out error, out rollbackFailed),
            "seed restore gate applied state");

        var report = new NodePatchApplyReport
        {
            Snapshot = snapshot,
            Phase = NodePatchApplyPhase.AppliedUnsaved
        };
        var engine = new NodePatchEngine();
        Check(engine.Restore(report, host), "first restore succeeds");
        Check(report.Phase == NodePatchApplyPhase.Restored && !report.HasSession, "first restore ends session");

        string writeError;
        Check(host.TryWritePlacement("A", new NodePatchPlacementValue { X = 321f, Y = -456f }, out writeError),
            "simulate later placement edit");
        Check(host.TryWriteSlotTarget("A", 0, "C", out writeError), "simulate later connection edit");
        Check(!engine.Restore(report, host), "second restore rejected after session ended");
        Check(report.Phase == NodePatchApplyPhase.Restored, "rejected restore keeps restored phase");
        Check(host.Position("A") == 321f && host.SlotTarget("A", 0) == "C",
            "rejected restore does not overwrite later edits");
    }

    // 补偿失败保留锁时仍允许用同一快照重试恢复；成功后锁和会话才释放。
    private static void VerifyRollbackHeldRetry()
    {
        var plan = SamplePlan();
        var host = new FakeHost();
        NodePatchSnapshot snapshot;
        string error;
        Check(NodePatchMemoryCapture.TryCapture(plan, host, out snapshot, out error), "capture held retry snapshot");
        host.FailSlotAfterWriteOnce = true;
        host.FailCompensation = true;
        float width;
        float height;
        bool rollbackFailed;
        Check(!NodePatchTransaction.TryApply(plan, snapshot, host, false, 0f, 0f, out width, out height, out error, out rollbackFailed),
            "held retry seed fails transaction");
        Check(rollbackFailed, "held retry seed keeps compensation failure");

        var report = new NodePatchApplyReport
        {
            Snapshot = snapshot,
            Phase = NodePatchApplyPhase.FailedRollbackHeld
        };
        report.Issues.Add(error);
        Check(report.HasSession, "held retry report keeps write lock");
        host.FailCompensation = false;

        var engine = new NodePatchEngine();
        Check(engine.Restore(report, host), "held session can retry restore");
        Check(report.Phase == NodePatchApplyPhase.Restored && !report.HasSession, "held retry success releases lock");
        Check(host.Position("A") == 100f && host.SlotTarget("A", 0) == "B", "held retry restores before state");
    }

    private static NodePatchPlan SamplePlan()
    {
        var plan = new NodePatchPlan
        {
            Source = new NodePatchSource { BundleId = BundleId, ManifestSha256 = Hash },
            Layout = new NodeLayoutPatch
            {
                Canvas = new NodeCanvasValue { Width = 4080f, Height = 2850f }
            }
        };
        plan.Connections.Add(new NodeConnectionPatch
        {
            SourceUid = "A",
            Slot = 0,
            Before = new NodeSlotValue
            {
                TargetUid = "B",
                ConditionType = "ConnectPart",
                ConditionCount = 0,
                FadeType = 0,
                FadeTime = 0.5f
            },
            After = new NodeConnectionTarget { TargetUid = "C" }
        });
        plan.Layout.Nodes.Add(new NodePlacementPatch
        {
            Uid = "A",
            Before = new NodePositionValue { X = 100f, Y = -100f },
            After = new NodePositionValue { X = 200f, Y = -200f }
        });
        return plan;
    }

    private static SceneSnapshot BuildScene()
    {
        var canvas = new GeometryCanvasSnapshot { width = 4080f, height = 2850f };
        var nodes = new[]
        {
            Node("A", "ADV", 100f, -100f, canvas, "B"),
            Node("B", "ADV", 400f, -100f, canvas, ""),
            Node("C", "ADV", 700f, -100f, canvas, "")
        };
        return new SceneSnapshot
        {
            schemaVersion = "1.0",
            scene = new SceneMetadata
            {
                title = "fixture",
                exportedAt = "2026-10-07T00:00:00.0000000Z",
                gameVersion = "1.0",
                plugins = new string[0]
            },
            geometry = new SceneGeometrySnapshot
            {
                version = 1,
                coordinateSpace = "grid-ui-top-left-y-down",
                canvas = canvas,
                nodes = new[]
                {
                    Geometry("A", 100f, 100f, true),
                    Geometry("B", 400f, 100f, true),
                    Geometry("C", 700f, 100f, true)
                }
            },
            nodes = nodes,
            warnings = new SnapshotWarning[0],
            crossPartReferences = new string[0]
        };
    }

    private static NodeSnapshot Node(string uid, string kind, float x, float y, GeometryCanvasSnapshot canvas, string target)
    {
        var outputs = new OutputSlotSnapshot[5];
        for (var slot = 0; slot < outputs.Length; slot++)
        {
            outputs[slot] = new OutputSlotSnapshot
            {
                slot = slot,
                targetUid = slot == 0 ? target : string.Empty,
                conditionType = "ConnectPart",
                conditionCount = 0,
                fadeType = 0,
                fadeTime = 0.5f
            };
        }
        return new NodeSnapshot
        {
            uid = uid,
            name = uid,
            kind = kind,
            position = new Vector2Snapshot { x = x, y = y },
            outputs = outputs
        };
    }

    private static GeometryNodeSnapshot Geometry(string uid, float frameX, float frameY, bool input)
    {
        return new GeometryNodeSnapshot
        {
            uid = uid,
            status = "exact",
            frame = new GeometryFrameSnapshot { x = frameX, y = frameY, width = 120f, height = 60f },
            positionOffset = new Vector2Snapshot { x = 0f, y = 0f },
            input = new GeometryPointSnapshot { enabled = input, x = 60f, y = 30f },
            outputs = new[]
            {
                new GeometryOutputPointSnapshot { slot = 0, enabled = true, x = 120f, y = 30f }
            }
        };
    }

    private static string PatchJson(string bundleId = BundleId, string hash = Hash)
    {
        return "{\n"
            + "  \"format\": \"ec-scene-node-patch\", \"version\": 1,\n"
            + "  \"source\": {\"bundleId\": \"" + bundleId + "\", \"manifestSha256\": \"" + hash + "\"},\n"
            + "  \"connections\": [{\"sourceUid\":\"A\",\"slot\":0,"
            + "\"before\":{\"targetUid\":\"B\",\"conditionType\":\"ConnectPart\",\"conditionCount\":0,\"fadeType\":0,\"fadeTime\":0.5},"
            + "\"after\":{\"targetUid\":\"C\"}}],\n"
            + "  \"layout\": {\"canvas\":{\"width\":5000,\"height\":3000},"
            + "\"nodes\":[{\"uid\":\"A\",\"before\":{\"x\":100,\"y\":-100},\"after\":{\"x\":200,\"y\":-200}}]}\n"
            + "}";
    }

    // 回验必须绑定来源包 hash、要求新 bundleId，并核对 after 与未目标状态。
    private static void VerifyVerifier()
    {
        var root = Path.Combine(Path.GetFullPath(".tmp/node-patch-check"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var originalDir = Path.Combine(root, "original");
        WriteBundle(originalDir, BuildScene(), BundleId);
        var original = RepairBundleReader.Read(originalDir);
        var patchPath = Path.Combine(root, "patch.json");
        File.WriteAllText(patchPath, PatchJson(original.BundleId, original.ManifestSha256), new UTF8Encoding(false));

        var applied = BuildScene();
        applied.nodes[0].outputs[0].targetUid = "C";
        applied.nodes[0].position = new Vector2Snapshot { x = 200f, y = -200f };
        ApplyFrame(applied, "A", 200f, 200f);
        var appliedDir = Path.Combine(root, "applied");
        WriteBundle(appliedDir, applied, "new-bundle-id-1");
        var passed = new NodePatchVerifier().Verify(patchPath, originalDir, appliedDir);
        Check(passed.Passed, "verifier accepts applied after with untouched state");

        var notApplied = BuildScene();
        notApplied.nodes[0].position = new Vector2Snapshot { x = 200f, y = -200f };
        ApplyFrame(notApplied, "A", 200f, 200f);
        var notAppliedDir = Path.Combine(root, "not-applied");
        WriteBundle(notAppliedDir, notApplied, "new-bundle-id-2");
        var notAppliedReport = new NodePatchVerifier().Verify(patchPath, originalDir, notAppliedDir);
        Check(!notAppliedReport.Passed, "verifier rejects slot that did not reach after");

        var sameIdDir = Path.Combine(root, "same-id");
        WriteBundle(sameIdDir, applied, original.BundleId);
        var sameIdReport = new NodePatchVerifier().Verify(patchPath, originalDir, sameIdDir);
        Check(!sameIdReport.Passed, "verifier rejects same bundleId as source");

        var untouchedSlot = BuildScene();
        untouchedSlot.nodes[0].outputs[0].targetUid = "C";
        untouchedSlot.nodes[1].outputs[2].targetUid = "C";
        untouchedSlot.nodes[1].outputs[2].conditionType = "NotUsed";
        untouchedSlot.nodes[0].position = new Vector2Snapshot { x = 200f, y = -200f };
        ApplyFrame(untouchedSlot, "A", 200f, 200f);
        var untouchedDir = Path.Combine(root, "untouched-slot");
        WriteBundle(untouchedDir, untouchedSlot, "new-bundle-id-3");
        var untouchedReport = new NodePatchVerifier().Verify(patchPath, originalDir, untouchedDir);
        Check(!untouchedReport.Passed, "verifier rejects untargeted slot change");

        // 新包缺 position 时回验必须报问题，不能抛 NullReferenceException。
        var missingPosition = BuildScene();
        missingPosition.nodes[0].outputs[0].targetUid = "C";
        missingPosition.nodes[0].position = null;
        ApplyFrame(missingPosition, "A", 200f, 200f);
        var missingPositionDir = Path.Combine(root, "missing-position");
        WriteBundle(missingPositionDir, missingPosition, "new-bundle-id-4");
        var missingPositionReport = new NodePatchVerifier().Verify(patchPath, originalDir, missingPositionDir);
        Check(!missingPositionReport.Passed, "verifier reports missing latest position instead of throwing");
    }

    private static void ApplyFrame(SceneSnapshot scene, string uid, float x, float y)
    {
        foreach (var geometry in scene.geometry.nodes)
        {
            if (geometry.uid == uid)
                geometry.frame = new GeometryFrameSnapshot { x = x, y = y, width = 120f, height = 60f };
        }
    }

    private static void WriteBundle(string root, SceneSnapshot scene, string bundleId)
    {
        Directory.CreateDirectory(root);
        var sceneBytes = new UTF8Encoding(false).GetBytes(JsonSnapshotWriter.Serialize(scene));
        File.WriteAllBytes(Path.Combine(root, "scene.json"), sceneBytes);
        var manifest = new BundleManifest
        {
            bundleId = bundleId,
            scene = scene.scene,
            collection = new DependencyCollection()
        };
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
    }

    private static string ManyConnectionsJson(int count)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("{\"format\":\"ec-scene-node-patch\",\"version\":1,\"source\":{\"bundleId\":\"")
            .Append(BundleId).Append("\",\"manifestSha256\":\"").Append(Hash).Append("\"},\"connections\":[");
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
                builder.Append(',');
            builder.Append("{\"sourceUid\":\"N").Append(index).Append("\",\"slot\":0,")
                .Append("\"before\":{\"targetUid\":\"B\",\"conditionType\":\"ConnectPart\",\"conditionCount\":0,\"fadeType\":0,\"fadeTime\":0.5},")
                .Append("\"after\":{\"targetUid\":\"C\"}}");
        }
        builder.Append("]}");
        return builder.ToString();
    }

    private static string ManyLayoutNodesJson(int count)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("{\"format\":\"ec-scene-node-patch\",\"version\":1,\"source\":{\"bundleId\":\"")
            .Append(BundleId).Append("\",\"manifestSha256\":\"").Append(Hash).Append("\"},\"layout\":{\"canvas\":{\"width\":5000,\"height\":3000},\"nodes\":[");
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
                builder.Append(',');
            builder.Append("{\"uid\":\"N").Append(index).Append("\",\"before\":{\"x\":0,\"y\":0},\"after\":{\"x\":1,\"y\":1}}");
        }
        builder.Append("]}}");
        return builder.ToString();
    }

    private static void ExpectFailure(Action action, string label)
    {
        assertions++;
        try
        {
            action();
        }
        catch (RepairInputException)
        {
            return;
        }
        throw new InvalidOperationException(label);
    }

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private sealed class FakeHost : INodePatchMemoryHost
    {
        private readonly object sceneOwner = new object();
        private readonly object nodeOwner = new object();
        private readonly object generationOwner = new object();
        private readonly Dictionary<string, NodePatchPlacementValue> positions =
            new Dictionary<string, NodePatchPlacementValue>();
        private readonly Dictionary<string, NodePatchSlotValue> slots =
            new Dictionary<string, NodePatchSlotValue>();
        private bool slotTouched;

        public FakeHost()
        {
            positions["A"] = new NodePatchPlacementValue { X = 100f, Y = -100f };
            positions["B"] = new NodePatchPlacementValue { X = 400f, Y = -100f };
            positions["C"] = new NodePatchPlacementValue { X = 700f, Y = -100f };
            slots["A|0"] = new NodePatchSlotValue("B", "ConnectPart", 0, 0, 0.5f);
            slots["B|0"] = new NodePatchSlotValue(string.Empty, "ConnectPart", 0, 0, 0.5f);
            slots["C|0"] = new NodePatchSlotValue(string.Empty, "ConnectPart", 0, 0, 0.5f);
        }

        public bool FailSlotAfterWriteOnce { get; set; }
        public bool ThrowSlotAfterWriteOnce { get; set; }
        public bool FailCompensation { get; set; }
        public bool FailCompensationAlways { get; set; }
        public bool ResizeAppliesThenFails { get; set; }
        private float canvasWidth = 4080f;
        private float canvasHeight = 2850f;
        private int slotWrites;
        public object SceneOwner => sceneOwner;
        public object NodeOwner => nodeOwner;
        public object GenerationOwner => generationOwner;

        public float Position(string uid) => positions[uid].X;
        public string SlotTarget(string uid, int slot) => slots[uid + "|" + slot].TargetUid;

        public bool TryValidateTargets(ICollection<string> targetUids, out string error)
        {
            error = null;
            return true;
        }

        public bool TryGetCanvas(out float width, out float height, out bool supportsAbsoluteResize, out string error)
        {
            width = canvasWidth;
            height = canvasHeight;
            supportsAbsoluteResize = true;
            error = null;
            return true;
        }

        public bool TryResizeAbsolute(float width, float height, out float actualWidth, out float actualHeight, out string error)
        {
            actualWidth = width;
            actualHeight = height;
            canvasWidth = width;
            canvasHeight = height;
            if (ResizeAppliesThenFails)
            {
                error = "模拟扩展后失败";
                return false;
            }
            error = null;
            return true;
        }

        public bool TryReadPlacement(string uid, out NodePatchPlacementValue value, out string error)
        {
            error = null;
            return positions.TryGetValue(uid, out value);
        }

        public bool TryWritePlacement(string uid, NodePatchPlacementValue value, out string error)
        {
            positions[uid] = value;
            error = null;
            return true;
        }

        public bool TryReadSlot(string sourceUid, int slot, out NodePatchSlotValue value, out string error)
        {
            error = null;
            return slots.TryGetValue(sourceUid + "|" + slot, out value);
        }

        public bool TryWriteSlotTarget(string sourceUid, int slot, string targetUid, out string error)
        {
            error = null;
            var key = sourceUid + "|" + slot;
            if (FailCompensationAlways || (FailCompensation && slotTouched))
                return false;
            NodePatchSlotValue value;
            if (!slots.TryGetValue(key, out value))
            {
                error = "missing slot";
                return false;
            }
            slotTouched = true;
            slots[key] = new NodePatchSlotValue(targetUid, value.ConditionType, value.ConditionCount, value.FadeType, value.FadeTime);
            slotWrites++;
            if (ThrowSlotAfterWriteOnce && slotWrites == 1)
                throw new InvalidOperationException("模拟槽位写入后异常");
            return !FailSlotAfterWriteOnce || slotWrites > 1;
        }
    }
}
