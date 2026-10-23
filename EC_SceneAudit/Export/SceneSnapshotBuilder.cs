using System;
using System.Collections.Generic;
using BepInEx;
using HEdit;
using HarmonyLib;
using UnityEngine;
using YS_Node;

namespace EC_SceneAudit.Export
{
    internal sealed class SceneSnapshotBuilder
    {
        private const int PartKindH = 0;
        private const int PartKindAdv = 1;
        private const int OutputSlotCount = 5;
        private const string GeometryCoordinateSpace = "grid-ui-top-left-y-down";
        private const string GeometryExact = "exact";
        private const string GeometryUnavailable = "unavailable";

        private static readonly AccessTools.FieldRef<NodeUI, RectTransform> InputRectRef =
            AccessTools.FieldRefAccess<NodeUI, RectTransform>("rtfInput");
        private static readonly AccessTools.FieldRef<NodeUI, RectTransform[]> OutputRectsRef =
            AccessTools.FieldRefAccess<NodeUI, RectTransform[]>("rtfOutput");

        private readonly VariableModScanner scanner = new VariableModScanner();
        private readonly string externalScriptRoot;

        public SceneSnapshotBuilder(string externalScriptRoot)
        {
            this.externalScriptRoot = externalScriptRoot;
        }

        public SceneSnapshot Build(
            out int nodeCount,
            out int partCount,
            out int cutCount,
            out int textBoxCount)
        {
            nodeCount = 0;
            partCount = 0;
            cutCount = 0;
            textBoxCount = 0;

            var warnings = new List<SnapshotWarning>();
            var crossPartReferences = new List<string>();
            var snapshot = new SceneSnapshot
            {
                schemaVersion = "1.0",
                scene = new SceneMetadata
                {
                    title = string.Empty,
                    exportedAt = DateTime.UtcNow.ToString("o"),
                    gameVersion = Application.version,
                    plugins = new string[0]
                },
                geometry = null,
                nodes = new NodeSnapshot[0],
                crossPartReferences = new string[0],
                warnings = new SnapshotWarning[0]
            };

            HEditData data = null;
            NodeControl nodeControl = null;
            try
            {
                if (Singleton<HEditData>.IsInstance())
                    data = Singleton<HEditData>.Instance;
            }
            catch (Exception ex)
            {
                warnings.Add(new SnapshotWarning("source-unavailable", "读取 HEditData 失败：" + ex.Message));
            }

            try
            {
                if (Singleton<HEditGlobal>.IsInstance())
                    nodeControl = Singleton<HEditGlobal>.Instance?.nodeControl;
            }
            catch (Exception ex)
            {
                warnings.Add(new SnapshotWarning("source-unavailable", "读取 HEditGlobal 失败：" + ex.Message));
            }

            if (data != null && data.info != null && !string.IsNullOrEmpty(data.info.title))
                snapshot.scene.title = data.info.title;
            if (data == null)
                warnings.Add(new SnapshotWarning("source-unavailable", "HEditData 不可用，无法读取 Part 数据。"));
            if (nodeControl == null)
                warnings.Add(new SnapshotWarning("source-unavailable", "nodeControl 不可用，无法读取节点图。"));
            if (nodeControl?.dictNode == null)
                warnings.Add(new SnapshotWarning("source-unavailable", "nodeControl.dictNode 不可用，无法读取节点图。"));

            if (nodeControl?.dictNode != null)
            {
                var knownPartNames = CollectKnownPartNames(nodeControl.dictNode, data);
                var nodes = new List<NodeSnapshot>();
                foreach (var pair in nodeControl.dictNode)
                {
                    var node = BuildNode(
                        pair.Key,
                        pair.Value,
                        data,
                        nodeControl.dictNode,
                        knownPartNames,
                        warnings,
                        crossPartReferences,
                        ref partCount,
                        ref cutCount,
                        ref textBoxCount);
                    if (node != null)
                    {
                        nodes.Add(node);
                        nodeCount++;
                    }
                }
                snapshot.nodes = nodes.ToArray();
                snapshot.geometry = BuildGeometry(nodeControl, warnings);
            }

            snapshot.crossPartReferences = crossPartReferences.ToArray();
            snapshot.warnings = warnings.ToArray();
            return snapshot;
        }

