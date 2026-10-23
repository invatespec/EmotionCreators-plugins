using System.Collections.Generic;
using EC_SceneAudit.Export;

namespace EC_SceneAudit.Repair
{
    // 读取 F8 资料包内 scene.json 为共享快照模型，保留原始数组位置与源序号。
    // 必填字段缺失或类型不符时整份拒绝，不用默认 0 冒充索引。
    internal static class SceneSnapshotJson
    {
        public static SceneSnapshot Read(string json)
        {
            var root = RepairJson.Parse(json);
            if (!root.IsObject)
                throw new RepairInputException("资料包 scene 必须是 JSON 对象。");
            return new SceneSnapshot
            {
                schemaVersion = OptionalString(root, "schemaVersion"),
                scene = ReadMetadata(root.Member("scene")),
                geometry = ReadGeometry(root.Member("geometry")),
                nodes = ReadNodes(root),
                crossPartReferences = new string[0],
                warnings = ReadWarnings(root.Member("warnings"), "warnings")
            };
        }

        private static SceneMetadata ReadMetadata(RepairJsonValue value)
        {
            RequiredObject(value, "scene.scene");
            return new SceneMetadata
            {
                title = RequiredString(value, "title", "scene.scene"),
                exportedAt = OptionalString(value, "exportedAt"),
                gameVersion = OptionalString(value, "gameVersion"),
                plugins = new string[0]
            };
        }

        private static NodeSnapshot[] ReadNodes(RepairJsonValue root)
        {
            var nodesValue = RequiredArray(root, "nodes", "scene");
            var nodes = new NodeSnapshot[nodesValue.Count];
            for (var index = 0; index < nodesValue.Count; index++)
            {
                var path = "scene nodes[" + index + "]";
                var nodeValue = RequiredObject(nodesValue.Items[index], path);
                var uid = RequiredString(nodeValue, "uid", path);
                if (uid.Length == 0)
                    throw new RepairInputException("资料包 scene 结构不完整：" + path + ".uid 为空。");
                nodes[index] = new NodeSnapshot
                {
                    uid = uid,
                    name = OptionalString(nodeValue, "name") ?? uid,
                    kind = OptionalString(nodeValue, "kind"),
                    isSystemNode = false,
                    position = ReadVector2(nodeValue.Member("position")),
                    outputs = ReadOutputs(nodeValue.Member("outputs"), path + ".outputs"),
                    part = ReadPart(nodeValue.Member("part"), path + ".part")
                };
            }
            return nodes;
        }

        private static SceneGeometrySnapshot ReadGeometry(RepairJsonValue value)
        {
            if (value == null || value.IsNull)
                return null;
            if (!value.IsObject)
                throw new RepairInputException("资料包 scene.geometry 必须是 JSON 对象或 null。");
            var version = RequiredInteger(value, "version", "scene.geometry");
            if (version != 1)
                throw new RepairInputException("资料包 scene.geometry.version 不支持：" + version + "。");
            var coordinateSpace = RequiredString(value, "coordinateSpace", "scene.geometry");
            if (coordinateSpace != "grid-ui-top-left-y-down")
                throw new RepairInputException("资料包 scene.geometry.coordinateSpace 不支持：" + coordinateSpace + "。");
            var canvas = ReadGeometryCanvas(RequiredObject(value.Member("canvas"), "scene.geometry.canvas"));
            var nodesValue = RequiredArray(value, "nodes", "scene.geometry");
            var nodes = new GeometryNodeSnapshot[nodesValue.Count];
            for (var index = 0; index < nodesValue.Count; index++)
                nodes[index] = ReadGeometryNode(nodesValue.Items[index], "scene.geometry.nodes[" + index + "]");
            return new SceneGeometrySnapshot
            {
                version = (int)version,
                coordinateSpace = coordinateSpace,
                canvas = canvas,
                nodes = nodes
            };
        }

        private static GeometryCanvasSnapshot ReadGeometryCanvas(RepairJsonValue value)
        {
            var width = RequiredFloat(value, "width", "scene.geometry.canvas");
            var height = RequiredFloat(value, "height", "scene.geometry.canvas");
            if (!(width > 0f) || !(height > 0f))
                throw new RepairInputException("资料包 scene.geometry.canvas 宽高必须是正有限数。");
            return new GeometryCanvasSnapshot { width = width, height = height };
        }

