using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using EC_SceneAudit.Export;

internal static class AiSummaryBuilderRegression
{
    private static int assertions;

    private static int Main(string[] args)
    {
        try
        {
            VerifyWriteAttributesAndNormalization();
            VerifyInvalidXmlDoesNotBuildLogicTree();
            VerifyNestedFlowAndFindings();
            VerifyConditionBoundaries();
            VerifyWriteAttributePriority();
            if (args.Length > 0)
                ReplayEvidence(args[0]);
            Console.WriteLine("PASS: " + assertions + " assertions.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyWriteAttributesAndNormalization()
    {
        var summary = BuildSummary(
            "<script>"
            + "<set name=\"#tempText#\" value=\"text\"/>"
            + "<set variable=\"#legacyVariable#\" value=\"1\"/>"
            + "<set var=\"#legacyVar#\" value=\"2\"/>"
            + "<add result=\"#nowMoney#\" value=\"-80\"/>"
            + "<compute result=\"#computedMoney#\" value1=\"#nowMoney#\" operator=\"+\" value2=\"1\"/>"
            + "<delete name=\"#deletedText#\"/>"
            + "</script>");
        var expectedNames = new[]
        {
            "tempText",
            "legacyVariable",
            "legacyVar",
            "nowMoney",
            "computedMoney",
            "deletedText"
        };
        var expectedOperations = new[] { "set", "set", "set", "add", "compute", "delete" };

        AssertEqual("valid", summary.parseStatus, "valid script parse status");
        AssertEqual(expectedNames.Length, summary.writes.Length, "write count");
        for (var index = 0; index < expectedNames.Length; index++)
        {
            AssertEqual(expectedNames[index], summary.writes[index].name, "normalized write name " + index);
            AssertEqual(expectedOperations[index], summary.writes[index].operation, "write operation " + index);
        }
    }

    private static void VerifyInvalidXmlDoesNotBuildLogicTree()
    {
        var summary = BuildSummary("<script><set result=\"#broken#\" value=\"1\"></script>");

        AssertEqual("invalid", summary.parseStatus, "invalid script parse status");
        AssertEqual(0, summary.steps.Length, "invalid script logic steps");
        AssertEqual(0, summary.writes.Length, "invalid script writes");
        AssertEqual(1, summary.findings.Length, "invalid script finding count");
        AssertEqual("script-xml-invalid", summary.findings[0].code, "invalid script finding code");
    }

    private static AiScriptSummary BuildSummary(string raw)
    {
        var layout = new VariableModScanner().Scan(
            raw, Path.GetTempPath(), new HashSet<string> { "streamA", "streamB" }, 3);
        var snapshot = new SceneSnapshot
        {
            nodes = new[]
            {
                new NodeSnapshot
                {
                    part = new PartSnapshot
                    {
                        cuts = new[]
                        {
                            new CutSnapshot
                            {
                                textBoxes = new[]
                                {
                                    new TextBoxSnapshot
                                    {
                                        layouts = new[] { layout }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        return AiSummaryBuilder.Build(snapshot, "fixture.json").scripts[0];
    }

    private static void VerifyNestedFlowAndFindings()
    {
        var summary = BuildSummary(
            "<script><event-next>"
            + "<if value1=\"#phaseKey#\" operator=\"eq\" value2=\"2\"><goto-part name=\"streamA\"/></if>"
            + "<if value1=\"#phaseKey#\" operator=\"lteq\" value2=\"2\"><goto-part name=\"streamB\"/></if>"
            + "</event-next><save/><load/><scan-saves/><save-folder/></script>");

        AssertEqual("valid", summary.parseStatus, "nested script parse status");
        AssertEqual(5, summary.steps.Length, "root action order");
        AssertEqual("event-next", summary.events[0].kind, "event trigger");
        var conditions = summary.steps[0].children;
        AssertEqual(2, conditions.Length, "nested condition count");
        for (var index = 0; index < conditions.Length; index++)
        {
            AssertEqual("if", conditions[index].name, "condition order " + index);
            var jump = conditions[index].children[0];
            AssertEqual("goto-part", jump.name, "nested jump " + index);
            AssertEqual("unverified", jump.targetStatus, "jump target " + index);
            AssertEqual(index == 0 ? "streamA" : "streamB", jump.attributes[0].value, "jump name " + index);
        }
        AssertEqual(1, summary.findings.Count(item => item.code == "condition-overlap"), "condition overlap");
        for (var index = 1; index < summary.steps.Length; index++)
            AssertEqual("assumed-valid", summary.steps[index].targetStatus, "SaveLoad command " + index);
    }

    private static void VerifyWriteAttributePriority()
    {
        var summary = BuildSummary(
            "<script><set name=\"primary\" variable=\"secondary\" var=\"third\" result=\"fallback\"/>"
            + "<add result=\"#nowMoney#\" value=\"1\"/>"
            + "<add result=\"#nowMoney#\" value=\"2\"/></script>");
        AssertEqual("primary", summary.writes[0].name, "write attribute priority");
        AssertEqual(3, summary.writes.Length, "separate write operations");
        AssertEqual(false, summary.writes[1].rawRef == summary.writes[2].rawRef, "distinct command locators");
    }

    private static void VerifyConditionBoundaries()
    {
        var unrelated = BuildSummary(
            "<script><if value1=\"#phaseKey#\" operator=\"eq\" value2=\"2\"/>"
            + "<if value1=\"#otherKey#\" operator=\"lteq\" value2=\"2\"/>"
            + "<if value1=\"#phaseKey#\" operator=\"gt\" value2=\"2\"/></script>");
        AssertEqual(0, unrelated.findings.Count(item => item.code == "condition-overlap"), "unrelated conditions");
        var legacy = BuildSummary(
            "<script><if condition=\"#phaseKey# == 2\"/>"
            + "<if condition=\"#phaseKey# &lt;= 2\"/></script>");
        AssertEqual(1, legacy.findings.Count(item => item.code == "condition-overlap"), "legacy condition format");
    }

    private static void ReplayEvidence(string evidencePath)
    {
        var options = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        var snapshot = JsonSerializer.Deserialize<SceneSnapshot>(File.ReadAllText(evidencePath), options);
        var summary = AiSummaryBuilder.Build(snapshot, Path.GetFullPath(evidencePath));
        var outputPath = Path.Combine(AppContext.BaseDirectory, "replayed-ai-summary.json");
        File.WriteAllText(outputPath, JsonSerializer.Serialize(summary, options));
        Console.WriteLine("Offline builder replay: " + outputPath);
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                label + ": expected '" + expected + "', got '" + actual + "'.");
        }
    }
}
