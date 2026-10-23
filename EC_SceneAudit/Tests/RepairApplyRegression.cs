using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EC_SceneAudit.Export;
using EC_SceneAudit.Repair;

// 离线回归：用纯内存游戏替身验证应用、受影响快照、真实数据恢复、事务回滚与会话边界。
// Part 载荷格式：首行 "#meta=..."，其后每行 "cut|box|layout|text"，用来承载布局原文和其它无关状态。
internal static class RepairApplyRegression
{
    private const string SceneTitle = "laa2-S-part1";
    private const string UidA = "IrvHPj";
    private const string UidB = "W58ioW";
    private const string UidC = "0RrU17";
    private const string PointerA = "/nodes/0/part/cuts/1/textBoxes/1/layouts/1/rawText";
    private const string PointerB = "/nodes/1/part/cuts/0/textBoxes/1/layouts/0/rawText";
    private const string PointerC = "/nodes/2/part/cuts/0/textBoxes/0/layouts/0/rawText";
    private const string TextA = "死了\n<script>\n<event-select>\n</event-select>\n</script>";
    private const string TextB = "<script>\n<load-script file=\"x.xml\"/>\n</script>";
    private const string TextC = "第三条台词";
    private const string AfterA = "改了A";
    private const string AfterB = "改了B";
    private const string MetaA = "meta-A";
    private const string SiblingA = "sibling-layout";
    private const int NodeSlotCount = 5;
    private const string DefaultBundleId = "cb8ba68c5b704397bb11cd946dc3b592";
    private const string NewBundleId = "0f7a1c2d3e4b5a69788796a5b4c3d2e1";

    private static int assertions;