        private static GeometryNodeSnapshot ReadGeometryNode(RepairJsonValue value, string path)
        {
            if (value == null || !value.IsObject)
                throw new RepairInputException("资料包 " + path + " 必须是 JSON 对象。");
            var uid = RequiredString(value, "uid", path);
            if (uid.Length == 0)
                throw new RepairInputException("资料包 " + path + ".uid 为空。");
            var status = RequiredString(value, "status", path);
            if (status != "exact" && status != "unavailable")
                throw new RepairInputException("资料包 " + path + ".status 不支持：" + status + "。");

            var frame = ReadGeometryFrame(value.Member("frame"), path + ".frame");
            var offset = ReadVector2(value.Member("positionOffset"));
            var input = ReadGeometryPoint(value.Member("input"), path + ".input");
            var outputs = ReadGeometryOutputs(value.Member("outputs"), path + ".outputs");
            if (status == "exact" && (frame == null || offset == null))
                throw new RepairInputException("资料包 " + path + " 标记 exact 但缺少 frame 或 positionOffset。");
            return new GeometryNodeSnapshot
            {
                uid = uid,
                status = status,
                frame = frame,
                positionOffset = offset,
                input = input,
                outputs = outputs
            };
        }

        private static GeometryFrameSnapshot ReadGeometryFrame(RepairJsonValue value, string path)
        {
            if (value == null || value.IsNull)
                return null;
            if (!value.IsObject)
                throw new RepairInputException("资料包 " + path + " 必须是 JSON 对象或 null。");
            var width = RequiredFloat(value, "width", path);
            var height = RequiredFloat(value, "height", path);
            if (!(width > 0f) || !(height > 0f))
                throw new RepairInputException("资料包 " + path + " 宽高必须是正有限数。");
            return new GeometryFrameSnapshot
            {
                x = RequiredFloat(value, "x", path),
                y = RequiredFloat(value, "y", path),
                width = width,
                height = height
            };
        }

        private static GeometryPointSnapshot ReadGeometryPoint(RepairJsonValue value, string path)
        {
            if (value == null || value.IsNull)
                return null;
            if (!value.IsObject)
                throw new RepairInputException("资料包 " + path + " 必须是 JSON 对象或 null。");
            return new GeometryPointSnapshot
            {
                enabled = RequiredBool(value, "enabled", path),
                x = RequiredFloat(value, "x", path),
                y = RequiredFloat(value, "y", path)
            };
        }

        private static GeometryOutputPointSnapshot[] ReadGeometryOutputs(RepairJsonValue value, string path)
        {
            if (value == null || value.IsNull)
                return new GeometryOutputPointSnapshot[0];
            if (!value.IsArray)
                throw new RepairInputException("资料包 " + path + " 必须是数组。");
            var outputs = new List<GeometryOutputPointSnapshot>();
            var seen = new HashSet<int>();
            for (var index = 0; index < value.Count; index++)
            {
                var pointPath = path + "[" + index + "]";
                var pointValue = RequiredObject(value.Items[index], pointPath);
                var slot = RequiredInteger(pointValue, "slot", pointPath);
                if (slot < 0 || slot > 4 || !seen.Add((int)slot))
                    throw new RepairInputException("资料包 " + pointPath + ".slot 无效或重复：" + slot + "。");
                outputs.Add(new GeometryOutputPointSnapshot
                {
                    slot = (int)slot,
                    enabled = RequiredBool(pointValue, "enabled", pointPath),
                    x = RequiredFloat(pointValue, "x", pointPath),
                    y = RequiredFloat(pointValue, "y", pointPath)
                });
            }
            return outputs.ToArray();
        }