        private static SceneGeometrySnapshot BuildGeometry(NodeControl nodeControl, List<SnapshotWarning> warnings)
        {
            var grid = nodeControl?.rtfGrid;
            if (grid == null || nodeControl.dictNode == null)
            {
                warnings.Add(new SnapshotWarning(
                    "geometry-unavailable",
                    "节点画布不可用，无法导出几何。"));
                return null;
            }

            var canvasSize = grid.sizeDelta;
            if (!IsFinitePositive(canvasSize.x) || !IsFinitePositive(canvasSize.y))
            {
                warnings.Add(new SnapshotWarning(
                    "geometry-unavailable",
                    "节点画布尺寸无效：" + canvasSize));
                return null;
            }

            var geometryNodes = new List<GeometryNodeSnapshot>();
            foreach (var pair in nodeControl.dictNode)
            {
                var nodeUi = pair.Value;
                var nodeBase = nodeUi?.nodeBase;
                var uid = nodeBase == null
                    ? pair.Key
                    : (string.IsNullOrEmpty(nodeBase.uid) ? pair.Key : nodeBase.uid);
                if (nodeUi == null || nodeBase == null)
                {
                    warnings.Add(new SnapshotWarning(
                        "geometry-unavailable",
                        "节点对象或 NodeBase 为空。",
                        uid));
                    geometryNodes.Add(UnavailableGeometry(uid));
                    continue;
                }

                GeometryNodeSnapshot geometry;
                string error;
                if (!TrySampleGeometry(nodeUi, grid, out geometry, out error))
                {
                    warnings.Add(new SnapshotWarning("geometry-unavailable", error, uid));
                    geometryNodes.Add(UnavailableGeometry(uid));
                    continue;
                }

                geometryNodes.Add(geometry);
            }

            return new SceneGeometrySnapshot
            {
                version = 1,
                coordinateSpace = GeometryCoordinateSpace,
                canvas = new GeometryCanvasSnapshot
                {
                    width = canvasSize.x,
                    height = canvasSize.y
                },
                nodes = geometryNodes.ToArray()
            };
        }

        private static GeometryNodeSnapshot UnavailableGeometry(string uid)
        {
            return new GeometryNodeSnapshot
            {
                uid = uid ?? string.Empty,
                status = GeometryUnavailable,
                frame = null,
                positionOffset = null,
                input = null,
                outputs = new GeometryOutputPointSnapshot[0]
            };
        }

        private static bool TrySampleGeometry(
            NodeUI nodeUi,
            RectTransform grid,
            out GeometryNodeSnapshot geometry,
            out string error)
        {
            geometry = null;
            error = null;
            var rect = nodeUi.transform as RectTransform;
            GeometryFrameSnapshot frame;
            if (!TrySampleFrame(rect, grid, out frame, out error))
                return false;

            var nodeBase = nodeUi.nodeBase;
            float offsetX;
            float offsetY;
            if (!NodeGeometryMath.TryComputePositionOffset(
                frame.x, frame.y, nodeBase.pos.x, nodeBase.pos.y, out offsetX, out offsetY))
            {
                error = "节点几何偏移不是有限值。";
                return false;
            }
            var offset = new Vector2Snapshot
            {
                x = offsetX,
                y = offsetY
            };

            GeometryPointSnapshot input = null;
            var inputRect = InputRectRef(nodeUi);
            if (inputRect != null)
            {
                if (!TrySamplePoint(inputRect, grid, frame, out input, out error))
                    return false;
                input.enabled = inputRect.gameObject.activeSelf;
            }

            var outputRects = OutputRectsRef(nodeUi);
            var outputs = new List<GeometryOutputPointSnapshot>();
            if (outputRects != null)
            {
                for (var index = 0; index < outputRects.Length; index++)
                {
                    var outputRect = outputRects[index];
                    if (outputRect == null)
                        continue;
                    GeometryPointSnapshot point;
                    if (!TrySamplePoint(outputRect, grid, frame, out point, out error))
                    {
                        error = "输出槽 " + index + " 几何读取失败：" + error;
                        return false;
                    }
                    outputs.Add(new GeometryOutputPointSnapshot
                    {
                        slot = index,
                        enabled = outputRect.gameObject.activeSelf,
                        x = point.x,
                        y = point.y
                    });
                }
            }

            geometry = new GeometryNodeSnapshot
            {
                uid = nodeBase.uid,
                status = GeometryExact,
                frame = frame,
                positionOffset = offset,
                input = input,
                outputs = outputs.ToArray()
            };
            return true;
        }