    private static int Main(string[] args)
    {
        try
        {
            var output = Path.GetFullPath(args.Length > 0 ? args[0] : ".tmp/repair-apply-tests");
            var work = Path.Combine(output, "run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);

            VerifyHappyPath(work);
            VerifyEmptyAfter(work);
            VerifySlotPositionPreservedInRestore(work);
            VerifyBeforeConflict(work);
            VerifyDuplicateLocation(work);
            VerifySnapshotFailure(work);
            VerifySinkFailure(work);
            VerifySessionGuardAndFailedApplyKeepsSession(work);
            VerifyPartDeepCopyIsIndependent(work);
            VerifyRestoreRealDataAndUnrelatedState(work);
            VerifyTargetToTargetDedupe(work);
            VerifyRestorePartialFailureRollsBack(work);
            VerifyRestoreRollbackFailureReported(work);
            VerifyRestorePartialWriteCompensated(work);
            VerifyRestoreCompensationFailureReported(work);
            VerifySessionInvalidation(work);
            VerifyMultipleRepairs(work);
            VerifyVerification(work);
            VerifyNewestBundleDirectory(work);
            VerifyReportAndValidationText(work);
            VerifyWindowPointerOwnership();
            VerifyWindowGeometry();
            VerifyPathText();

            Console.WriteLine("PASS: " + assertions + " memory repair assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void VerifyHappyPath(string work)
    {
        var fixture = BuildFixture(work, "happy");
        var planPath = WritePlan(fixture, "plan.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var memory = fixture.NewHost();
        var sentinel = File.ReadAllText(fixture.SceneFile);

        var report = Apply(fixture, planPath, memory);

        Check(report.Phase == RepairApplyPhase.AppliedUnsaved, "apply stays in unsaved memory phase");
        Check(report.HasSession && report.HasSnapshot, "successful apply keeps one session snapshot");
        Check(report.Items.Count == 1 && report.Items[0].Written, "one target written");
        Check(report.Items[0].Label != null && report.Items[0].Label.Contains("S2-2A"), "label comes from preflight");
        Check(report.Snapshot.PartCount == 1 && report.Snapshot.HasPart(UidA), "snapshot covers only the target part");
        Check(report.Snapshot.InboundSlots.Count == 2, "snapshot records all inbound connection slots");
        Check(report.Snapshot.TargetNodes.Count == 1, "snapshot keeps the full target node base");
        Check(File.ReadAllText(fixture.SceneFile) == sentinel, "apply never writes a Scene file");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == AfterA, "memory holds the new text");
        Check(TextOf(memory.Scene, UidA, 1, 1, 0) == SiblingA, "sibling layout untouched");
        Check(MetaOf(memory.Scene, UidA) == MetaA, "part meta untouched");
        Check(report.Steps.Exists(step => step.Contains("未保存")), "step log warns the result is unsaved");
        Check(RepairApplyFormat.FormatText(report, null).Contains("内存快照"), "text report shows memory snapshot");
    }

    private static void VerifyEmptyAfter(string work)
    {
        var fixture = BuildFixture(work, "empty-after");
        var planPath = WritePlan(fixture, "plan.json", Change(PointerB, UidB, 0, 1, 0, TextB, string.Empty));
        var memory = fixture.NewHost();

        var report = Apply(fixture, planPath, memory);

        Check(report.Phase == RepairApplyPhase.AppliedUnsaved, "empty after applies normally");
        Check(TextOf(memory.Scene, UidB, 0, 1, 0) == string.Empty, "memory text cleared");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == TextA, "other parts untouched");
        Check(report.Snapshot.HasPart(UidB) && report.Snapshot.PartCount == 1, "snapshot covers the cleared target");
    }

    // 恢复只能动记录到的（来源, 槽位），非目标来源的其它槽位必须原样保留。
    private static void VerifySlotPositionPreservedInRestore(string work)
    {
        var fixture = BuildFixture(work, "slot-position");
        var planPath = WritePlan(fixture, "plan.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var memory = fixture.NewHost();
        memory.Scene.Nodes[UidC].Slots[1].ChildUid = string.Empty;
        var upstream = memory.Scene.Nodes[UidB];
        upstream.Slots[0].ChildUid = UidA;
        upstream.Slots[2].ChildUid = UidA;
        upstream.Slots[1].ChildUid = UidC;
        upstream.Slots[1].FadeType = 7;
        upstream.Slots[4].ChildUid = string.Empty;

        var report = Apply(fixture, planPath, memory);

        Check(report.Phase == RepairApplyPhase.AppliedUnsaved, "slot position fixture applies");
        Check(report.Snapshot.InboundSlots.Count == 2, "both inbound slots captured");
        Check(report.Snapshot.InboundSlots[0].Index == 0 && report.Snapshot.InboundSlots[1].Index == 2,
            "inbound slots keep their own indices");

        // 模拟外部把这两个槽位改成别的目标，恢复必须只回写快照里的两个槽位。
        upstream.Slots[0].ChildUid = UidC;
        upstream.Slots[2].ChildUid = string.Empty;
        Check(new RepairApplyEngine().Restore(report, memory), "slot position restore succeeds");
        Check(upstream.Slots[0].ChildUid == UidA && upstream.Slots[2].ChildUid == UidA,
            "recorded inbound slots restored");
        Check(upstream.Slots[1].ChildUid == UidC && upstream.Slots[1].FadeType == 7,
            "unrecorded slots of the same source untouched");
        Check(upstream.Slots[3].ChildUid == string.Empty && upstream.Slots[4].ChildUid == string.Empty,
            "other slots untouched");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == TextA, "slot restore also restores target text");
    }

    private static void VerifyBeforeConflict(string work)
    {
        var fixture = BuildFixture(work, "conflict");
        var planPath = WritePlan(fixture, "plan.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var memory = fixture.NewHost();
        WriteText(memory.Scene, UidA, 1, 1, 1, TextA + " / 已被改动");

        var report = Apply(fixture, planPath, memory);

        Check(report.Phase == RepairApplyPhase.PreflightFailed, "before conflict blocks the apply");
        Check(!report.HasSession && !report.HasSnapshot, "conflict produces no session");
        Check(report.Items.Count == 0, "nothing written on conflict");
        Check(memory.WritePartCalls == 0, "no part write on conflict");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1).Contains("已被改动"), "conflicting text remains unchanged");
    }

    private static void VerifyDuplicateLocation(string work)
    {
        var fixture = BuildFixture(work, "duplicate");
        var planPath = WritePlan(fixture, "plan.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var memory = fixture.NewHost();
        // 同一 UID 在快照里出现两次：唯一查找必须拒绝，不能取首项。
        memory.Scene.Nodes["dup-key"] = CloneNode(memory.Scene.Nodes[UidA]);
        memory.Scene.Parts["dup-key"] = RepairMemoryCopy.Bytes(memory.Scene.Parts[UidA]);
        memory.Scene.NodeOrder.Add("dup-key");

        var report = Apply(fixture, planPath, memory);

        Check(report.Phase == RepairApplyPhase.PreflightFailed, "duplicate node uid blocks the apply");
        Check(!report.HasSnapshot, "duplicate target produces no snapshot");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == TextA, "duplicate target leaves text unchanged");
    }

    private static void VerifySnapshotFailure(string work)
    {
        var fixture = BuildFixture(work, "snapshot-failure");
        var planPath = WritePlan(fixture, "plan.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var memory = fixture.NewHost();
        memory.FailReadPart = true;

        var report = Apply(fixture, planPath, memory);

        Check(report.Phase == RepairApplyPhase.Failed, "snapshot failure stops the apply");
        Check(!report.HasSnapshot, "failed snapshot produces no session");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == TextA, "snapshot failure leaves memory unchanged");
        Check(report.FirstIssue.Contains("内存快照"), "snapshot failure is reported");
    }

    private static void VerifySinkFailure(string work)
    {
        var fixture = BuildFixture(work, "sink-failure");
        var planPath = WritePlan(fixture, "plan.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var memory = fixture.NewHost();

        var report = new RepairApplyEngine().Apply(planPath, fixture.BundlePath, "scene-A",
            () => memory.ToSnapshot(SceneTitle), () => { throw new InvalidOperationException("模拟定位器失败"); }, memory);

        Check(report.Phase == RepairApplyPhase.Failed, "sink creation failure stops the apply");
        Check(!report.HasSnapshot, "sink failure produces no session");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == TextA, "sink failure leaves memory unchanged");
        Check(report.FirstIssue.Contains("定位器"), "sink failure is reported");
    }

    // 已有会话时拒绝新应用；失败的新应用不能顶掉旧会话。
    private static void VerifySessionGuardAndFailedApplyKeepsSession(string work)
    {
        var fixture = BuildFixture(work, "session-guard");
        var engine = new RepairApplyEngine();
        var firstPlan = WritePlan(fixture, "plan-a.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var memory = fixture.NewHost();

        var first = engine.Apply(firstPlan, fixture.BundlePath, "scene-A",
            () => memory.ToSnapshot(SceneTitle), () => memory.Sink(), memory, null);
        Check(first.Phase == RepairApplyPhase.AppliedUnsaved, "first apply creates the session");

        var second = engine.Apply(firstPlan, fixture.BundlePath, "scene-A",
            () => memory.ToSnapshot(SceneTitle), () => memory.Sink(), memory, first);
        Check(second.Phase == RepairApplyPhase.Failed, "second apply is refused while a session is active");
        Check(second.FirstIssue.Contains("尚未恢复或清除"), "refusal explains the active session");
        Check(!second.HasSnapshot && first.HasSession, "refused apply keeps the existing session intact");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == AfterA, "refused apply does not rewrite memory");

        // 失败的新应用（before 冲突）同样不能覆盖旧快照。
        var conflictPlan = WritePlan(fixture, "plan-conflict.json", Change(PointerA, UidA, 1, 1, 1, TextA, "别的稿"));
        var failed = engine.Apply(conflictPlan, fixture.BundlePath, "scene-A",
            () => memory.ToSnapshot(SceneTitle), () => memory.Sink(), memory, null);
        Check(failed.Phase == RepairApplyPhase.PreflightFailed, "conflicting new apply fails");
        Check(!failed.HasSnapshot, "failed new apply carries no snapshot");
        Check(first.HasSession && first.Snapshot.HasPart(UidA), "failed new apply does not drop the old snapshot");
    }

    // 快照必须是真正的深拷贝：就地改动内存后快照字节不变，恢复能回到改动前内容。
    private static void VerifyPartDeepCopyIsIndependent(string work)
    {
        var fixture = BuildFixture(work, "deep-copy");
        var plan = RepairPlanReader.Parse(PlanJson(fixture, Change(PointerA, UidA, 1, 1, 1, TextA, AfterA)));
        var memory = fixture.NewHost();

        RepairMemorySnapshot snapshot;
        string error;
        Check(RepairMemoryCapture.TryCapture(plan, memory, out snapshot, out error), "capture succeeds");
        var live = memory.Scene.Parts[UidA];
        var beforeFirstByte = snapshot.Parts[0].Data[0];
        live[0] = (byte)(live[0] ^ 0x2A);

        Check(snapshot.Parts[0].Data[0] == beforeFirstByte, "snapshot bytes unaffected by in-place live mutation");
        Check(snapshot.Parts[0].Data != live, "snapshot owns its own byte array");
        var nodeRaw = memory.Scene.Nodes[UidA].RawCache;
        Check(snapshot.TargetNodes[0].Data != nodeRaw, "node snapshot also copied");
        nodeRaw[0] = (byte)(nodeRaw[0] ^ 0x2A);
        Check(snapshot.TargetNodes[0].Data[0] != nodeRaw[0], "node snapshot unaffected by live mutation");

        // 记录与快照不同的内容，恢复后必须回到快照内容。
        WriteText(memory.Scene, UidA, 1, 1, 1, "被外部改掉了");
        Check(new RepairApplyEngine().Restore(
            new RepairApplyReport { Snapshot = snapshot, Phase = RepairApplyPhase.AppliedUnsaved }, memory),
            "restore from an independent snapshot succeeds");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == TextA, "restore returns the captured text");
        Check(MetaOf(memory.Scene, UidA) == MetaA, "restore keeps the captured meta line");
    }

    // 应用后改动目标文本、无关 Part、无关槽位，恢复只应还原目标与相关连线。
    private static void VerifyRestoreRealDataAndUnrelatedState(string work)
    {
        var fixture = BuildFixture(work, "restore-real-data");
        var planPath = WritePlan(fixture, "plan.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var memory = fixture.NewHost();
        var targetNode = memory.Scene.Nodes[UidA];
        var outboundBefore = CloneNode(targetNode);
        var unrelatedNode = memory.Scene.Nodes[UidC];

        var report = Apply(fixture, planPath, memory);
        Check(report.Phase == RepairApplyPhase.AppliedUnsaved, "restore fixture applies");

        // 应用后做与本次修复无关的改动，恢复后这些改动必须保留。
        WriteText(memory.Scene, UidB, 0, 1, 0, "无关节点的新文本");
        memory.Scene.Parts[UidB][1] = (byte)'Z';
        unrelatedNode.Slots[3].FadeType = 9;
        // 同一条入线被外部改错，恢复必须回到快照值。
        unrelatedNode.Slots[1].ChildUid = string.Empty;
        unrelatedNode.Slots[1].FadeType = 77;
        var unrelatedPartText = TextOf(memory.Scene, UidB, 0, 1, 0);

        var restored = new RepairApplyEngine().Restore(report, memory);

        Check(restored, "restore real data succeeds");
        Check(report.Phase == RepairApplyPhase.Restored, "phase records the memory restore");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == TextA, "target text restored from the snapshot");
        Check(MetaOf(memory.Scene, UidA) == MetaA, "target part payload fully restored");
        Check(SlotsText(targetNode) == SlotsText(outboundBefore), "target node base fully restored");
        Check(ReferenceEquals(memory.Scene.Nodes[UidA], targetNode), "target node instance identity kept");
        Check(TextOf(memory.Scene, UidB, 0, 1, 0) == unrelatedPartText, "unrelated part text preserved");
        Check(unrelatedNode.Slots[3].FadeType == 9, "unrelated node slots preserved");
        Check(unrelatedNode.Slots[1].ChildUid == UidA && unrelatedNode.Slots[1].FadeType == 2,
            "recorded inbound slot restored to its snapshot value");
        Check(report.Steps.Exists(step => step.Contains("内存快照恢复")), "restore step recorded");
        Check(!report.HasSession, "restored session no longer blocks a new repair");
    }

    // 目标之间互连去重：目标来源的槽位由完整 NodeBase 覆盖，不再重复记为入线。
    private static void VerifyTargetToTargetDedupe(string work)
    {
        var fixture = BuildFixture(work, "dedupe");
        var planPath = WritePlan(fixture, "plan.json",
            Change(PointerA, UidA, 1, 1, 1, TextA, AfterA),
            Change(PointerB, UidB, 0, 1, 0, TextB, AfterB));
        var memory = fixture.NewHost();
        memory.Scene.Nodes[UidA].Slots[0].ChildUid = UidB;
        memory.Scene.Nodes[UidB].Slots[3].ChildUid = UidA;

        var report = Apply(fixture, planPath, memory);

        Check(report.Phase == RepairApplyPhase.AppliedUnsaved, "two-target apply succeeds");
        Check(report.Snapshot.PartCount == 2, "two target parts snapshotted");
        for (var index = 0; index < report.Snapshot.InboundSlots.Count; index++)
            Check(report.Snapshot.InboundSlots[index].SourceUid != UidA
                && report.Snapshot.InboundSlots[index].SourceUid != UidB,
                "target-to-target edges are deduplicated");

        memory.Scene.Nodes[UidA].Slots[0].ChildUid = string.Empty;
        memory.Scene.Nodes[UidB].Slots[3].ChildUid = string.Empty;
        Check(new RepairApplyEngine().Restore(report, memory), "two-target restore succeeds");
        Check(memory.Scene.Nodes[UidA].Slots[0].ChildUid == UidB, "target outbound edge restored");
        Check(memory.Scene.Nodes[UidB].Slots[3].ChildUid == UidA, "second target outbound edge restored");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == TextA && TextOf(memory.Scene, UidB, 0, 1, 0) == TextB,
            "both targets restore their original text");
    }