        private static OutputSlotSnapshot[] ReadOutputs(RepairJsonValue value, string path)
        {
            if (value == null || value.IsNull)
                return new OutputSlotSnapshot[0];
            if (!value.IsArray)
                throw new RepairInputException("资料包 " + path + " 必须是数组。");
            var outputs = new OutputSlotSnapshot[value.Count];
            for (var index = 0; index < value.Count; index++)
            {
                var itemPath = path + "[" + index + "]";
                var item = RequiredObject(value.Items[index], itemPath);
                outputs[index] = new OutputSlotSnapshot
                {
                    slot = (int)RequiredInteger(item, "slot", itemPath),
                    targetUid = OptionalString(item, "targetUid") ?? string.Empty,
                    conditionType = OptionalString(item, "conditionType") ?? string.Empty,
                    conditionCount = OptionalInteger(item, "conditionCount") ?? 0,
                    fadeType = OptionalInteger(item, "fadeType") ?? 0,
                    fadeTime = OptionalFloat(item, "fadeTime") ?? 0f
                };
            }
            return outputs;
        }

        private static Vector2Snapshot ReadVector2(RepairJsonValue value)
        {
            if (value == null || value.IsNull)
                return null;
            if (!value.IsObject)
                throw new RepairInputException("资料包向量必须是 JSON 对象或 null。");
            return new Vector2Snapshot
            {
                x = RequiredFloat(value, "x", "vector"),
                y = RequiredFloat(value, "y", "vector")
            };
        }

        private static PartSnapshot ReadPart(RepairJsonValue value, string path)
        {
            if (value == null || value.IsNull)
                return null;
            if (!value.IsObject)
                throw new RepairInputException("资料包 scene 结构不完整：" + path + " 不是 JSON 对象。");
            return new PartSnapshot
            {
                uid = RequiredString(value, "uid", path),
                name = OptionalString(value, "name"),
                cuts = ReadCuts(value, path)
            };
        }

        private static CutSnapshot[] ReadCuts(RepairJsonValue part, string path)
        {
            var cutsValue = RequiredArray(part, "cuts", path);
            var cuts = new CutSnapshot[cutsValue.Count];
            for (var index = 0; index < cutsValue.Count; index++)
            {
                var cutPath = path + ".cuts[" + index + "]";
                var cutValue = RequiredObject(cutsValue.Items[index], cutPath);
                cuts[index] = new CutSnapshot
                {
                    sourceIndex = RequiredIndex(cutValue, "sourceIndex", cutPath),
                    textBoxes = ReadTextBoxes(cutValue, cutPath)
                };
            }
            return cuts;
        }

        private static TextBoxSnapshot[] ReadTextBoxes(RepairJsonValue cut, string path)
        {
            var boxesValue = RequiredArray(cut, "textBoxes", path);
            var boxes = new TextBoxSnapshot[boxesValue.Count];
            for (var index = 0; index < boxesValue.Count; index++)
            {
                var boxPath = path + ".textBoxes[" + index + "]";
                var boxValue = RequiredObject(boxesValue.Items[index], boxPath);
                boxes[index] = new TextBoxSnapshot
                {
                    sourceIndex = RequiredIndex(boxValue, "sourceIndex", boxPath),
                    id = RequiredIndex(boxValue, "id", boxPath),
                    option = RequiredBool(boxValue, "option", boxPath),
                    layouts = ReadLayouts(boxValue, boxPath)
                };
            }
            return boxes;
        }

        private static TextLayoutSnapshot[] ReadLayouts(RepairJsonValue box, string path)
        {
            var layoutsValue = RequiredArray(box, "layouts", path);
            var layouts = new TextLayoutSnapshot[layoutsValue.Count];
            for (var index = 0; index < layoutsValue.Count; index++)
            {
                var layoutPath = path + ".layouts[" + index + "]";
                var layoutValue = RequiredObject(layoutsValue.Items[index], layoutPath);
                layouts[index] = new TextLayoutSnapshot
                {
                    layoutIndex = RequiredIndex(layoutValue, "layoutIndex", layoutPath),
                    rawText = RequiredString(layoutValue, "rawText", layoutPath),
                    scriptBlocks = new ScriptBlockSnapshot[0],
                    variableReferences = new VariableReferenceSnapshot[0],
                    warnings = ReadWarnings(layoutValue.Member("warnings"), layoutPath + ".warnings")
                };
            }
            return layouts;
        }