        private static bool TrySampleFrame(
            RectTransform rect,
            RectTransform grid,
            out GeometryFrameSnapshot frame,
            out string error)
        {
            frame = null;
            error = null;
            if (rect == null || grid == null)
            {
                error = "节点 RectTransform 或画布不可用。";
                return false;
            }

            var scale = rect.localScale;
            if (!IsFinite(scale.x) || !IsFinite(scale.y)
                || !IsFinite(scale.z) || scale.x <= 0f || scale.y <= 0f
                || Mathf.Abs(scale.x - scale.y) > 0.001f
                || Mathf.Abs(scale.z - 1f) > 0.001f)
            {
                error = "节点使用非均匀或非单位缩放，无法稳定转换几何。";
                return false;
            }
            if (Quaternion.Angle(rect.localRotation, Quaternion.identity) > 0.01f)
            {
                error = "节点存在旋转，无法用轴对齐矩形表示。";
                return false;
            }

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var minX = float.MaxValue;
            var maxX = float.MinValue;
            var minY = float.MaxValue;
            var maxY = float.MinValue;
            for (var index = 0; index < corners.Length; index++)
            {
                Vector2 point;
                if (!TryConvertToCanvas(corners[index], grid, out point))
                {
                    error = "节点世界角点无法转换到画布坐标。";
                    return false;
                }
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxY = Mathf.Max(maxY, point.y);
            }

            var width = maxX - minX;
            var height = maxY - minY;
            if (!IsFinite(minX) || !IsFinite(minY) || !IsFinite(width) || !IsFinite(height)
                || width <= 0f || height <= 0f)
            {
                error = "节点运行时矩形尺寸无效。";
                return false;
            }

            frame = new GeometryFrameSnapshot
            {
                x = minX,
                y = minY,
                width = width,
                height = height
            };
            return true;
        }

        private static bool TrySamplePoint(
            RectTransform rect,
            RectTransform grid,
            GeometryFrameSnapshot frame,
            out GeometryPointSnapshot point,
            out string error)
        {
            point = null;
            error = null;
            if (rect == null || grid == null)
            {
                error = "端口 RectTransform 或画布不可用。";
                return false;
            }

            Vector2 canvasPoint;
            if (!TryConvertToCanvas(rect.TransformPoint(rect.rect.center), grid, out canvasPoint))
            {
                error = "端口世界坐标无法转换到画布坐标。";
                return false;
            }
            float relativeX;
            float relativeY;
            if (!NodeGeometryMath.ToRelativePoint(
                canvasPoint.x, canvasPoint.y, frame, out relativeX, out relativeY))
            {
                error = "端口相对几何不是有限值。";
                return false;
            }

            point = new GeometryPointSnapshot
            {
                enabled = false,
                x = relativeX,
                y = relativeY
            };
            return true;
        }