    // 恢复中途失败：已改部分必须回滚到恢复前状态，快照保留供重试。
    private static void VerifyRestorePartialFailureRollsBack(string work)
    {
        var fixture = BuildFixture(work, "restore-partial");
        var planPath = WritePlan(fixture, "plan.json",
            Change(PointerA, UidA, 1, 1, 1, TextA, AfterA),
            Change(PointerB, UidB, 0, 1, 0, TextB, AfterB));
        var memory = fixture.NewHost();
        var report = Apply(fixture, planPath, memory);
        Check(report.Phase == RepairApplyPhase.AppliedUnsaved, "partial failure fixture applies");

        // 恢复前记录"恢复前状态"，并让第二个 Part 的替换失败。
        var preRestoreA = RepairMemoryCopy.Bytes(memory.Scene.Parts[UidA]);
        var preRestoreB = RepairMemoryCopy.Bytes(memory.Scene.Parts[UidB]);
        var preSlots = SlotsText(memory.Scene.Nodes[UidC]);
        // 第二个目标的首次替换失败；补偿也必须允许成功，才能验证当前项进入回滚范围。
        memory.FailPartWrite = (uid, writes) => uid == UidB && writes == 1;

        var restored = new RepairApplyEngine().Restore(report, memory);

        Check(!restored, "partial restore failure is reported");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == AfterA, "first part text rolled back");
        Check(SameBytes(memory.Scene.Parts[UidA], preRestoreA), "first part bytes equal pre-restore state");
        Check(SameBytes(memory.Scene.Parts[UidB], preRestoreB), "second part untouched");
        Check(SlotsText(memory.Scene.Nodes[UidC]) == preSlots, "slots untouched by the failed restore");
        Check(report.Steps.Exists(step => step.Contains("已回滚")), "rollback success recorded");
        Check(report.FirstIssue.Contains("回滚"), "failure report includes the rollback result");
        Check(report.HasSnapshot, "snapshot kept for a retry");
    }

    // 回滚本身失败时必须如实报告，并保留快照。
    private static void VerifyRestoreRollbackFailureReported(string work)
    {
        var fixture = BuildFixture(work, "restore-rollback-failure");
        var planPath = WritePlan(fixture, "plan.json",
            Change(PointerA, UidA, 1, 1, 1, TextA, AfterA),
            Change(PointerB, UidB, 0, 1, 0, TextB, AfterB));
        var memory = fixture.NewHost();
        var report = Apply(fixture, planPath, memory);

        // UidB 写入失败触发回滚，回滚再次写 UidA 时也失败：必须如实报告回滚失败。
        memory.FailPartWrite = (uid, writes) => uid == UidB || writes > 1;
        var restored = new RepairApplyEngine().Restore(report, memory);

        Check(!restored, "restore with failing rollback is reported as failed");
        Check(report.Steps.Exists(step => step.Contains("回滚失败")), "rollback failure recorded honestly");
        Check(report.FirstIssue.Contains("回滚失败"), "report names the rollback failure");
        Check(report.HasSnapshot, "snapshot kept when rollback fails");
    }

    // 单步写入已部分生效后抛异常或返回 false：当前失败项也必须纳入回滚，不能只回滚此前完成项。
    private static void VerifyRestorePartialWriteCompensated(string work)
    {
        VerifyRestorePartialSlotWriteCompensated(work);
        VerifyRestorePartialNodeWriteCompensated(work);
        VerifyRestorePartialPartWriteCompensated(work);
        VerifyRestoreFalseAfterNodeWriteCompensated(work);
    }

    private static MemoryHost ApplySingleTarget(Fixture fixture, string name, out RepairApplyReport report)
    {
        var planPath = WritePlan(fixture, name, Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var memory = fixture.NewHost();
        report = Apply(fixture, planPath, memory);
        Check(report.Phase == RepairApplyPhase.AppliedUnsaved, name + " fixture applies");
        return memory;
    }

    private static void VerifyRestorePartialSlotWriteCompensated(string work)
    {
        var fixture = BuildFixture(work, "restore-partial-slot");
        var memory = ApplySingleTarget(fixture, "plan.json", out var report);
        var upstream = memory.Scene.Nodes[UidC];
        upstream.Slots[1].ChildUid = UidB;
        upstream.Slots[1].FadeType = 77;
        var preSlots = SlotsText(upstream);
        var prePart = RepairMemoryCopy.Bytes(memory.Scene.Parts[UidA]);
        // 恢复该入线槽位时先改值再抛异常，模拟 CreateConnectLine 失败留下的部分写入。
        memory.ThrowSlotWriteAfter = (uid, index, writes) => uid == UidC && index == 1 && writes == 1;

        var restored = new RepairApplyEngine().Restore(report, memory);

        Check(!restored, "partial slot write failure is reported");
        Check(SlotsText(upstream) == preSlots, "current failing slot rolled back to pre-restore state");
        Check(SameBytes(memory.Scene.Parts[UidA], prePart), "part rolled back when slot write throws");
        Check(report.Steps.Exists(step => step.Contains("已回滚")), "partial slot write rollback recorded");
        Check(report.FirstIssue.Contains("回滚"), "partial slot write report names the rollback");
    }

    private static void VerifyRestorePartialNodeWriteCompensated(string work)
    {
        var fixture = BuildFixture(work, "restore-partial-node");
        var memory = ApplySingleTarget(fixture, "plan.json", out var report);
        var target = memory.Scene.Nodes[UidA];
        target.PosX = 999f;
        var preNode = MemorySceneCodec.Serialize(target);
        var prePart = RepairMemoryCopy.Bytes(memory.Scene.Parts[UidA]);
        // 节点 Copy 完成后连线同步抛异常：节点已被改写，当前节点必须回滚。
        memory.ThrowNodeWriteAfter = (uid, writes) => uid == UidA && writes == 1;

        var restored = new RepairApplyEngine().Restore(report, memory);

        Check(!restored, "partial node write failure is reported");
        Check(SameBytes(MemorySceneCodec.Serialize(target), preNode), "current failing node rolled back to pre-restore state");
        Check(SameBytes(memory.Scene.Parts[UidA], prePart), "part rolled back when node write throws");
        Check(report.Steps.Exists(step => step.Contains("已回滚")), "partial node write rollback recorded");
    }

    private static void VerifyRestorePartialPartWriteCompensated(string work)
    {
        var fixture = BuildFixture(work, "restore-partial-part");
        var memory = ApplySingleTarget(fixture, "plan.json", out var report);
        var prePart = RepairMemoryCopy.Bytes(memory.Scene.Parts[UidA]);
        // Part 落库后抛异常：当前 Part 必须回滚到恢复前字节。
        memory.ThrowPartWriteAfter = (uid, writes) => uid == UidA && writes == 1;

        var restored = new RepairApplyEngine().Restore(report, memory);

        Check(!restored, "partial part write failure is reported");
        Check(SameBytes(memory.Scene.Parts[UidA], prePart), "current failing part rolled back to pre-restore bytes");
        Check(report.Steps.Exists(step => step.Contains("已回滚")), "partial part write rollback recorded");
    }

    private static void VerifyRestoreFalseAfterNodeWriteCompensated(string work)
    {
        var fixture = BuildFixture(work, "restore-false-after-node");
        var memory = ApplySingleTarget(fixture, "plan.json", out var report);
        var target = memory.Scene.Nodes[UidA];
        target.PosX = 999f;
        var preNode = MemorySceneCodec.Serialize(target);
        var prePart = RepairMemoryCopy.Bytes(memory.Scene.Parts[UidA]);
        // 节点已就地写入后 TryWriteNode 返回 false，当前节点仍必须进入补偿范围。
        memory.FailNodeWriteAfter = (uid, writes) => uid == UidA && writes == 1;

        var restored = new RepairApplyEngine().Restore(report, memory);

        Check(!restored, "false-after-write node failure is reported");
        Check(SameBytes(MemorySceneCodec.Serialize(target), preNode), "false-after-write node rolled back");
        Check(SameBytes(memory.Scene.Parts[UidA], prePart), "part rolled back when node returns false after write");
        Check(report.Steps.Exists(step => step.Contains("已回滚")), "false-after-write rollback recorded");
    }

    // 当前失败项的补偿也失败时，必须如实报告回滚失败并保留快照，不能谎报已回滚。
    private static void VerifyRestoreCompensationFailureReported(string work)
    {
        var fixture = BuildFixture(work, "restore-compensation-failure");
        var memory = ApplySingleTarget(fixture, "plan.json", out var report);
        memory.Scene.Nodes[UidC].Slots[1].ChildUid = UidB;
        // 第一次写该槽位先改值再抛异常；第二次补偿返回失败，形成部分写入且补偿失败。
        memory.ThrowSlotWriteAfter = (uid, index, writes) => uid == UidC && index == 1 && writes == 1;
        memory.FailSlotWrite = (uid, index, writes) => uid == UidC && index == 1 && writes == 2;

        var restored = new RepairApplyEngine().Restore(report, memory);

        Check(!restored, "restore with failing compensation is reported as failed");
        Check(report.Steps.Exists(step => step.Contains("回滚失败")), "compensation failure recorded honestly");
        Check(report.FirstIssue.Contains("回滚失败"), "report names the compensation failure");
        Check(report.HasSnapshot, "snapshot kept when compensation fails");
    }

    // 切换或同路径重新加载 Scene（dictNode 换新实例）后旧快照必须失效。
    private static void VerifySessionInvalidation(string work)
    {
        var fixture = BuildFixture(work, "invalidation");
        var planPath = WritePlan(fixture, "plan.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var memory = fixture.NewHost();
        var report = Apply(fixture, planPath, memory);
        Check(report.Phase == RepairApplyPhase.AppliedUnsaved, "invalidation fixture applies");

        // 同路径重新加载：世代对象换新，但 Part 内容与 UID 都还是同一份。
        memory.Scene.Generation = new object();
        var restored = new RepairApplyEngine().Restore(report, memory);

        Check(!restored, "reloaded scene rejects the old snapshot");
        Check(report.FirstIssue.Contains("已失效"), "invalidation reason reported");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == AfterA, "invalidation leaves the applied text alone");

        // 切到别的 Scene：SceneOwner 变化同样失效。
        var other = fixture.NewHost();
        other.Scene.Parts[UidA] = RepairMemoryCopy.Bytes(memory.Scene.Parts[UidA]);
        var beforeOther = RepairMemoryCopy.Bytes(other.Scene.Parts[UidA]);
        var switched = new RepairApplyEngine().Restore(report, other);
        Check(!switched, "another scene rejects the old snapshot");
        Check(SameBytes(other.Scene.Parts[UidA], beforeOther), "another scene is not modified by the refused restore");
    }

    // 恢复或清除后可再次修复另一份清单，新快照只覆盖新目标。
    private static void VerifyMultipleRepairs(string work)
    {
        var fixture = BuildFixture(work, "multiple");
        var engine = new RepairApplyEngine();
        var memory = fixture.NewHost();
        var firstPlan = WritePlan(fixture, "plan-a.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var secondPlan = WritePlan(fixture, "plan-b.json", Change(PointerB, UidB, 0, 1, 0, TextB, AfterB));

        var first = engine.Apply(firstPlan, fixture.BundlePath, "scene-A",
            () => memory.ToSnapshot(SceneTitle), () => memory.Sink(), memory, null);
        Check(engine.Restore(first, memory), "first repair restored");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == TextA, "first repair rolled back");

        var second = engine.Apply(secondPlan, fixture.BundlePath, "scene-A",
            () => memory.ToSnapshot(SceneTitle), () => memory.Sink(), memory, first);
        Check(second.Phase == RepairApplyPhase.AppliedUnsaved, "second repair applies after restore");
        Check(second.Snapshot.PartCount == 1 && second.Snapshot.HasPart(UidB),
            "second snapshot only covers the new target");
        Check(TextOf(memory.Scene, UidA, 1, 1, 1) == TextA, "earlier target untouched by the second repair");
        Check(TextOf(memory.Scene, UidB, 0, 1, 0) == AfterB, "second target written");
        Check(engine.Restore(second, memory), "second repair restored");
        Check(TextOf(memory.Scene, UidB, 0, 1, 0) == TextB, "second repair rolled back");
    }

    private static void VerifyVerification(string work)
    {
        var fixture = BuildFixture(work, "verification");
        var planPath = WritePlan(fixture, "plan.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var engine = new RepairApplyEngine();

        var applied = Clone(fixture.Source);
        SetSnapshotText(applied, UidA, 1, 1, 1, AfterA);
        var appliedDir = Path.Combine(fixture.Root, "f8-applied");
        WriteBundle(appliedDir, applied, true, NewBundleId);
        var report = engine.Verify(planPath, fixture.BundlePath, appliedDir);
        Check(report.Passed, "verification passes when the target matches after");
        Check(report.Items.Count == 1 && report.PassedCount == 1, "verification item passes");
        Check(report.NewBundleId == NewBundleId, "verification records the new bundle id");
        Check(RepairApplyFormat.FormatText(null, report).Contains("回验"), "verification text report written");

        var staleDir = Path.Combine(fixture.Root, "f8-stale");
        WriteBundle(staleDir, Clone(fixture.Source), true, "stale-bundle-id");
        var stale = engine.Verify(planPath, fixture.BundlePath, staleDir);
        Check(!stale.Passed, "verification fails while the old text remains");
        Check(stale.Items[0].Issues.Exists(issue => issue.Contains("原文")), "stale text reported");

        var extraDir = Path.Combine(fixture.Root, "f8-extra");
        var extra = Clone(applied);
        SetSnapshotText(extra, UidC, 0, 0, 0, TextC + " / 顺手改了");
        WriteBundle(extraDir, extra, true, "extra-bundle-id");
        var extraReport = engine.Verify(planPath, fixture.BundlePath, extraDir);
        Check(!extraReport.Passed, "untouched text change fails verification");
        Check(extraReport.GlobalIssues.Exists(issue => issue.Contains("未目标文本变化")), "untouched text change reported");

        var structureDir = Path.Combine(fixture.Root, "f8-structure");
        var reduced = Clone(applied);
        reduced.nodes = new[] { reduced.nodes[0], reduced.nodes[1] };
        WriteBundle(structureDir, reduced, true, "structure-bundle-id");
        var structureReport = engine.Verify(planPath, fixture.BundlePath, structureDir);
        Check(!structureReport.Passed, "structure change fails verification");
        Check(structureReport.GlobalIssues.Exists(issue => issue.Contains("节点数量变化")), "structure change reported");

        var missingSummaryDir = Path.Combine(fixture.Root, "f8-nosummary");
        WriteBundle(missingSummaryDir, Clone(applied), false, "nosummary-bundle-id");
        var missingSummary = engine.Verify(planPath, fixture.BundlePath, missingSummaryDir);
        Check(missingSummary.GlobalIssues.Exists(issue => issue.Contains("ai-summary.json")), "missing ai-summary rejected");

        var badSummaryDir = Path.Combine(fixture.Root, "f8-badsummary");
        WriteBundle(badSummaryDir, Clone(applied), true, "badsummary-bundle-id");
        File.WriteAllText(Path.Combine(badSummaryDir, "ai-summary.json"), "{");
        var badSummary = engine.Verify(planPath, fixture.BundlePath, badSummaryDir);
        Check(badSummary.GlobalIssues.Exists(issue => issue.Contains("无法解析")), "unparsable ai-summary rejected");

        var sameBundle = engine.Verify(planPath, fixture.BundlePath, fixture.BundlePath);
        Check(sameBundle.GlobalIssues.Exists(issue => issue.Contains("同一目录")), "same bundle rejected for verification");

        // 应用前的旧导出：目录不同但 bundleId 与应用前资料包相同，必须拒绝。
        var preApplyDir = Path.Combine(fixture.Root, "f8-pre-apply");
        WriteBundle(preApplyDir, Clone(fixture.Source), true, fixture.Bundle.BundleId);
        var preApply = engine.Verify(planPath, fixture.BundlePath, preApplyDir);
        Check(preApply.GlobalIssues.Exists(issue => issue.Contains("同一批次")), "pre-apply export rejected by bundle id");

        var missingDir = engine.Verify(planPath, fixture.BundlePath, Path.Combine(fixture.Root, "f8-missing"));
        Check(!missingDir.Passed, "missing export directory fails verification");
    }

    private static void VerifyNewestBundleDirectory(string work)
    {
        var root = Path.Combine(work, "exports");
        var older = Path.Combine(root, "scene-a", "20261006", "120000_aaaa");
        var newer = Path.Combine(root, "scene-a", "20261006", "130000_bbbb");
        var incomplete = Path.Combine(root, "scene-a", "20261006", "140000_cccc");
        foreach (var directory in new[] { older, newer, incomplete })
            Directory.CreateDirectory(directory);
        WriteBundleStub(older);
        WriteBundleStub(newer);
        File.WriteAllText(Path.Combine(incomplete, "bundle.json"), "{}");

        var baseTime = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(older, baseTime);
        Directory.SetLastWriteTimeUtc(newer, baseTime.AddMinutes(30));
        Directory.SetLastWriteTimeUtc(incomplete, baseTime.AddMinutes(60));

        Check(RepairApplyEngine.FindNewestBundleDirectory(root, baseTime.AddMinutes(-1)) == newer,
            "newest complete export picked");
        // 严格晚于阈值：等于导出时间也不接受，避免选中应用前的导出。
        Check(RepairApplyEngine.FindNewestBundleDirectory(root, baseTime.AddMinutes(30)) == null,
            "export at the exact threshold is rejected");
        Check(RepairApplyEngine.FindNewestBundleDirectory(root, baseTime.AddMinutes(45)) == null,
            "exports before the threshold ignored");
        Check(RepairApplyEngine.FindNewestBundleDirectory(Path.Combine(work, "no-exports"), baseTime) == null,
            "missing export root returns null");
    }

    private static void VerifyReportAndValidationText(string work)
    {
        var fixture = BuildFixture(work, "report");
        var planPath = WritePlan(fixture, "plan.json", Change(PointerA, UidA, 1, 1, 1, TextA, AfterA));
        var memory = fixture.NewHost();
        var report = Apply(fixture, planPath, memory);

        var text = RepairApplyFormat.FormatText(report, null);
        Check(text.Contains("内存快照"), "report mentions the memory snapshot");
        Check(text.Contains("未保存"), "report warns that nothing is saved");
        Check(!text.Contains("备份") && !text.Contains("已保存，待"), "report no longer mentions file backup");
        Check(!text.Contains("重新载入 Scene"), "report no longer mentions reloading the scene");
        Check(report.StageText.Contains("未保存"), "stage text states unsaved");
    }

    // 窗口输入隔离：按鼠标键跟踪手势归属，窗口外起始的手势必须全程放行（游戏要拿到松开），
    // 窗口内起始的手势整段屏蔽；覆盖左右中键、内外往返、多键同时与关闭清理。
    private static void VerifyWindowPointerOwnership()
    {
        foreach (var button in new[] { 0, 1, 2 })
        {
            var single = new RepairPointerOwnership();
            single.ButtonDown(button, true);
            Check(single.IsOwned(button) && single.IsHeld(button), "window press owns button " + button);
            Check(single.ShouldBlock(true) && single.ShouldBlock(false),
                "owned gesture keeps blocking after leaving the window " + button);
            single.ButtonUp(button);
            Check(!single.IsOwned(button) && !single.IsHeld(button), "release clears button " + button);
            Check(!single.ShouldBlock(false), "no block after releasing outside the window " + button);
            Check(single.ShouldBlock(true), "hover rule returns after releasing " + button);
        }

        // 窗口外按下、移入窗口再松开：全程放行，游戏才能收到松开边沿。
        var outside = new RepairPointerOwnership();
        outside.ButtonDown(2, false);
        Check(!outside.ShouldBlock(false), "outside press is not blocked outside the window");
        Check(!outside.ShouldBlock(true), "outside press stays unblocked while over the window");
        Check(outside.IsHeld(2) && !outside.IsOwned(2), "outside press is held but not owned");
        outside.ButtonUp(2);
        Check(!outside.AnyHeld && !outside.AnyOwned, "outside release clears the gesture");
        // 释放帧仍需放行：uGUI 的 drop/click 用该帧的命中结果，清空射线会丢连线。
        Check(!outside.ShouldBlock(true), "outside release frame is still handed to the game");
        outside.BeginFrame();
        Check(outside.ShouldBlock(true), "hover rule returns after the release frame");

        // 窗口内按下、移出窗口再松开：整段归窗口，跨越边界往返都不放行。
        var roundTrip = new RepairPointerOwnership();
        roundTrip.ButtonDown(0, true);
        Check(roundTrip.ShouldBlock(true) && roundTrip.ShouldBlock(false),
            "window gesture blocks on both sides of the boundary");
        Check(roundTrip.ShouldBlock(true), "window gesture keeps blocking when the pointer returns");
        roundTrip.ButtonUp(0);
        Check(!roundTrip.AnyOwned && !roundTrip.AnyHeld, "window gesture ends on release");
        // 窗口内手势的释放帧继续屏蔽，游戏不应看到这次点击。
        Check(roundTrip.ShouldBlock(true), "window gesture release frame stays blocked");
        roundTrip.BeginFrame();
        Check(roundTrip.ShouldBlock(true), "hover rule returns after the window gesture");

        // 多键同时：窗口外左键 + 窗口内中键，窗口内手势优先；中键松开后左键交还游戏。
        var mixed = new RepairPointerOwnership();
        mixed.ButtonDown(0, false);
        mixed.ButtonDown(2, true);
        Check(mixed.ShouldBlock(true), "window-owned button wins the shared raycast list");
        mixed.ButtonUp(2);
        Check(mixed.IsHeld(0) && !mixed.IsOwned(0), "outside left press stays held and unowned");
        Check(!mixed.ShouldBlock(true), "outside press is handed back after the window button ends");
        mixed.ButtonUp(0);
        Check(!mixed.AnyHeld, "every button is released");
        Check(!mixed.ShouldBlock(true), "outside release frame is handed back after the window button");
        mixed.BeginFrame();
        Check(mixed.ShouldBlock(true), "hover rule returns after every button is released");

        // 同帧按下并松开（极快点击）：两个边沿都处理，净结果为松开，不留归窗口状态。
        var quick = new RepairPointerOwnership();
        quick.ButtonDown(0, true);
        quick.ButtonUp(0);
        quick.BeginFrame();
        Check(!quick.AnyHeld && !quick.AnyOwned && !quick.ShouldBlock(false) && quick.ShouldBlock(true),
            "same-frame down and up leaves only the hover rule");

        // 关闭窗口、失焦或插件卸载：清理全部归属，避免残留屏蔽下一次操作。
        var closing = new RepairPointerOwnership();
        closing.ButtonDown(1, true);
        closing.Reset();
        Check(!closing.AnyHeld && !closing.AnyOwned && !closing.ShouldBlock(false),
            "reset clears ownership on close or focus loss");
        Check(closing.ShouldBlock(true), "reset still honours plain hover");

        // 释放宽限必须能被 Reset 清掉，否则失焦后会漏掉后续屏蔽判定。
        var grace = new RepairPointerOwnership();
        grace.ButtonDown(0, false);
        grace.ButtonUp(0);
        Check(!grace.ShouldBlock(true), "release grace passes the release frame");
        grace.Reset();
        Check(grace.ShouldBlock(true), "reset clears the release grace");

        var invalid = new RepairPointerOwnership();
        invalid.ButtonDown(9, true);
        invalid.ButtonUp(-1);
        Check(!invalid.AnyHeld && !invalid.AnyOwned, "out-of-range button index is ignored");
    }

    // 窗口位置：默认居中偏上、始终夹在屏幕内、拖动结算后换分辨率仍落在屏幕内且位置成比例。
    private static void VerifyWindowGeometry()
    {
        var box = RepairWindowGeometry.FromNormalized(0.5f, 0.35f, 1920, 1080);
        Check(Math.Abs(box.Width - 780f) < 0.01f && Math.Abs(box.Height - 640f) < 0.01f,
            "preferred size used when the screen is large enough");
        Check(Math.Abs(box.X - (1920 - 780) * 0.5f) < 0.01f && Math.Abs(box.Y - (1080 - 640) * 0.35f) < 0.01f,
            "normalized position expands to screen space");
        Check(Inside(box, 1920, 1080), "default box stays inside the screen");
        // 拖到右下越界后再夹紧：右下边界必须贴住屏幕，位置仍然有效。
        var dragged = RepairWindowGeometry.ClampToScreen(new RepairWindowBox(1800f, 1000f, 780f, 640f), 1920, 1080);
        Check(Math.Abs(dragged.X - (1920 - 780)) < 0.01f && Math.Abs(dragged.Y - (1080 - 640)) < 0.01f,
            "dragged box is clamped to the bottom right");
        var negative = RepairWindowGeometry.ClampToScreen(new RepairWindowBox(-500f, -300f, 780f, 640f), 1920, 1080);
        Check(Math.Abs(negative.X) < 0.01f && Math.Abs(negative.Y) < 0.01f, "negative position clamped to origin");
        Check(Inside(dragged, 1920, 1080) && Inside(negative, 1920, 1080), "clamped boxes stay inside the screen");

        // 小分辨率时窗口收缩，不超出屏幕。
        var small = RepairWindowGeometry.FromNormalized(0.5f, 0.35f, 1280, 720);
        Check(small.Width <= 1280f && small.Height <= 720f, "window shrinks on small screens");
        Check(Inside(small, 1280, 720), "small-screen box stays inside the screen");

        // 结算：把当前位置折算成归一化坐标，再按另一分辨率展开，仍应在屏幕内且比例一致。
        var moved = RepairWindowGeometry.ClampToScreen(new RepairWindowBox(1200f, 400f, 780f, 640f), 1920, 1080);
        float normalizedX, normalizedY;
        RepairWindowGeometry.Normalize(moved, 1920, 1080, out normalizedX, out normalizedY);
        Check(normalizedX >= 0f && normalizedX <= 1f && normalizedY >= 0f && normalizedY <= 1f,
            "normalized position stays in unit range");
        var reopened = RepairWindowGeometry.FromNormalized(normalizedX, normalizedY, 1920, 1080);
        Check(Math.Abs(reopened.X - moved.X) < 0.01f && Math.Abs(reopened.Y - moved.Y) < 0.01f,
            "reopening at the same resolution restores the moved position");
        var resized = RepairWindowGeometry.FromNormalized(normalizedX, normalizedY, 2560, 1440);
        Check(Inside(resized, 2560, 1440), "position stays inside after a resolution change");
        var downscaled = RepairWindowGeometry.FromNormalized(normalizedX, normalizedY, 1280, 720);
        Check(Inside(downscaled, 1280, 720), "position stays inside after downscaling");

        // 非法输入不能产生 NaN 或越界位置。
        var nan = RepairWindowGeometry.FromNormalized(float.NaN, float.PositiveInfinity, 1920, 1080);
        Check(Inside(nan, 1920, 1080), "non-finite normalized input falls back inside the screen");
        Check(Inside(RepairWindowGeometry.ClampToScreen(new RepairWindowBox(float.NaN, 0f, 780f, 640f), 1920, 1080),
            1920, 1080), "non-finite position is clamped inside the screen");
        // 尺寸也必须挡住非有限值：否则 NaN 会直接带进 GUI.Window 矩形与命中判定。
        Check(Inside(RepairWindowGeometry.ClampToScreen(
            new RepairWindowBox(100f, 100f, float.NaN, float.PositiveInfinity), 1920, 1080), 1920, 1080),
            "non-finite size falls back inside the screen");
        Check(Inside(RepairWindowGeometry.ClampToScreen(
            new RepairWindowBox(0f, 0f, float.NaN, 640f), 1280, 720), 1280, 720),
            "non-finite width falls back inside a small screen");
    }

    private static bool Inside(RepairWindowBox box, int screenWidth, int screenHeight)
    {
        return box.X >= -0.01f && box.Y >= -0.01f
            && box.X + box.Width <= screenWidth + 0.01f
            && box.Y + box.Height <= screenHeight + 0.01f;
    }

    private static void VerifyPathText()
    {
        var pasted = RepairPathText.Sanitize("G:/DL/测试\r\n/Scene\n/part\r/cache");
        Check(pasted == "G:/DL/测试/Scene/part/cache", "path sanitization removes CR and LF from pasted input");
        Check(RepairPathText.Sanitize("F:\\Down\\EC\\路径 with spaces.json") == "F:\\Down\\EC\\路径 with spaces.json",
            "path sanitization preserves ordinary path characters");
        Check(RepairPathText.Sanitize(null) == string.Empty, "null path becomes empty text");
    }

    private static void WriteBundleStub(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "bundle.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "bundle.sha256"), "stub");
    }

    private static RepairApplyReport Apply(Fixture fixture, string planPath, MemoryHost memory)
    {
        return new RepairApplyEngine().Apply(planPath, fixture.BundlePath, "scene-A",
            () => memory.ToSnapshot(SceneTitle), () => memory.Sink(), memory, null);
    }

    // ---- Part 载荷：首行 "#meta=..."，其后 "cut|box|layout|text"（文本转义换行）----

    private static byte[] BuildPayload(string meta, params LayoutEntry[] entries)
    {
        var builder = new StringBuilder();
        builder.Append("#meta=").Append(meta ?? string.Empty).Append('\n');
        foreach (var entry in entries)
            AppendEntry(builder, entry.Cut, entry.Box, entry.Layout, entry.Text);
        return new UTF8Encoding(false).GetBytes(builder.ToString());
    }

    private static void AppendEntry(StringBuilder builder, int cut, int box, int layout, string text)
    {
        builder.Append(cut).Append('|').Append(box).Append('|').Append(layout).Append('|')
            .Append(EncodeText(text)).Append('\n');
    }

    private static List<LayoutEntry> ParsePayload(byte[] payload)
    {
        var entries = new List<LayoutEntry>();
        if (payload == null || payload.Length == 0)
            return entries;
        var text = new UTF8Encoding(false).GetString(payload);
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var first = line.IndexOf('|');
            var second = first < 0 ? -1 : line.IndexOf('|', first + 1);
            var third = second < 0 ? -1 : line.IndexOf('|', second + 1);
            if (third < 0)
                continue;
            int cut, box, layout;
            if (!int.TryParse(line.Substring(0, first), out cut)
                || !int.TryParse(line.Substring(first + 1, second - first - 1), out box)
                || !int.TryParse(line.Substring(second + 1, third - second - 1), out layout))
                continue;
            entries.Add(new LayoutEntry
            {
                Cut = cut,
                Box = box,
                Layout = layout,
                Text = DecodeText(line.Substring(third + 1))
            });
        }
        return entries;
    }

    private static bool PayloadHas(byte[] payload, int cut, int box, int layout)
    {
        foreach (var entry in ParsePayload(payload))
        {
            if (entry.Cut == cut && entry.Box == box && entry.Layout == layout)
                return true;
        }
        return false;
    }

    private static string TextOf(MemoryScene scene, string uid, int cut, int box, int layout)
    {
        byte[] payload;
        if (!scene.Parts.TryGetValue(uid, out payload))
            return null;
        foreach (var entry in ParsePayload(payload))
        {
            if (entry.Cut == cut && entry.Box == box && entry.Layout == layout)
                return entry.Text;
        }
        return null;
    }

    private static string MetaOf(MemoryScene scene, string uid)
    {
        byte[] payload;
        if (!scene.Parts.TryGetValue(uid, out payload))
            return null;
        var text = new UTF8Encoding(false).GetString(payload);
        var end = text.IndexOf('\n');
        var first = end < 0 ? text : text.Substring(0, end);
        return first.StartsWith("#meta=", StringComparison.Ordinal) ? first.Substring(6) : string.Empty;
    }

    private static void WriteText(MemoryScene scene, string uid, int cut, int box, int layout, string value)
    {
        if (!scene.Parts.ContainsKey(uid))
        {
            scene.Parts[uid] = BuildPayload(string.Empty);
        }
        var entries = ParsePayload(scene.Parts[uid]);
        var found = false;
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Cut == cut && entries[index].Box == box && entries[index].Layout == layout)
            {
                entries[index].Text = value ?? string.Empty;
                found = true;
                break;
            }
        }
        if (!found)
            entries.Add(new LayoutEntry { Cut = cut, Box = box, Layout = layout, Text = value ?? string.Empty });
        scene.Parts[uid] = BuildPayload(MetaOf(scene, uid), entries.ToArray());
    }

    private static string EncodeText(string text)
    {
        return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static string DecodeText(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character != '\\' || index + 1 >= text.Length)
            {
                builder.Append(character);
                continue;
            }
            index++;
            var next = text[index];
            if (next == 'n') builder.Append('\n');
            else if (next == 'r') builder.Append('\r');
            else if (next == '\\') builder.Append('\\');
            else { builder.Append('\\').Append(next); }
        }
        return builder.ToString();
    }

    // ---- 纯内存游戏替身 ----

    private sealed class MemoryScene
    {
        public readonly Dictionary<string, byte[]> Parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        public readonly Dictionary<string, MemoryNode> Nodes = new Dictionary<string, MemoryNode>(StringComparer.Ordinal);
        public readonly List<string> NodeOrder = new List<string>();
        public object Generation = new object();
        public bool ValidateFail;

        public MemoryScene Clone()
        {
            var clone = new MemoryScene { ValidateFail = ValidateFail };
            foreach (var pair in Parts)
                clone.Parts[pair.Key] = RepairMemoryCopy.Bytes(pair.Value);
            foreach (var uid in NodeOrder)
            {
                var node = Nodes[uid].Clone();
                clone.Nodes[uid] = node;
                clone.NodeOrder.Add(uid);
            }
            return clone;
        }

        public SceneSnapshot ToSnapshot(string title)
        {
            var nodes = new List<NodeSnapshot>();
            foreach (var orderKey in NodeOrder)
            {
                var node = Nodes[orderKey];
                var uid = node.Uid;
                var partKey = Parts.ContainsKey(orderKey) ? orderKey : uid;
                var entries = ParsePayload(Parts.ContainsKey(partKey) ? Parts[partKey] : new byte[0]);
                nodes.Add(new NodeSnapshot
                {
                    uid = uid,
                    name = node.Name,
                    kind = "ADV",
                    outputs = BuildOutputs(node, entries),
                    part = new PartSnapshot
                    {
                        uid = uid,
                        name = node.Name,
                        kind = 1,
                        type = "ADV",
                        cuts = BuildCuts(entries)
                    }
                });
            }
            return new SceneSnapshot
            {
                schemaVersion = "1.0",
                scene = new SceneMetadata
                {
                    title = title,
                    exportedAt = "2026-10-06T00:00:00.0000000Z",
                    gameVersion = "1.0",
                    plugins = new string[0]
                },
                crossPartReferences = new string[0],
                warnings = new SnapshotWarning[0],
                nodes = nodes.ToArray()
            };
        }

        private static OutputSlotSnapshot[] BuildOutputs(MemoryNode node, List<LayoutEntry> entries)
        {
            var outputs = new OutputSlotSnapshot[NodeSlotCount];
            for (var index = 0; index < NodeSlotCount; index++)
            {
                outputs[index] = new OutputSlotSnapshot
                {
                    slot = index,
                    targetUid = node.Slots[index].ChildUid ?? string.Empty,
                    conditionType = node.Slots[index].EndConditionType.ToString(),
                    conditionCount = node.Slots[index].EndConditionCount,
                    fadeType = node.Slots[index].FadeType,
                    fadeTime = node.Slots[index].FadeTime
                };
            }
            return outputs;
        }

        private static CutSnapshot[] BuildCuts(List<LayoutEntry> entries)
        {
            var maxCut = 0;
            foreach (var entry in entries)
                if (entry.Cut > maxCut) maxCut = entry.Cut;
            var cuts = new CutSnapshot[maxCut + 1];
            for (var cut = 0; cut <= maxCut; cut++)
            {
                var maxBox = 0;
                foreach (var entry in entries)
                    if (entry.Cut == cut && entry.Box > maxBox) maxBox = entry.Box;
                var boxes = new TextBoxSnapshot[maxBox + 1];
                for (var box = 0; box <= maxBox; box++)
                {
                    var maxLayout = 0;
                    foreach (var entry in entries)
                        if (entry.Cut == cut && entry.Box == box && entry.Layout > maxLayout) maxLayout = entry.Layout;
                    var layouts = new TextLayoutSnapshot[maxLayout + 1];
                    for (var layout = 0; layout <= maxLayout; layout++)
                    {
                        layouts[layout] = new TextLayoutSnapshot
                        {
                            layoutIndex = layout,
                            rawText = TextOrFiller(entries, cut, box, layout),
                            scriptBlocks = new ScriptBlockSnapshot[0],
                            variableReferences = new VariableReferenceSnapshot[0],
                            warnings = new SnapshotWarning[0]
                        };
                    }
                    boxes[box] = new TextBoxSnapshot
                    {
                        sourceIndex = box,
                        exportOrder = box,
                        sortOrder = box,
                        id = 100 + cut * 10 + box,
                        option = box == 1,
                        layouts = layouts
                    };
                }
                cuts[cut] = new CutSnapshot
                {
                    sourceIndex = cut,
                    exportOrder = cut,
                    endCut = false,
                    optionCount = 1,
                    textBoxes = boxes
                };
            }
            return cuts;
        }

        private static string TextOrFiller(List<LayoutEntry> entries, int cut, int box, int layout)
        {
            foreach (var entry in entries)
            {
                if (entry.Cut == cut && entry.Box == box && entry.Layout == layout)
                    return entry.Text ?? string.Empty;
            }
            return "filler-" + cut + "-" + box + "-" + layout;
        }
    }

    private sealed class MemoryNode
    {
        public string Uid;
        public string Name;
        public int Kind;
        public float PosX;
        public float PosY;
        public readonly RepairSlotValue[] Slots = new RepairSlotValue[NodeSlotCount];
        public byte[] RawCache;

        public MemoryNode Clone()
        {
            var clone = new MemoryNode { Uid = Uid, Name = Name, Kind = Kind, PosX = PosX, PosY = PosY };
            for (var index = 0; index < NodeSlotCount; index++)
                clone.Slots[index] = Slots[index];
            return clone;
        }
    }

    private static class MemorySceneCodec
    {
        public static byte[] Serialize(MemoryNode node)
        {
            var builder = new StringBuilder();
            builder.Append(node.Kind).Append('|').Append(node.Name).Append('|')
                .Append(node.PosX.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(node.PosY.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            for (var index = 0; index < NodeSlotCount; index++)
            {
                var slot = node.Slots[index];
                builder.Append('|').Append(slot.ChildUid ?? string.Empty)
                    .Append('|').Append(slot.EndConditionType)
                    .Append('|').Append(slot.EndConditionCount)
                    .Append('|').Append(slot.FadeType)
                    .Append('|').Append(slot.FadeTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            return new UTF8Encoding(false).GetBytes(builder.ToString());
        }

        // 写回既有实例，模拟游戏侧 NodeBase.Copy 保留引用。
        public static void DeserializeInto(MemoryNode node, byte[] data)
        {
            var parts = new UTF8Encoding(false).GetString(data ?? new byte[0]).Split('|');
            if (parts.Length < 4 + NodeSlotCount * 5)
                throw new InvalidOperationException("节点快照长度不足");
            node.Kind = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            node.Name = parts[1];
            node.PosX = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            node.PosY = float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            for (var index = 0; index < NodeSlotCount; index++)
            {
                var offset = 4 + index * 5;
                node.Slots[index] = new RepairSlotValue
                {
                    ChildUid = parts[offset],
                    EndConditionType = int.Parse(parts[offset + 1], System.Globalization.CultureInfo.InvariantCulture),
                    EndConditionCount = int.Parse(parts[offset + 2], System.Globalization.CultureInfo.InvariantCulture),
                    FadeType = int.Parse(parts[offset + 3], System.Globalization.CultureInfo.InvariantCulture),
                    FadeTime = float.Parse(parts[offset + 4], System.Globalization.CultureInfo.InvariantCulture)
                };
            }
        }

        public static string SlotsText(MemoryNode node)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < NodeSlotCount; index++)
            {
                var slot = node.Slots[index];
                builder.Append(index).Append(':').Append(slot.ChildUid ?? string.Empty).Append('/')
                    .Append(slot.EndConditionType).Append('/').Append(slot.EndConditionCount).Append('/')
                    .Append(slot.FadeType).Append('/')
                    .Append(slot.FadeTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            }
            return builder.ToString();
        }
    }

    private sealed class MemoryHost : IRepairMemoryHost
    {
        private int partWrites;

        public readonly MemoryScene Scene;
        public bool FailReadPart;
        // (目标 UID, 该 UID 的第几次写入) => 是否失败；用于制造部分恢复失败与回滚失败。
        public Func<string, int, bool> FailPartWrite;
        // 写入后抛异常：模拟单步写入已部分生效后失败；参数为 (目标 UID, 该 UID 的第几次写入)。
        public Func<string, int, bool> ThrowPartWriteAfter;
        public Func<string, int, bool> ThrowNodeWriteAfter;
        public Func<string, int, bool> FailNodeWriteAfter;
        // (来源 UID, 槽位, 该槽位第几次写入) => 写入后抛异常 / 写入前返回失败。
        public Func<string, int, int, bool> ThrowSlotWriteAfter;
        public Func<string, int, int, bool> FailSlotWrite;
        public int WritePartCalls;
        private readonly Dictionary<string, int> partWritesByUid = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> nodeWritesByUid = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> slotWritesByKey = new Dictionary<string, int>(StringComparer.Ordinal);

        public MemoryHost(MemoryScene scene)
        {
            Scene = scene;
        }

        public int SlotCount => NodeSlotCount;

        public object SceneOwner => Scene;

        public object NodeOwner => Scene;

        public object GenerationOwner => Scene.Generation;

        public IEnumerable<string> NodeUids => new List<string>(Scene.NodeOrder);

        public bool TryReadPart(string uid, out RepairPartValue value, out string error)
        {
            value = null;
            error = null;
            if (FailReadPart)
            {
                error = "模拟读取 Part 失败";
                return false;
            }
            byte[] payload;
            if (!Scene.Parts.TryGetValue(uid, out payload))
            {
                error = "找不到 Part：" + uid;
                return false;
            }
            // 故意返回内部数组：快照必须自己复制，不能让内存改动穿透。
            value = new RepairPartValue { Uid = uid, Data = payload };
            return true;
        }

        public bool TryWritePart(RepairPartValue value, out string error)
        {
            error = null;
            partWrites++;
            WritePartCalls++;
            if (value == null || string.IsNullOrEmpty(value.Uid))
            {
                error = "无效 Part 记录";
                return false;
            }
            int writesForUid;
            partWritesByUid.TryGetValue(value.Uid, out writesForUid);
            writesForUid++;
            partWritesByUid[value.Uid] = writesForUid;
            if (FailPartWrite != null && FailPartWrite(value.Uid, writesForUid))
            {
                error = "模拟写入 Part 失败（" + value.Uid + " 第 " + writesForUid + " 次）";
                return false;
            }
            // 落库时复制，保证快照字节与内存各自独立。
            Scene.Parts[value.Uid] = RepairMemoryCopy.Bytes(value.Data);
            if (ThrowPartWriteAfter != null && ThrowPartWriteAfter(value.Uid, writesForUid))
                throw new InvalidOperationException("模拟 Part 写入后异常（" + value.Uid + " 第 " + writesForUid + " 次）");
            return true;
        }

        public bool TryReadNode(string uid, out RepairNodeValue value, out string error)
        {
            value = null;
            error = null;
            MemoryNode node;
            if (!Scene.Nodes.TryGetValue(uid, out node))
            {
                error = "找不到节点：" + uid;
                return false;
            }
            node.RawCache = MemorySceneCodec.Serialize(node);
            value = new RepairNodeValue { Uid = uid, Data = node.RawCache };
            return true;
        }

        public bool TryWriteNode(RepairNodeValue value, out string error)
        {
            error = null;
            MemoryNode node;
            if (value == null || string.IsNullOrEmpty(value.Uid) || !Scene.Nodes.TryGetValue(value.Uid, out node))
            {
                error = "找不到节点";
                return false;
            }
            MemorySceneCodec.DeserializeInto(node, value.Data);
            node.RawCache = null;
            int nodeWrites;
            nodeWritesByUid.TryGetValue(value.Uid, out nodeWrites);
            nodeWrites++;
            nodeWritesByUid[value.Uid] = nodeWrites;
            if (ThrowNodeWriteAfter != null && ThrowNodeWriteAfter(value.Uid, nodeWrites))
                throw new InvalidOperationException("模拟节点写入后异常（" + value.Uid + " 第 " + nodeWrites + " 次）");
            if (FailNodeWriteAfter != null && FailNodeWriteAfter(value.Uid, nodeWrites))
            {
                error = "模拟节点写入后返回失败（" + value.Uid + " 第 " + nodeWrites + " 次）";
                return false;
            }
            return true;
        }

        public bool TryReadSlot(string sourceUid, int index, out RepairSlotValue value, out string error)
        {
            value = default(RepairSlotValue);
            error = null;
            MemoryNode node;
            if (!Scene.Nodes.TryGetValue(sourceUid, out node))
            {
                error = "找不到节点：" + sourceUid;
                return false;
            }
            if (index < 0 || index >= NodeSlotCount)
            {
                error = "槽位越界：" + index;
                return false;
            }
            value = node.Slots[index];
            return true;
        }

        public bool TryWriteSlot(string sourceUid, int index, RepairSlotValue value, out string error)
        {
            error = null;
            MemoryNode node;
            if (!Scene.Nodes.TryGetValue(sourceUid, out node))
            {
                error = "找不到节点：" + sourceUid;
                return false;
            }
            if (index < 0 || index >= NodeSlotCount)
            {
                error = "槽位越界：" + index;
                return false;
            }
            var key = sourceUid + ":" + index;
            int slotWrites;
            slotWritesByKey.TryGetValue(key, out slotWrites);
            slotWrites++;
            slotWritesByKey[key] = slotWrites;
            if (FailSlotWrite != null && FailSlotWrite(sourceUid, index, slotWrites))
            {
                error = "模拟槽位写入失败（" + key + " 第 " + slotWrites + " 次）";
                return false;
            }
            node.Slots[index] = value;
            node.RawCache = null;
            if (ThrowSlotWriteAfter != null && ThrowSlotWriteAfter(sourceUid, index, slotWrites))
                throw new InvalidOperationException("模拟槽位写入后异常（" + key + " 第 " + slotWrites + " 次）");
            return true;
        }

        public bool TryValidateTargets(ICollection<string> targetUids, out string error)
        {
            error = null;
            if (Scene.ValidateFail)
            {
                error = "模拟 ADV 编辑界面占用";
                return false;
            }
            foreach (var uid in targetUids ?? new List<string>())
            {
                if (!Scene.Nodes.ContainsKey(uid))
                {
                    error = "找不到节点：" + uid;
                    return false;
                }
            }
            return true;
        }

        public IRepairTargetSink Sink()
        {
            return new MemoryTextSink(Scene);
        }

        public SceneSnapshot ToSnapshot(string title)
        {
            return Scene.ToSnapshot(title);
        }
    }

    private sealed class MemoryTextSink : IRepairTargetSink
    {
        private readonly MemoryScene scene;

        public MemoryTextSink(MemoryScene scene)
        {
            this.scene = scene;
        }

        public bool TryLocate(RepairPlanTarget target, out IRepairTextSlot slot, out string issue)
        {
            slot = null;
            issue = null;
            byte[] payload;
            if (!scene.Parts.TryGetValue(target.NodeUid, out payload))
            {
                issue = "找不到 Part：" + target.NodeUid;
                return false;
            }
            if (!PayloadHas(payload, target.CutSourceIndex, target.TextBoxSourceIndex, target.LayoutIndex))
            {
                issue = "载荷中没有该布局";
                return false;
            }
            slot = new PayloadSlot(scene, target.NodeUid, target.CutSourceIndex,
                target.TextBoxSourceIndex, target.LayoutIndex);
            return true;
        }

        private sealed class PayloadSlot : IRepairTextSlot
        {
            private readonly MemoryScene scene;
            private readonly string uid;
            private readonly int cut;
            private readonly int box;
            private readonly int layout;

            public PayloadSlot(MemoryScene scene, string uid, int cut, int box, int layout)
            {
                this.scene = scene;
                this.uid = uid;
                this.cut = cut;
                this.box = box;
                this.layout = layout;
            }

            public string Read()
            {
                return TextOf(scene, uid, cut, box, layout) ?? string.Empty;
            }

            public void Write(string text)
            {
                WriteText(scene, uid, cut, box, layout, text);
            }
        }
    }

    private sealed class LayoutEntry
    {
        public int Cut;
        public int Box;
        public int Layout;
        public string Text;
    }

    private sealed class Fixture
    {
        public string Root;
        public SceneSnapshot Source;
        public string SceneFile;
        public string BundlePath;
        public RepairBundleSource Bundle;
        public MemoryScene Pristine;

        public MemoryHost NewHost()
        {
            return new MemoryHost(Pristine.Clone());
        }
    }

    private static Fixture BuildFixture(string work, string name)
    {
        var root = Path.Combine(work, name);
        Directory.CreateDirectory(root);
        var scene = BuildMemoryScene();
        var source = scene.ToSnapshot(SceneTitle);
        var bundlePath = Path.Combine(root, "bundle");
        var bundle = WriteBundle(bundlePath, source, true, DefaultBundleId);
        var sceneFile = Path.Combine(root, "edit-scene.png");
        File.WriteAllText(sceneFile, "original-scene-file");
        return new Fixture
        {
            Root = root,
            Source = source,
            SceneFile = sceneFile,
            BundlePath = bundlePath,
            Bundle = bundle,
            Pristine = scene
        };
    }

    private static MemoryScene BuildMemoryScene()
    {
        var scene = new MemoryScene();

        scene.Parts[UidA] = BuildPayload(MetaA,
            new LayoutEntry { Cut = 0, Box = 0, Layout = 0, Text = "a-cut0-box0" },
            new LayoutEntry { Cut = 1, Box = 0, Layout = 0, Text = "a-cut1-box0" },
            new LayoutEntry { Cut = 1, Box = 1, Layout = 0, Text = SiblingA },
            new LayoutEntry { Cut = 1, Box = 1, Layout = 1, Text = TextA });
        scene.Parts[UidB] = BuildPayload("meta-B",
            new LayoutEntry { Cut = 0, Box = 0, Layout = 0, Text = "b-cut0-box0" },
            new LayoutEntry { Cut = 0, Box = 1, Layout = 0, Text = TextB });
        scene.Parts[UidC] = BuildPayload("meta-C",
            new LayoutEntry { Cut = 0, Box = 0, Layout = 0, Text = TextC });

        scene.NodeOrder.Add(UidA);
        scene.NodeOrder.Add(UidB);
        scene.NodeOrder.Add(UidC);
        scene.Nodes[UidA] = BuildNode(UidA, "S2-2A");
        scene.Nodes[UidB] = BuildNode(UidB, "S2-3-4");
        scene.Nodes[UidC] = BuildNode(UidC, "S2-2B");
        // 默认两条入线：UidB 槽位 0 与 UidC 槽位 1 都指向 UidA。
        scene.Nodes[UidB].Slots[0].ChildUid = UidA;
        scene.Nodes[UidB].Slots[0].EndConditionType = 1;
        scene.Nodes[UidC].Slots[1].ChildUid = UidA;
        scene.Nodes[UidC].Slots[1].FadeType = 2;
        return scene;
    }

    private static MemoryNode BuildNode(string uid, string name)
    {
        var node = new MemoryNode { Uid = uid, Name = name, Kind = 1, PosX = 100f, PosY = -50f };
        for (var index = 0; index < NodeSlotCount; index++)
        {
            node.Slots[index] = new RepairSlotValue
            {
                ChildUid = string.Empty,
                EndConditionType = 0,
                EndConditionCount = 0,
                FadeType = 0,
                FadeTime = 0.5f
            };
        }
        return node;
    }

    private static MemoryNode CloneNode(MemoryNode node)
    {
        return node.Clone();
    }

    private static string SlotsText(MemoryNode node)
    {
        return MemorySceneCodec.SlotsText(node);
    }

    private static string WritePlan(Fixture fixture, string name, params string[] changes)
    {
        var path = Path.Combine(fixture.Root, name);
        File.WriteAllText(path, PlanJson(fixture, changes), new UTF8Encoding(false));
        return path;
    }

    private static RepairBundleSource WriteBundle(string root, SceneSnapshot scene, bool withAiSummary, string bundleId)
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
        if (withAiSummary)
            File.WriteAllText(Path.Combine(root, "ai-summary.json"), "{}");
        return RepairBundleReader.Read(root);
    }

    private static string PlanJson(Fixture fixture, params string[] changes)
    {
        return PlanJson(fixture.Bundle.BundleId, fixture.Bundle.ManifestSha256, changes);
    }

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

    private static string Change(string pointer, string nodeUid, int cutSourceIndex, int textBoxSourceIndex,
        int layoutIndex, string before, string after)
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
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(character); break;
            }
        }
        return builder.ToString();
    }

    private static SceneSnapshot Clone(SceneSnapshot scene)
    {
        return SceneSnapshotJson.Read(JsonSnapshotWriter.Serialize(scene));
    }

    private static void SetSnapshotText(SceneSnapshot scene, string uid, int cut, int box, int layout, string text)
    {
        var node = RepairTargetLookup.FindNode(scene, uid);
        var sourceCut = RepairTargetLookup.FindCut(node?.part, cut);
        var textBox = RepairTargetLookup.FindTextBox(sourceCut, box);
        var target = RepairTargetLookup.FindLayout(textBox, layout);
        target.rawText = text;
    }

    private static bool SameBytes(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
            return false;
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
                return false;
        }
        return true;
    }

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition)
            throw new InvalidOperationException(label);
    }
}