        private static SnapshotWarning[] ReadWarnings(RepairJsonValue value, string path)
        {
            if (value == null)
                return new SnapshotWarning[0];
            if (!value.IsArray)
                throw new RepairInputException("资料包 " + path + " 必须是数组。");
            var warnings = new SnapshotWarning[value.Count];
            for (var index = 0; index < value.Count; index++)
            {
                var warningPath = path + "[" + index + "]";
                var warningValue = RequiredObject(value.Items[index], warningPath);
                warnings[index] = new SnapshotWarning(
                    RequiredString(warningValue, "code", warningPath),
                    OptionalString(warningValue, "message"),
                    OptionalString(warningValue, "source"));
            }
            return warnings;
        }

        private static RepairJsonValue RequiredArray(RepairJsonValue parent, string name, string path)
        {
            var value = parent.Member(name);
            if (value == null || !value.IsArray)
                throw new RepairInputException("资料包 scene 结构不完整：" + path + "." + name + " 必须是数组。");
            return value;
        }

        private static RepairJsonValue RequiredObject(RepairJsonValue value, string path)
        {
            if (value == null || !value.IsObject)
                throw new RepairInputException("资料包 scene 结构不完整：" + path + " 必须是 JSON 对象。");
            return value;
        }

        private static string RequiredString(RepairJsonValue parent, string name, string path)
        {
            var value = parent.Member(name);
            if (value == null || !value.IsString)
                throw new RepairInputException("资料包 scene 结构不完整：" + path + "." + name + " 必须是文本。");
            return value.Text;
        }

        private static string OptionalString(RepairJsonValue parent, string name)
        {
            var value = parent.Member(name);
            return value != null && value.IsString ? value.Text : null;
        }

        private static int RequiredIndex(RepairJsonValue parent, string name, string path)
        {
            var value = parent.Member(name);
            if (value == null || !value.IsNumber || !value.TryGetInteger(out var number)
                || number < 0 || number > int.MaxValue)
                throw new RepairInputException("资料包 scene 结构不完整：" + path + "." + name + " 必须是非负整数。");
            return (int)number;
        }

        private static long RequiredInteger(RepairJsonValue parent, string name, string path)
        {
            var value = parent.Member(name);
            if (value == null || !value.IsNumber || !value.TryGetInteger(out var number))
                throw new RepairInputException("资料包 scene 结构不完整：" + path + "." + name + " 必须是整数。");
            return number;
        }

        private static int? OptionalInteger(RepairJsonValue parent, string name)
        {
            var value = parent.Member(name);
            if (value == null || value.IsNull)
                return null;
            if (!value.IsNumber || !value.TryGetInteger(out var number)
                || number < int.MinValue || number > int.MaxValue)
                throw new RepairInputException("资料包 scene 字段 " + name + " 必须是整数。");
            return (int)number;
        }

        private static float RequiredFloat(RepairJsonValue parent, string name, string path)
        {
            var value = parent.Member(name);
            if (value == null || !value.IsNumber || !TryFloat(value, out var number))
                throw new RepairInputException("资料包 scene 结构不完整：" + path + "." + name + " 必须是有限数字。");
            return number;
        }

        private static float? OptionalFloat(RepairJsonValue parent, string name)
        {
            var value = parent.Member(name);
            if (value == null || value.IsNull)
                return null;
            if (!value.IsNumber || !TryFloat(value, out var number))
                throw new RepairInputException("资料包 scene 字段 " + name + " 必须是有限数字。");
            return number;
        }

        private static bool TryFloat(RepairJsonValue value, out float number)
        {
            number = 0f;
            if (value == null || !value.IsNumber || value.Text == null
                || !float.TryParse(value.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                || float.IsNaN(parsed) || float.IsInfinity(parsed))
                return false;
            number = parsed;
            return true;
        }

        private static bool RequiredBool(RepairJsonValue parent, string name, string path)
        {
            var value = parent.Member(name);
            if (value == null || value.Kind != RepairJsonKind.Bool)
                throw new RepairInputException("资料包 scene 结构不完整：" + path + "." + name + " 必须是布尔值。");
            return value.Boolean;
        }
    }
}