        private static bool TryConvertToCanvas(Vector3 worldPoint, RectTransform grid, out Vector2 point)
        {
            point = default(Vector2);
            if (grid == null)
                return false;
            var local = grid.InverseTransformPoint(worldPoint);
            point = new Vector2(
                local.x - grid.rect.xMin,
                grid.rect.yMax - local.y);
            return IsFinite(point.x) && IsFinite(point.y);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinitePositive(float value)
        {
            return IsFinite(value) && value > 0f;
        }

        private NodeSnapshot BuildNode(
            string key,
            NodeUI nodeUi,
            HEditData data,
            Dictionary<string, NodeUI> allNodes,
            ISet<string> knownPartNames,
            List<SnapshotWarning> warnings,
            List<string> crossPartReferences,
            ref int partCount,
            ref int cutCount,
            ref int textBoxCount)
        {
            if (nodeUi == null || nodeUi.nodeBase == null)
            {
                warnings.Add(new SnapshotWarning("node-read-failed", "节点对象或 NodeBase 为空。", key));
                return null;
            }

            var nodeBase = nodeUi.nodeBase;
            var uid = string.IsNullOrEmpty(nodeBase.uid) ? key : nodeBase.uid;
            var isSystemNode = uid == "node_start" || uid == "node_end";
            var node = new NodeSnapshot
            {
                uid = uid,
                name = string.IsNullOrEmpty(nodeBase.name) ? uid : nodeBase.name,
                kind = nodeBase.kind.ToString(),
                isSystemNode = isSystemNode,
                position = new Vector2Snapshot
                {
                    x = nodeBase.pos.x,
                    y = nodeBase.pos.y
                },
                outputs = BuildOutputs(uid, nodeBase, allNodes, warnings),
                part = null
            };

            if (!isSystemNode)
            {
                var part = FindPart(data, key, uid);
                if (part == null)
                {
                    warnings.Add(new SnapshotWarning(
                        "missing-part",
                        "节点没有对应的 Part 数据。",
                        uid));
                }
                else
                {
                    partCount++;
                    node.part = BuildPart(
                        node,
                        part,
                        knownPartNames,
                        warnings,
                        crossPartReferences,
                        ref cutCount,
                        ref textBoxCount);
                }
            }

            return node;
        }

        private static OutputSlotSnapshot[] BuildOutputs(
            string nodeUid,
            NodeBase nodeBase,
            Dictionary<string, NodeUI> allNodes,
            List<SnapshotWarning> warnings)
        {
            var outputs = new OutputSlotSnapshot[OutputSlotCount];
            if (nodeBase.childUID == null || nodeBase.childUID.Length < OutputSlotCount
                || nodeBase.endConditionType == null || nodeBase.endConditionType.Length < OutputSlotCount
                || nodeBase.endConditionCount == null || nodeBase.endConditionCount.Length < OutputSlotCount
                || nodeBase.fadeType == null || nodeBase.fadeType.Length < OutputSlotCount
                || nodeBase.fadeTime == null || nodeBase.fadeTime.Length < OutputSlotCount)
            {
                warnings.Add(new SnapshotWarning(
                    "node-read-failed",
                    "节点输出槽数组不完整，缺失位置已用默认值保留槽位。",
                    nodeUid));
            }
            for (var i = 0; i < OutputSlotCount; i++)
            {
                var targetUid = ReadArrayValue(nodeBase.childUID, i, string.Empty);
                outputs[i] = new OutputSlotSnapshot
                {
                    slot = i,
                    targetUid = targetUid ?? string.Empty,
                    conditionType = ReadArrayValue(nodeBase.endConditionType, i, default(EndConditionType)).ToString(),
                    conditionCount = ReadArrayValue(nodeBase.endConditionCount, i, 0),
                    fadeType = ReadArrayValue(nodeBase.fadeType, i, 0),
                    fadeTime = ReadArrayValue(nodeBase.fadeTime, i, 0f)
                };

                if (!string.IsNullOrEmpty(targetUid)
                    && (allNodes == null || !allNodes.ContainsKey(targetUid)))
                {
                    warnings.Add(new SnapshotWarning(
                        "missing-node-target",
                        "输出槽连接目标 UID 不存在：" + targetUid,
                        nodeUid + "/slot:" + i));
                }
            }
            return outputs;
        }

        private PartSnapshot BuildPart(
            NodeSnapshot node,
            BasePart part,
            ISet<string> knownPartNames,
            List<SnapshotWarning> warnings,
            List<string> crossPartReferences,
            ref int cutCount,
            ref int textBoxCount)
        {
            var result = new PartSnapshot
            {
                uid = string.IsNullOrEmpty(part.uuId) ? node.uid : part.uuId,
                name = node.name,
                kind = part.kind,
                type = GetPartType(part.kind),
                cuts = new CutSnapshot[0]
            };

            var advPart = part as HEdit.ADVPart;
            if (part.kind != PartKindAdv || advPart == null)
            {
                if (part.kind != PartKindH && part.kind != PartKindAdv)
                {
                    warnings.Add(new SnapshotWarning(
                        "unknown-part-kind",
                        "发现未识别的 Part 类型：" + part.kind,
                        node.uid));
                }
                return result;
            }

            if (advPart.cuts == null)
            {
                warnings.Add(new SnapshotWarning("part-read-failed", "ADV Part 的 cuts 为空。", node.uid));
                return result;
            }

            var cuts = new List<CutSnapshot>();
            for (var cutIndex = 0; cutIndex < advPart.cuts.Count; cutIndex++)
            {
                var cut = advPart.cuts[cutIndex];
                if (cut == null)
                {
                    warnings.Add(new SnapshotWarning(
                        "cut-read-failed",
                        "CUT 对象为空。",
                        node.uid + "/cut:" + cutIndex));
                    continue;
                }

                var cutSnapshot = new CutSnapshot
                {
                    sourceIndex = cutIndex,
                    exportOrder = cutIndex,
                    endCut = cut.endCut,
                    optionCount = cut.optionCount,
                    textBoxes = new TextBoxSnapshot[0]
                };
                cutCount++;

                if (cut.speechBubbles == null)
                {
                    warnings.Add(new SnapshotWarning(
                        "cut-read-failed",
                        "CUT 的 speechBubbles 为空。",
                        node.uid + "/cut:" + cutIndex));
                }
                else
                {
                    var indexedBubbles = new List<IndexedBubble>();
                    for (var bubbleIndex = 0; bubbleIndex < cut.speechBubbles.Count; bubbleIndex++)
                    {
                        var bubble = cut.speechBubbles[bubbleIndex];
                        if (bubble == null)
                        {
                            warnings.Add(new SnapshotWarning(
                                "textbox-read-failed",
                                "对白框对象为空。",
                                node.uid + "/cut:" + cutIndex + "/textBox:" + bubbleIndex));
                            continue;
                        }
                        indexedBubbles.Add(new IndexedBubble(bubbleIndex, bubble));
                    }

                    indexedBubbles.Sort((left, right) =>
                    {
                        var order = left.Bubble.sortOrder.CompareTo(right.Bubble.sortOrder);
                        return order != 0
                            ? order
                            : left.SourceIndex.CompareTo(right.SourceIndex);
                    });

                    var textBoxes = new List<TextBoxSnapshot>();
                    for (var exportOrder = 0; exportOrder < indexedBubbles.Count; exportOrder++)
                    {
                        var indexed = indexedBubbles[exportOrder];
                        var bubble = indexed.Bubble;
                        var textBox = new TextBoxSnapshot
                        {
                            sourceIndex = indexed.SourceIndex,
                            exportOrder = exportOrder,
                            sortOrder = bubble.sortOrder,
                            id = bubble.id,
                            option = bubble.option,
                            layouts = new TextLayoutSnapshot[2]
                        };
                        BuildLayouts(
                            node,
                            cutIndex,
                            indexed.SourceIndex,
                            bubble,
                            knownPartNames,
                            advPart.cuts.Count,
                            warnings,
                            crossPartReferences,
                            textBox.layouts,
                            ref textBoxCount);
                        textBoxes.Add(textBox);
                    }
                    cutSnapshot.textBoxes = textBoxes.ToArray();
                }
                cuts.Add(cutSnapshot);
            }

            result.cuts = cuts.ToArray();
            return result;
        }

        private void BuildLayouts(
            NodeSnapshot node,
            int cutIndex,
            int sourceIndex,
            HEdit.ADVPart.SpeechBubbles bubble,
            ISet<string> knownPartNames,
            int cutCount,
            List<SnapshotWarning> warnings,
            List<string> crossPartReferences,
            TextLayoutSnapshot[] layouts,
            ref int textBoxCount)
        {
            textBoxCount++;
            if (bubble.textLayouts == null)
            {
                warnings.Add(new SnapshotWarning(
                    "text-layout-read-failed",
                    "对白框 textLayouts 为空。",
                    node.uid + "/cut:" + cutIndex + "/textBox:" + sourceIndex));
                for (var layoutIndex = 0; layoutIndex < 2; layoutIndex++)
                {
                    layouts[layoutIndex] = CreateUnavailableLayout(
                        layoutIndex,
                        node.uid + "/cut:" + cutIndex + "/textBox:" + sourceIndex
                        + "/layout:" + layoutIndex);
                }
                return;
            }

            for (var layoutIndex = 0; layoutIndex < 2; layoutIndex++)
            {
                var source = node.uid + "/cut:" + cutIndex + "/textBox:" + sourceIndex
                             + "/layout:" + layoutIndex;
                if (layoutIndex >= bubble.textLayouts.Length || bubble.textLayouts[layoutIndex] == null)
                {
                    layouts[layoutIndex] = CreateUnavailableLayout(layoutIndex, source);
                    warnings.Add(new SnapshotWarning("text-layout-read-failed", "文本布局不可用。", source));
                    continue;
                }

                var rawText = bubble.textLayouts[layoutIndex].msg ?? string.Empty;
                var layout = scanner.Scan(rawText, externalScriptRoot, knownPartNames, cutCount);
                layout.layoutIndex = layoutIndex;
                layouts[layoutIndex] = layout;
                foreach (var warning in layout.warnings)
                {
                    warnings.Add(new SnapshotWarning(
                        warning.code,
                        warning.message,
                        source + "/" + warning.source));
                }
                CollectCrossPartReferences(node.uid, cutIndex, sourceIndex, layout, crossPartReferences);
            }
        }

        private static TextLayoutSnapshot CreateUnavailableLayout(int layoutIndex, string source)
        {
            return new TextLayoutSnapshot
            {
                layoutIndex = layoutIndex,
                rawText = string.Empty,
                containsScript = false,
                scriptBlocks = new ScriptBlockSnapshot[0],
                variableReferences = new VariableReferenceSnapshot[0],
                warnings = new[]
                {
                    new SnapshotWarning("text-layout-read-failed", "文本布局不可用。", source)
                }
            };
        }

        private static void CollectCrossPartReferences(
            string uid,
            int cutIndex,
            int textBoxIndex,
            TextLayoutSnapshot layout,
            List<string> references)
        {
            if (layout?.scriptBlocks == null)
                return;
            foreach (var block in layout.scriptBlocks)
            {
                if (block?.references == null)
                    continue;
                foreach (var reference in block.references)
                {
                    if (reference == null || string.IsNullOrEmpty(reference.name))
                        continue;
                    if (reference.kind != "flow")
                        continue;
                    references.Add(
                        uid + "/cut:" + cutIndex + "/textBox:" + textBoxIndex
                        + " -> " + reference.name + " [" + reference.targetStatus + "]");
                }
            }
        }

        private static BasePart FindPart(HEditData data, string key, string uid)
        {
            if (data?.nodes == null)
                return null;
            if (!string.IsNullOrEmpty(key) && data.nodes.TryGetValue(key, out var part))
                return part;
            if (!string.IsNullOrEmpty(uid) && data.nodes.TryGetValue(uid, out part))
                return part;
            foreach (var pair in data.nodes)
            {
                if (pair.Value != null && pair.Value.uuId == uid)
                    return pair.Value;
            }
            return null;
        }

        private static HashSet<string> CollectKnownPartNames(
            Dictionary<string, NodeUI> nodes,
            HEditData data)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (nodes != null)
            {
                foreach (var pair in nodes)
                {
                    var nodeBase = pair.Value?.nodeBase;
                    if (nodeBase != null && !string.IsNullOrEmpty(nodeBase.name)
                        && nodeBase.uid != "node_start" && nodeBase.uid != "node_end")
                    {
                        names.Add(nodeBase.name);
                    }
                }
            }
            if (data?.nodes != null)
            {
                foreach (var pair in data.nodes)
                {
                    if (pair.Value != null && !string.IsNullOrEmpty(pair.Value.name))
                        names.Add(pair.Value.name);
                }
            }
            return names;
        }

        private static string GetPartType(int kind)
        {
            if (kind == PartKindH)
                return "H";
            if (kind == PartKindAdv)
                return "ADV";
            return "Unknown";
        }

        private static T ReadArrayValue<T>(T[] values, int index, T fallback)
        {
            return values != null && index >= 0 && index < values.Length ? values[index] : fallback;
        }

        private sealed class IndexedBubble
        {
            public IndexedBubble(int sourceIndex, HEdit.ADVPart.SpeechBubbles bubble)
            {
                SourceIndex = sourceIndex;
                Bubble = bubble;
            }

            public int SourceIndex { get; }
            public HEdit.ADVPart.SpeechBubbles Bubble { get; }
        }
    }
}
