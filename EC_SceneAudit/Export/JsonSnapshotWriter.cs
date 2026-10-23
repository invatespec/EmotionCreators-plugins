using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace EC_SceneAudit.Export
{
    internal static partial class JsonSnapshotWriter
    {
        public static string Serialize(SceneSnapshot snapshot)
        {
            var writer = new JsonWriter();
            writer.StartObject();
            writer.Property("schemaVersion", () => writer.String(snapshot.schemaVersion));
            writer.Property("scene", () => WriteSceneMetadata(writer, snapshot.scene));
            writer.Property("geometry", () => WriteSceneGeometry(writer, snapshot.geometry));
            writer.Property("nodes", () => WriteArray(writer, snapshot.nodes, WriteNodeSnapshot));
            writer.Property(
                "crossPartReferences",
                () => WriteStringArray(writer, snapshot.crossPartReferences));
            writer.Property("warnings", () => WriteArray(writer, snapshot.warnings, WriteWarning));
            writer.EndObject();
            return writer.ToString();
        }

        public static string SerializeAiSummary(AiSummarySnapshot summary)
        {
            var writer = new JsonWriter();
            writer.StartObject();
            writer.Property("schemaVersion", () => writer.String(summary.schemaVersion));
            writer.Property("sourceEvidenceFile", () => writer.String(summary.sourceEvidenceFile));
            writer.Property("scene", () => WriteAiScene(writer, summary.scene));
            writer.Property("statistics", () => WriteAiStatistics(writer, summary.statistics));
            writer.Property("parts", () => WriteArray(writer, summary.parts, WriteAiPart));
            writer.Property("scripts", () => WriteArray(writer, summary.scripts, WriteAiScript));
            writer.Property("findings", () => WriteArray(writer, summary.findings, WriteAiFinding));
            writer.EndObject();
            return writer.ToString();
        }

        private static void WriteAiScene(JsonWriter writer, AiSceneSummary value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }
            writer.StartObject();
            writer.Property("title", () => writer.String(value.title));
            writer.Property("exportedAt", () => writer.String(value.exportedAt));
            writer.EndObject();
        }

        private static void WriteAiStatistics(JsonWriter writer, AiSceneStatistics value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }
            writer.StartObject();
            writer.Property("nodes", () => writer.Integer(value.nodes));
            writer.Property("parts", () => writer.Integer(value.parts));
            writer.Property("cuts", () => writer.Integer(value.cuts));
            writer.Property("textBoxes", () => writer.Integer(value.textBoxes));
            writer.Property("scriptBlocks", () => writer.Integer(value.scriptBlocks));
            writer.Property("warnings", () => writer.Integer(value.warnings));
            writer.EndObject();
        }

        private static void WriteAiPart(JsonWriter writer, AiPartIndex value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }
            writer.StartObject();
            writer.Property("nodeUid", () => writer.String(value.nodeUid));
            writer.Property("uid", () => writer.String(value.uid));
            writer.Property("name", () => writer.String(value.name));
            writer.Property("type", () => writer.String(value.type));
            writer.Property("cutCount", () => writer.Integer(value.cutCount));
            writer.Property("textBoxCount", () => writer.Integer(value.textBoxCount));
            writer.Property("rawRef", () => writer.String(value.rawRef));
            writer.EndObject();
        }

        private static void WriteAiScript(JsonWriter writer, AiScriptSummary value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }
            writer.StartObject();
            writer.Property("rawRef", () => writer.String(value.rawRef));
            writer.Property("parseStatus", () => writer.String(value.parseStatus));
            writer.Property("commands", () => WriteStringArray(writer, value.commands));
            writer.Property("declarations", () => WriteArray(writer, value.declarations, WriteAiVariable));
            writer.Property("reads", () => WriteArray(writer, value.reads, WriteAiVariable));
            writer.Property("writes", () => WriteArray(writer, value.writes, WriteAiVariable));
            writer.Property("steps", () => WriteArray(writer, value.steps, WriteAiStep));
            writer.Property("events", () => WriteArray(writer, value.events, WriteAiEvent));
            writer.Property("findings", () => WriteArray(writer, value.findings, WriteAiFinding));
            writer.EndObject();
        }

        private static void WriteAiVariable(JsonWriter writer, AiVariable value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }
            writer.StartObject();
            writer.Property("name", () => writer.String(value.name));
            writer.Property("category", () => writer.String(value.category));
            writer.Property("operation", () => writer.String(value.operation));
            writer.Property("expression", () => writer.String(value.expression));
            writer.Property("sourceOffset", () => writer.Integer(value.sourceOffset));
            writer.Property("rawRef", () => writer.String(value.rawRef));
            writer.EndObject();
        }

        private static void WriteAiStep(JsonWriter writer, AiLogicStep value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }
            writer.StartObject();
            writer.Property("kind", () => writer.String(value.kind));
            writer.Property("name", () => writer.String(value.name));
            writer.Property("attributes", () => WriteArray(writer, value.attributes, WriteAttribute));
            writer.Property("targetStatus", () => writer.String(value.targetStatus));
            writer.Property("sourceOffset", () => writer.Integer(value.sourceOffset));
            writer.Property("rawRef", () => writer.String(value.rawRef));
            writer.Property("children", () => WriteArray(writer, value.children, WriteAiStep));
            writer.EndObject();
        }

        private static void WriteAiEvent(JsonWriter writer, AiEventTrigger value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }
            writer.StartObject();
            writer.Property("kind", () => writer.String(value.kind));
            writer.Property("attributes", () => WriteArray(writer, value.attributes, WriteAttribute));
            writer.Property("targetStatus", () => writer.String(value.targetStatus));
            writer.Property("sourceOffset", () => writer.Integer(value.sourceOffset));
            writer.Property("rawRef", () => writer.String(value.rawRef));
            writer.EndObject();
        }

        private static void WriteAiFinding(JsonWriter writer, AiFinding value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }
            writer.StartObject();
            writer.Property("code", () => writer.String(value.code));
            writer.Property("message", () => writer.String(value.message));
            writer.Property("rawRef", () => writer.String(value.rawRef));
            writer.EndObject();
        }

        private static void WriteSceneMetadata(JsonWriter writer, SceneMetadata value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("title", () => writer.String(value.title));
            writer.Property("exportedAt", () => writer.String(value.exportedAt));
            writer.Property("gameVersion", () => writer.String(value.gameVersion));
            writer.Property("plugins", () => WriteStringArray(writer, value.plugins));
            writer.EndObject();
        }

        private static void WriteSceneGeometry(JsonWriter writer, SceneGeometrySnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("version", () => writer.Integer(value.version));
            writer.Property("coordinateSpace", () => writer.String(value.coordinateSpace));
            writer.Property("canvas", () => WriteGeometryCanvas(writer, value.canvas));
            writer.Property("nodes", () => WriteArray(writer, value.nodes, WriteGeometryNode));
            writer.EndObject();
        }

        private static void WriteGeometryCanvas(JsonWriter writer, GeometryCanvasSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("width", () => writer.Float(value.width));
            writer.Property("height", () => writer.Float(value.height));
            writer.EndObject();
        }

        private static void WriteGeometryNode(JsonWriter writer, GeometryNodeSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("uid", () => writer.String(value.uid));
            writer.Property("status", () => writer.String(value.status));
            writer.Property("frame", () => WriteGeometryFrame(writer, value.frame));
            writer.Property("positionOffset", () => WriteVector2(writer, value.positionOffset));
            writer.Property("input", () => WriteGeometryPoint(writer, value.input));
            writer.Property("outputs", () => WriteArray(writer, value.outputs, WriteGeometryOutputPoint));
            writer.EndObject();
        }

        private static void WriteGeometryFrame(JsonWriter writer, GeometryFrameSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("x", () => writer.Float(value.x));
            writer.Property("y", () => writer.Float(value.y));
            writer.Property("width", () => writer.Float(value.width));
            writer.Property("height", () => writer.Float(value.height));
            writer.EndObject();
        }

        private static void WriteGeometryPoint(JsonWriter writer, GeometryPointSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("enabled", () => writer.Boolean(value.enabled));
            writer.Property("x", () => writer.Float(value.x));
            writer.Property("y", () => writer.Float(value.y));
            writer.EndObject();
        }

        private static void WriteGeometryOutputPoint(JsonWriter writer, GeometryOutputPointSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("slot", () => writer.Integer(value.slot));
            writer.Property("enabled", () => writer.Boolean(value.enabled));
            writer.Property("x", () => writer.Float(value.x));
            writer.Property("y", () => writer.Float(value.y));
            writer.EndObject();
        }

        private static void WriteNodeSnapshot(JsonWriter writer, NodeSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("uid", () => writer.String(value.uid));
            writer.Property("name", () => writer.String(value.name));
            writer.Property("kind", () => writer.String(value.kind));
            writer.Property("isSystemNode", () => writer.Boolean(value.isSystemNode));
            writer.Property("position", () => WriteVector2(writer, value.position));
            writer.Property("outputs", () => WriteArray(writer, value.outputs, WriteOutputSlot));
            writer.Property("part", () => WritePartSnapshot(writer, value.part));
            writer.EndObject();
        }

        private static void WriteVector2(JsonWriter writer, Vector2Snapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("x", () => writer.Float(value.x));
            writer.Property("y", () => writer.Float(value.y));
            writer.EndObject();
        }

        private static void WriteOutputSlot(JsonWriter writer, OutputSlotSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("slot", () => writer.Integer(value.slot));
            writer.Property("targetUid", () => writer.String(value.targetUid));
            writer.Property("conditionType", () => writer.String(value.conditionType));
            writer.Property("conditionCount", () => writer.Integer(value.conditionCount));
            writer.Property("fadeType", () => writer.Integer(value.fadeType));
            writer.Property("fadeTime", () => writer.Float(value.fadeTime));
            writer.EndObject();
        }

        private static void WritePartSnapshot(JsonWriter writer, PartSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("uid", () => writer.String(value.uid));
            writer.Property("name", () => writer.String(value.name));
            writer.Property("kind", () => writer.Integer(value.kind));
            writer.Property("type", () => writer.String(value.type));
            writer.Property("cuts", () => WriteArray(writer, value.cuts, WriteCutSnapshot));
            writer.EndObject();
        }

        private static void WriteCutSnapshot(JsonWriter writer, CutSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("sourceIndex", () => writer.Integer(value.sourceIndex));
            writer.Property("exportOrder", () => writer.Integer(value.exportOrder));
            writer.Property("endCut", () => writer.Boolean(value.endCut));
            writer.Property("optionCount", () => writer.Integer(value.optionCount));
            writer.Property("textBoxes", () => WriteArray(writer, value.textBoxes, WriteTextBox));
            writer.EndObject();
        }

        private static void WriteTextBox(JsonWriter writer, TextBoxSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("sourceIndex", () => writer.Integer(value.sourceIndex));
            writer.Property("exportOrder", () => writer.Integer(value.exportOrder));
            writer.Property("sortOrder", () => writer.Integer(value.sortOrder));
            writer.Property("id", () => writer.Integer(value.id));
            writer.Property("option", () => writer.Boolean(value.option));
            writer.Property("layouts", () => WriteArray(writer, value.layouts, WriteTextLayout));
            writer.EndObject();
        }

        private static void WriteTextLayout(JsonWriter writer, TextLayoutSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("layoutIndex", () => writer.Integer(value.layoutIndex));
            writer.Property("rawText", () => writer.String(value.rawText));
            writer.Property("containsScript", () => writer.Boolean(value.containsScript));
            writer.Property(
                "scriptBlocks",
                () => WriteArray(writer, value.scriptBlocks, WriteScriptBlock));
            writer.Property(
                "variableReferences",
                () => WriteArray(writer, value.variableReferences, WriteVariableReference));
            writer.Property("warnings", () => WriteArray(writer, value.warnings, WriteWarning));
            writer.EndObject();
        }

        private static void WriteScriptBlock(JsonWriter writer, ScriptBlockSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("raw", () => writer.String(value.raw));
            writer.Property("sourceOffset", () => writer.Integer(value.sourceOffset));
            writer.Property("commands", () => WriteStringArray(writer, value.commands));
            writer.Property(
                "attributes",
                () => WriteArray(writer, value.attributes, WriteAttribute));
            writer.Property(
                "references",
                () => WriteArray(writer, value.references, WriteScriptReference));
            writer.EndObject();
        }

        private static void WriteScriptReference(JsonWriter writer, ScriptReferenceSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("kind", () => writer.String(value.kind));
            writer.Property("name", () => writer.String(value.name));
            writer.Property(
                "attributes",
                () => WriteArray(writer, value.attributes, WriteAttribute));
            writer.Property("sourceOffset", () => writer.Integer(value.sourceOffset));
            writer.Property("targetStatus", () => writer.String(value.targetStatus));
            writer.EndObject();
        }

        private static void WriteAttribute(JsonWriter writer, AttributeSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("key", () => writer.String(value.key));
            writer.Property("value", () => writer.String(value.value));
            writer.EndObject();
        }

        private static void WriteVariableReference(
            JsonWriter writer,
            VariableReferenceSnapshot value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("raw", () => writer.String(value.raw));
            writer.Property("name", () => writer.String(value.name));
            writer.Property("category", () => writer.String(value.category));
            writer.Property("sourceOffset", () => writer.Integer(value.sourceOffset));
            writer.EndObject();
        }

        private static void WriteWarning(JsonWriter writer, SnapshotWarning value)
        {
            if (value == null)
            {
                writer.Null();
                return;
            }

            writer.StartObject();
            writer.Property("code", () => writer.String(value.code));
            writer.Property("message", () => writer.String(value.message));
            writer.Property("source", () => writer.String(value.source));
            writer.EndObject();
        }

        private static void WriteStringArray(JsonWriter writer, string[] values)
        {
            if (values == null)
            {
                writer.Null();
                return;
            }

            writer.StartArray();
            foreach (var value in values)
                writer.Element(() => writer.String(value));
            writer.EndArray();
        }

        private static void WriteArray<T>(
            JsonWriter writer,
            T[] values,
            Action<JsonWriter, T> writeValue)
        {
            if (values == null)
            {
                writer.Null();
                return;
            }

            writer.StartArray();
            foreach (var value in values)
                writer.Element(() => writeValue(writer, value));
            writer.EndArray();
        }

        internal static string SanitizeFileName(string value, string fallback)
        {
            var safe = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(safe.Length);
            foreach (var character in safe)
                builder.Append(Array.IndexOf(invalidChars, character) >= 0 ? '_' : character);

            var result = builder.ToString().Trim().Trim('.');
            return string.IsNullOrEmpty(result) ? fallback : result;
        }

        private sealed class JsonWriter
        {
            private readonly StringBuilder _builder = new StringBuilder();
            private readonly List<bool> _hasEntries = new List<bool>();
            private int _indent;

            public void StartObject()
            {
                _builder.Append('{');
                _hasEntries.Add(false);
                _indent++;
            }

            public void EndObject()
            {
                EndContainer('}');
            }

            public void StartArray()
            {
                _builder.Append('[');
                _hasEntries.Add(false);
                _indent++;
            }

            public void EndArray()
            {
                EndContainer(']');
            }

            public void Property(string name, Action writeValue)
            {
                PrepareEntry();
                String(name);
                _builder.Append(": ");
                writeValue();
            }

            public void Element(Action writeValue)
            {
                PrepareEntry();
                writeValue();
            }

            public void Null()
            {
                _builder.Append("null");
            }

            public void Boolean(bool value)
            {
                _builder.Append(value ? "true" : "false");
            }

            public void Integer(int value)
            {
                _builder.Append(value.ToString(CultureInfo.InvariantCulture));
            }

            public void Float(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    Null();
                    return;
                }

                _builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }

            public void String(string value)
            {
                if (value == null)
                {
                    Null();
                    return;
                }

                _builder.Append('"');
                foreach (var character in value)
                {
                    switch (character)
                    {
                        case '"':
                            _builder.Append("\\\"");
                            break;
                        case '\\':
                            _builder.Append("\\\\");
                            break;
                        case '\b':
                            _builder.Append("\\b");
                            break;
                        case '\f':
                            _builder.Append("\\f");
                            break;
                        case '\n':
                            _builder.Append("\\n");
                            break;
                        case '\r':
                            _builder.Append("\\r");
                            break;
                        case '\t':
                            _builder.Append("\\t");
                            break;
                        default:
                            if (character < 0x20
                                || (character >= 0xD800 && character <= 0xDFFF))
                            {
                                _builder.Append("\\u");
                                _builder.Append(
                                    ((int)character).ToString("X4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                _builder.Append(character);
                            }
                            break;
                    }
                }

                _builder.Append('"');
            }

            public override string ToString()
            {
                if (_hasEntries.Count != 0)
                    throw new InvalidOperationException("JSON writer has unclosed containers.");

                return _builder.ToString();
            }

            private void PrepareEntry()
            {
                if (_hasEntries.Count == 0)
                    throw new InvalidOperationException("JSON value must be inside a container.");

                if (_hasEntries[_hasEntries.Count - 1])
                    _builder.Append(',');

                _builder.AppendLine();
                AppendIndent();
                _hasEntries[_hasEntries.Count - 1] = true;
            }

            private void EndContainer(char closingCharacter)
            {
                if (_hasEntries.Count == 0)
                    throw new InvalidOperationException("JSON writer container stack is empty.");

                var hasEntries = _hasEntries[_hasEntries.Count - 1];
                _indent--;
                _hasEntries.RemoveAt(_hasEntries.Count - 1);
                if (hasEntries)
                {
                    _builder.AppendLine();
                    AppendIndent();
                }

                _builder.Append(closingCharacter);
            }

            private void AppendIndent()
            {
                for (var index = 0; index < _indent; index++)
                    _builder.Append("  ");
            }
        }

    }
}
