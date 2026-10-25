using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EC_SceneAudit.Repair;

internal static class NodePatchRegression
{
    private static int assertions;
    private static readonly NodePatchEngine Engine = new NodePatchEngine();

    private static int Main()
    {
        try
        {
            MatchingAndRepeatedRestore();
            MissingNodesAndSlots();
            PartialFailuresAndExpansion();
            FileInputAndFormat();
            Console.WriteLine("PASS: " + assertions + " node patch assertions.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void MatchingAndRepeatedRestore()
    {
        var host = new Memory();
        var before = host.Positions["A"];
        host.Slots["A"][0] = new NodePatchSlotValue("B", "AllRoot", 13, 4, 1.5f);
        var plan = Plan();
        plan.Layout.Nodes.Add(Placement("A"));
        plan.Connections.Add(Connection("A", 0, "C"));
        var report = Engine.Apply(plan, host);
        Check(report.PlacementCount == 1 && report.ConnectionCount == 1, "edited before does not block any matching item");
        Check(host.Positions["A"].X == 300 && host.Slots["A"][0].TargetUid == "C", "after values applied");
        Check(host.Slots["A"][0].ConditionCount == 13 && host.Slots["A"][0].ConditionType == "AllRoot", "current condition parameters preserved");
        Check(report.Snapshot.Slots[0].Before.TargetUid == "B", "snapshot uses actual current target");
        Check(Engine.Restore(report, host), "first restore allowed");
        Check(host.Positions["A"].X == before.X && host.Slots["A"][0].TargetUid == "B", "actual pre-apply values restored");
        Check(report.HasSnapshot && report.Phase == NodePatchApplyPhase.Restored, "restored snapshot remains usable");
        host.Positions["A"] = new NodePatchPlacementValue { X = 900, Y = -900 };
        host.Slots["A"][0].TargetUid = "C";
        Check(Engine.Restore(report, host), "second restore allowed");
        Check(host.Positions["A"].X == before.X && host.Slots["A"][0].TargetUid == "B", "same snapshot restores again");
        var again = Engine.Apply(plan, host);
        Check(again.HasSnapshot && again.ConnectionCount == 1, "held snapshot does not prevent applying again");
        host.Generation = new object();
        Check(!Engine.Restore(report, host), "reloaded scene cannot use old snapshot");
        Check(report.HasSnapshot, "reload refusal does not discard recovery record");
    }

    private static void MissingNodesAndSlots()
    {
        var host = new Memory();
        var plan = Plan();
        plan.Layout.Nodes.Add(Placement("missing"));
        plan.Layout.Nodes.Add(Placement("A"));
        plan.Connections.Add(Connection("missing", 0, "C"));
        plan.Connections.Add(Connection("A", 3, "missing"));
        plan.Connections.Add(Connection("B", 8, "C"));
        plan.Connections.Add(Connection("B", 0, "C"));
        var report = Engine.Apply(plan, host);
        Check(report.PlacementCount == 1 && report.ConnectionCount == 1, "missing source/target/slot ignored");
        Check(report.Issues.Count == 0, "missing entries do not report a blocking failure");
        Check(host.Positions["extra"].X == 700 && host.Slots["extra"][0].TargetUid == "A", "extra nodes untouched");
        Check(report.Snapshot.Placements.Count == 1 && report.Snapshot.Slots.Count == 1, "only matching fields captured");
        host.Positions.Remove("B");
        host.Slots.Remove("B");
        Check(Engine.Restore(report, host), "deleted source does not block remaining restore");
        Check(host.Positions["A"].X == 10, "remaining placement restored");
        Check(!host.Positions.ContainsKey("B"), "missing source not recreated");

        var noMatches = Plan();
        noMatches.Layout.Nodes.Add(Placement("missing"));
        Check(!Engine.Apply(noMatches, host).HasSnapshot, "no match keeps previous snapshot available");

        var missingOldTarget = Plan();
        missingOldTarget.Connections.Add(Connection("A", 0, "C"));
        host.Slots["A"][0].TargetUid = "deleted-before";
        var oldReport = Engine.Apply(missingOldTarget, host);
        Check(oldReport.ConnectionCount == 1, "old before target need not exist");
        Check(Engine.Restore(oldReport, host) && host.Slots["A"][0].TargetUid == "C", "missing old endpoint ignored during restore");

        host.Slots["A"][0].TargetUid = "";
        var emptyReport = Engine.Apply(missingOldTarget, host);
        Check(emptyReport.ConnectionCount == 1, "currently empty source slot can receive a matching target");
        Check(Engine.Restore(emptyReport, host) && host.Slots["A"][0].TargetUid == "", "empty original slot restored");
    }

    private static void PartialFailuresAndExpansion()
    {
        var host = new Memory { FailingUid = "A", SupportsResize = true };
        var plan = Plan();
        plan.Layout.Canvas.Width = 6000;
        plan.Layout.Nodes.Add(Placement("A"));
        plan.Layout.Nodes.Add(Placement("B"));
        plan.Connections.Add(Connection("B", 0, "C"));
        var report = Engine.Apply(plan, host);
        Check(report.PlacementCount == 1 && report.ConnectionCount == 1, "false write does not cancel other writes");
        Check(report.Issues.Count == 1 && report.HasSnapshot, "failed attempted field remains recoverable");
        Check(host.Width == 6000, "existing absolute resize API used");
        host.FailingUid = null;
        Check(Engine.Restore(report, host), "partial result restorable");
        Check(host.Positions["A"].X == 10 && host.Positions["B"].X == 20, "failed attempted field also restored");
        Check(host.Width == 6000, "restoration keeps expanded canvas");

        host.SupportsResize = false;
        var noApi = Engine.Apply(plan, host);
        Check(noApi.PlacementCount == 2 && noApi.ConnectionCount == 1, "missing resize API does not reject node patch");

        host.SupportsResize = true;
        host.FailResize = true;
        host.Width = 5000;
        var resizeFailure = Engine.Apply(plan, host);
        Check(resizeFailure.PlacementCount == 2 && resizeFailure.ConnectionCount == 1, "resize failure still applies available nodes");
        Check(resizeFailure.Issues.Count == 1, "actual resize failure explained");

        host.FailingUid = "A";
        host.ThrowWrite = true;
        var thrown = Engine.Apply(plan, host);
        Check(thrown.PlacementCount == 1 && thrown.ConnectionCount == 1, "write exception does not stop later items");
    }

    private static void FileInputAndFormat()
    {
        var root = Path.GetFullPath(".tmp/scene-audit-tests");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "node-patch.json");
        var json = @"{""format"":""ec-scene-node-patch"",""version"":1,""source"":{""bundleId"":""old-bundle"",""manifestSha256"":""" +
            new string('0', 64) + @"""},""connections"":[{""sourceUid"":""A"",""slot"":0,""before"":{""targetUid"":""B"",""conditionType"":""ConnectPart"",""conditionCount"":0,""fadeType"":0,""fadeTime"":0},""after"":{""targetUid"":""C""}}],""layout"":{""canvas"":{""width"":5000,""height"":3000},""nodes"":[{""uid"":""A"",""before"":{""x"":100,""y"":-100},""after"":{""x"":300,""y"":-300}}]}}";
        File.WriteAllText(path, json, new UTF8Encoding(true));
        var host = new Memory();
        var report = Engine.Apply(path, host);
        Check(report.PlacementCount == 1 && report.ConnectionCount == 1, "file applied without any bundle directory");
        Check(File.ReadAllText(path) == json, "input file not overwritten");
        Check(NodePatchReader.Parse(json).Connections[0].After.TargetUid == "C", "existing v1 format retained");
        ExpectFailure(() => NodePatchReader.Parse(json.Replace(@"""slot"":0", @"""slot"":9")), "invalid slot format rejected");
        ExpectFailure(() => NodePatchReader.Parse(json.Replace(@"""version"":1", @"""version"":2")), "unknown format version rejected");
        ExpectFailure(() => NodePatchReader.Parse(json.Replace(@"""after"":{""targetUid"":""C""}", @"""after"":{""targetUid"":""A""}")), "existing self-target format constraint retained");
    }

    private static NodePatchPlan Plan() => new NodePatchPlan
        { Layout = new NodeLayoutPatch { Canvas = new NodeCanvasValue { Width = 5000, Height = 3000 } } };
    private static NodePlacementPatch Placement(string uid) => new NodePlacementPatch
        { Uid = uid, Before = new NodePositionValue { X = 100, Y = -100 }, After = new NodePositionValue { X = 300, Y = -300 } };
    private static NodeConnectionPatch Connection(string uid, int slot, string target) => new NodeConnectionPatch
    {
        SourceUid = uid, Slot = slot, Before = new NodeSlotValue { TargetUid = "B", ConditionType = "ConnectPart" },
        After = new NodeConnectionTarget { TargetUid = target }
    };
    private static void Check(bool value, string label) { assertions++; if (!value) throw new Exception(label); }
    private static void ExpectFailure(Action action, string label)
    { try { action(); } catch (RepairInputException) { assertions++; return; } throw new Exception(label); }

    private sealed class Memory : INodePatchMemoryHost
    {
        public readonly Dictionary<string, NodePatchPlacementValue> Positions = new Dictionary<string, NodePatchPlacementValue>();
        public readonly Dictionary<string, NodePatchSlotValue[]> Slots = new Dictionary<string, NodePatchSlotValue[]>();
        public object Generation = new object();
        public string FailingUid;
        public bool SupportsResize;
        public bool FailResize;
        public bool ThrowWrite;
        public float Width = 5000;
        public Memory()
        {
            var x = 10f;
            foreach (var uid in new[] { "A", "B", "C", "extra" })
            {
                Positions[uid] = new NodePatchPlacementValue { X = uid == "extra" ? 700 : x, Y = -x };
                Slots[uid] = new NodePatchSlotValue[5];
                x += 10;
            }
            Slots["A"][0].TargetUid = "B";
            Slots["B"][0].TargetUid = "A";
            Slots["extra"][0].TargetUid = "A";
        }
        public object SceneOwner => this;
        public object NodeOwner => Positions;
        public object GenerationOwner => Generation;
        public bool TryValidateTargets(ICollection<string> targets, out string error) { error = null; return true; }
        public bool TryGetCanvas(out float width, out float height, out bool supportsResize, out string error)
        { width = Width; height = 3000; supportsResize = SupportsResize; error = null; return true; }
        public bool TryResizeAbsolute(float width, float height, out float actualWidth, out float actualHeight, out string error)
        { actualWidth = Width = width; actualHeight = height; error = FailResize ? "resize failed" : null; return !FailResize; }
        public bool TryReadPlacement(string uid, out NodePatchPlacementValue value, out string error)
        { error = null; return Positions.TryGetValue(uid, out value); }
        public bool TryWritePlacement(string uid, NodePatchPlacementValue value, out string error)
        {
            error = null;
            if (!Positions.ContainsKey(uid)) return false;
            Positions[uid] = value;
            if (uid != FailingUid) return true;
            if (ThrowWrite) throw new Exception("write failed");
            error = "write failed"; return false;
        }
        public bool TryReadSlot(string uid, int slot, out NodePatchSlotValue value, out string error)
        {
            value = default; error = null;
            if (!Slots.ContainsKey(uid) || slot < 0 || slot >= 5) return false;
            value = Slots[uid][slot]; return true;
        }
        public bool TryWriteSlotTarget(string uid, int slot, string target, out string error)
        {
            error = null;
            if (!Slots.ContainsKey(uid) || slot < 0 || slot >= 5 || (!string.IsNullOrEmpty(target) && !Positions.ContainsKey(target))) return false;
            Slots[uid][slot].TargetUid = target; return true;
        }
    }
}
