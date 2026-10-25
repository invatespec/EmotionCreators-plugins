using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EC_SceneAudit.Repair;

internal static class RepairApplyRegression
{
    private static int assertions;
    private static readonly RepairApplyEngine Engine = new RepairApplyEngine();

    private static int Main()
    {
        try
        {
            MatchingAndRepeatedRestore();
            MissingAndExtraTargets();
            PartialWritesAndCapture();
            IndependentSnapshotsAndReload();
            FileInput();
            PointerAndWindow();
            Console.WriteLine("PASS: " + assertions + " memory repair assertions.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void MatchingAndRepeatedRestore()
    {
        var host = new Memory();
        host.Parts["A"].Text = "游戏内已修改";
        host.Slots["P"][3] = new RepairSlotValue { ChildUid = "A", EndConditionCount = 7 };
        var report = Engine.Apply(Plan(Change("A", "旧稿", "新稿")), host, host);
        Check(report.AppliedCount == 1, "before differences do not block writing");
        Check(host.Parts["A"].Text == "新稿", "matching target written");
        Check(host.Parts["A"].Sibling == "A sibling", "other layout untouched");
        Check(report.Snapshot.Parts.Count == 1, "only affected part captured");
        Check(report.Snapshot.InboundSlots.Count == 1, "related inbound slot captured");
        host.Slots["P"][3] = new RepairSlotValue { ChildUid = "B" };
        Check(Engine.Restore(report, host), "first restore");
        Check(host.Parts["A"].Text == "游戏内已修改", "snapshot uses actual memory, not plan before");
        Check(host.Slots["P"][3].ChildUid == "A" && host.Slots["P"][3].EndConditionCount == 7, "inbound data restored");
        Check(report.HasSnapshot && report.Phase == RepairApplyPhase.Restored, "restored snapshot retained");
        host.Parts["A"].Text = "恢复后又改了";
        Check(Engine.Restore(report, host), "second restore allowed");
        Check(host.Parts["A"].Text == "游戏内已修改", "same original snapshot reused");

        var empty = Engine.Apply(Plan(Change("A", "任意", "")), host, host);
        Check(empty.AppliedCount == 1 && host.Parts["A"].Text == "", "empty after applied");
        var again = Engine.Apply(Plan(Change("A", "旧稿", "第二次")), host, host);
        Check(again.HasSnapshot && again.AppliedCount == 1, "new application not blocked by held snapshots");
        Check(Engine.Restore(again, host) && host.Parts["A"].Text == "", "latest snapshot restores latest pre-apply state");
    }

    private static void MissingAndExtraTargets()
    {
        var host = new Memory();
        var plan = Plan(Change("missing", "x", "y"), Change("A", "x", "A new"),
            Change("A", "x", "missing cut", cut: 8), Change("B", "x", "B new"));
        var missingBox = Change("A", "x", "unused");
        missingBox.Target.TextBoxSourceIndex = 99;
        plan.Changes.Add(missingBox);
        var missingLayout = Change("A", "x", "unused");
        missingLayout.Target.LayoutIndex = 99;
        plan.Changes.Add(missingLayout);
        var report = Engine.Apply(plan, host, host);
        Check(report.AppliedCount == 2 && report.Issues.Count == 0, "missing part/cut ignored");
        Check(host.Parts["P"].Text == "P original", "extra node untouched");
        Check(report.Snapshot.PartCount == 2, "snapshot excludes missing targets");
        host.Parts.Remove("A");
        host.Slots.Remove("A");
        host.Parts["extra"] = new Part { Text = "new node" };
        host.Slots["extra"] = new RepairSlotValue[5];
        host.Parts["B"].Text = "B changed after apply";
        Check(Engine.Restore(report, host), "deleted snapshot target does not block other restores");
        Check(host.Parts["B"].Text == "B original" && host.Parts["extra"].Text == "new node", "available restored, extra preserved");
        Check(!host.Parts.ContainsKey("A"), "restore does not recreate deleted node");
        var none = Engine.Apply(Plan(Change("missing", "x", "y")), host, host);
        Check(!none.HasSnapshot && none.AppliedCount == 0, "no matches does not replace usable snapshot");
    }

    private static void PartialWritesAndCapture()
    {
        var host = new Memory();
        var report = Engine.Apply(Plan(Change("A", "old", "A new"), Change("B", "old", "fail"),
            Change("P", "old", "P new")), host, host);
        Check(report.AppliedCount == 2 && report.Issues.Count == 1, "write exception does not stop later item");
        Check(host.Parts["A"].Text == "A new" && host.Parts["P"].Text == "P new", "successful writes retained");
        Check(host.Parts["B"].Text == "B original", "failed item alone compensated");
        Check(report.HasSnapshot && Engine.Restore(report, host), "partial application remains restorable");

        host.FailedCaptureUid = "A";
        var other = Engine.Apply(Plan(Change("A", "old", "A new"), Change("B", "old", "B new")), host, host);
        Check(other.AppliedCount == 1 && host.Parts["A"].Text == "A original", "uncapturable item skipped individually");
        Check(other.Snapshot.HasPart("B") && !other.Snapshot.HasPart("A"), "capture keeps available target");
    }

    private static void IndependentSnapshotsAndReload()
    {
        var host = new Memory();
        var report = Engine.Apply(Plan(Change("A", "old", "new")), host, host);
        host.Parts["A"].Sibling = "changed sibling";
        host.Slots["P"][1] = new RepairSlotValue { ChildUid = "B", FadeType = 4 };
        Check(Engine.Restore(report, host), "deep snapshot restored");
        Check(host.Parts["A"].Sibling == "A sibling", "affected part independent snapshot");
        Check(host.Slots["P"][1].FadeType == 4, "unrelated inbound slot preserved");
        host.Generation = new object();
        Check(!Engine.Restore(report, host), "same UID in reloaded scene rejects old snapshot");
        Check(report.HasSnapshot, "failed restore retains snapshot");
    }

    private static void FileInput()
    {
        var root = Path.GetFullPath(".tmp/scene-audit-tests");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "repair-plan.json");
        var json = @"{""format"":""ec-scene-repair-plan"",""version"":1,""source"":{""bundleId"":""old-bundle"",""manifestSha256"":""" +
            new string('0', 64) + @"""},""changes"":[{""target"":{""reference"":{""fileId"":""scene"",""kind"":""json-pointer"",""pointer"":""/nodes/0/part/cuts/0/textBoxes/0/layouts/0/rawText""},""nodeUid"":""A"",""cutSourceIndex"":0,""textBoxSourceIndex"":0,""layoutIndex"":0},""before"":""old"",""after"":""new""}]}";
        File.WriteAllText(path, json, new UTF8Encoding(true));
        var host = new Memory();
        var report = Engine.Apply(path, host, host);
        Check(report.AppliedCount == 1, "file input works without any original bundle");
        Check(File.ReadAllText(path) == json, "input not rewritten");
        Check(RepairPlanReader.Parse(json).Changes[0].Target.NodeUid == "A", "v1 reader retained");
        Check(RepairPlanReader.DecodeUtf8(new byte[] { 0xEF, 0xBB, 0xBF, 65 }, "test") == "A", "UTF8 BOM");
    }

    private static void PointerAndWindow()
    {
        var pointer = new RepairPointerOwnership();
        pointer.ButtonDown(0, false);
        Check(!pointer.ShouldBlock(true), "external drag remains available over window");
        pointer.ButtonUp(0);
        Check(!pointer.ShouldBlock(true), "external release frame available");
        pointer.BeginFrame();
        Check(pointer.ShouldBlock(true), "idle hover blocks game");
        pointer.ButtonDown(0, true);
        Check(pointer.ShouldBlock(false), "internal drag remains blocked outside");
        pointer.Reset();
        Check(!pointer.ShouldBlock(false), "hidden/focus loss releases pointer ownership");

        var box = RepairWindowGeometry.FromNormalized(0.5f, 0.35f, 1280, 720);
        Check(box.Width == 640 && box.Height == 440, "compact window");
        var small = RepairWindowGeometry.FromNormalized(1, 1, 400, 300);
        Check(small.X >= 0 && small.Y >= 0 && small.X + small.Width <= 400 && small.Y + small.Height <= 300, "small screen clamp");
        RepairWindowGeometry.Normalize(box, 1280, 720, out var x, out var y);
        var reopened = RepairWindowGeometry.FromNormalized(x, y, 1280, 720);
        Check(Math.Abs(reopened.X - box.X) < 0.001 && Math.Abs(reopened.Y - box.Y) < 0.001, "reopen preserves position");
        Check(RepairPathText.Sanitize("a\r\nb\nc") == "abc", "path remains single line");
    }

    private static RepairPlan Plan(params RepairChange[] changes) => new RepairPlan { Changes = new List<RepairChange>(changes) };
    private static RepairChange Change(string uid, string before, string after, int cut = 0) => new RepairChange
    {
        Target = new RepairPlanTarget { NodeUid = uid, CutSourceIndex = cut, TextBoxSourceIndex = 0, LayoutIndex = 0 },
        Before = before, After = after
    };
    private static void Check(bool value, string label) { assertions++; if (!value) throw new Exception(label); }
    private sealed class Part { public string Text; public string Sibling; }

    private sealed class Memory : IRepairMemoryHost, IRepairTargetSink
    {
        public readonly Dictionary<string, Part> Parts = new Dictionary<string, Part>();
        public readonly Dictionary<string, RepairSlotValue[]> Slots = new Dictionary<string, RepairSlotValue[]>();
        public object Generation = new object();
        public string FailedCaptureUid;
        public Memory()
        {
            foreach (var uid in new[] { "A", "B", "P" })
            {
                Parts[uid] = new Part { Text = uid + " original", Sibling = uid + " sibling" };
                Slots[uid] = new RepairSlotValue[5];
            }
        }
        public int SlotCount => 5;
        public object SceneOwner => this;
        public object NodeOwner => Slots;
        public object GenerationOwner => Generation;
        public IEnumerable<string> NodeUids => Slots.Keys;
        public bool TryValidateTargets(ICollection<string> targets, out string error) { error = null; return true; }

        public bool TryLocate(RepairPlanTarget target, out IRepairTextSlot slot, out string issue)
        {
            slot = null; issue = null;
            if (!Parts.ContainsKey(target.NodeUid) || target.CutSourceIndex != 0 || target.TextBoxSourceIndex != 0 || target.LayoutIndex != 0)
                return false;
            slot = new TextSlot(this, target.NodeUid);
            return true;
        }

        public bool TryReadPart(string uid, out RepairPartValue value, out string error)
        {
            value = null; error = null;
            if (uid == FailedCaptureUid || !Parts.TryGetValue(uid, out var part)) return false;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                { writer.Write(part.Text); writer.Write(part.Sibling); }
                value = new RepairPartValue { Uid = uid, Data = stream.ToArray() };
            }
            return true;
        }

        public bool TryWritePart(RepairPartValue value, out string error)
        {
            error = null;
            if (!Parts.ContainsKey(value.Uid)) return false;
            using (var reader = new BinaryReader(new MemoryStream(value.Data)))
                Parts[value.Uid] = new Part { Text = reader.ReadString(), Sibling = reader.ReadString() };
            return true;
        }

        public bool TryReadNode(string uid, out RepairNodeValue value, out string error)
        {
            value = null; error = null;
            if (!Slots.TryGetValue(uid, out var slots)) return false;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    foreach (var slot in slots)
                    {
                        writer.Write(slot.ChildUid ?? ""); writer.Write(slot.EndConditionType);
                        writer.Write(slot.EndConditionCount); writer.Write(slot.FadeType); writer.Write(slot.FadeTime);
                    }
                }
                value = new RepairNodeValue { Uid = uid, Data = stream.ToArray() };
            }
            return true;
        }

        public bool TryWriteNode(RepairNodeValue value, out string error)
        {
            error = null;
            if (!Slots.ContainsKey(value.Uid)) return false;
            using (var reader = new BinaryReader(new MemoryStream(value.Data)))
            {
                for (var i = 0; i < 5; i++)
                {
                    var slot = new RepairSlotValue { ChildUid = reader.ReadString(), EndConditionType = reader.ReadInt32(),
                        EndConditionCount = reader.ReadInt32(), FadeType = reader.ReadInt32(), FadeTime = reader.ReadSingle() };
                    if (slot.ChildUid == "" || Slots.ContainsKey(slot.ChildUid)) Slots[value.Uid][i] = slot;
                }
            }
            return true;
        }

        public bool TryReadSlot(string uid, int index, out RepairSlotValue value, out string error)
        {
            value = default; error = null;
            if (!Slots.ContainsKey(uid) || index < 0 || index >= 5) return false;
            value = Slots[uid][index]; return true;
        }

        public bool TryWriteSlot(string uid, int index, RepairSlotValue value, out string error)
        {
            error = null;
            if (!Slots.ContainsKey(uid) || index < 0 || index >= 5) return false;
            Slots[uid][index] = value; return true;
        }

        private sealed class TextSlot : IRepairTextSlot
        {
            private readonly Memory memory; private readonly string uid;
            public TextSlot(Memory memory, string uid) { this.memory = memory; this.uid = uid; }
            public string Read() => memory.Parts[uid].Text;
            public void Write(string text)
            {
                memory.Parts[uid].Text = text;
                if (text == "fail") throw new Exception("simulated write error");
            }
        }
    }
}
