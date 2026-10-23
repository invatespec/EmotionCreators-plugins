using System;

namespace EC_SceneAudit.Export
{
    [Serializable]
    public sealed class SceneSnapshot
    {
        public string schemaVersion;
        public SceneMetadata scene;
        public SceneGeometrySnapshot geometry;
        public NodeSnapshot[] nodes;
        public string[] crossPartReferences;
        public SnapshotWarning[] warnings;
    }

    [Serializable]
    public sealed class SceneGeometrySnapshot
    {
        public int version;
        public string coordinateSpace;
        public GeometryCanvasSnapshot canvas;
        public GeometryNodeSnapshot[] nodes;
    }

    [Serializable]
    public sealed class GeometryCanvasSnapshot
    {
        public float width;
        public float height;
    }

    [Serializable]
    public sealed class GeometryNodeSnapshot
    {
        public string uid;
        public string status;
        public GeometryFrameSnapshot frame;
        public Vector2Snapshot positionOffset;
        public GeometryPointSnapshot input;
        public GeometryOutputPointSnapshot[] outputs;
    }

    [Serializable]
    public sealed class GeometryFrameSnapshot
    {
        public float x;
        public float y;
        public float width;
        public float height;
    }

    [Serializable]
    public sealed class GeometryPointSnapshot
    {
        public bool enabled;
        public float x;
        public float y;
    }

    [Serializable]
    public sealed class GeometryOutputPointSnapshot
    {
        public int slot;
        public bool enabled;
        public float x;
        public float y;
    }

    [Serializable]
    public sealed class SceneMetadata
    {
        public string title;
        public string exportedAt;
        public string gameVersion;
        public string[] plugins;
    }

    [Serializable]
    public sealed class NodeSnapshot
    {
        public string uid;
        public string name;
        public string kind;
        public bool isSystemNode;
        public Vector2Snapshot position;
        public OutputSlotSnapshot[] outputs;
        public PartSnapshot part;
    }

    [Serializable]
    public sealed class Vector2Snapshot
    {
        public float x;
        public float y;
    }

    [Serializable]
    public sealed class OutputSlotSnapshot
    {
        public int slot;
        public string targetUid;
        public string conditionType;
        public int conditionCount;
        public int fadeType;
        public float fadeTime;
    }

    [Serializable]
    public sealed class PartSnapshot
    {
        public string uid;
        public string name;
        public int kind;
        public string type;
        public CutSnapshot[] cuts;
    }

    [Serializable]
    public sealed class CutSnapshot
    {
        public int sourceIndex;
        public int exportOrder;
        public bool endCut;
        public int optionCount;
        public TextBoxSnapshot[] textBoxes;
    }

    [Serializable]
    public sealed class TextBoxSnapshot
    {
        public int sourceIndex;
        public int exportOrder;
        public int sortOrder;
        public int id;
        public bool option;
        public TextLayoutSnapshot[] layouts;
    }

    [Serializable]
    public sealed class TextLayoutSnapshot
    {
        public int layoutIndex;
        public string rawText;
        public bool containsScript;
        public ScriptBlockSnapshot[] scriptBlocks;
        public VariableReferenceSnapshot[] variableReferences;
        public SnapshotWarning[] warnings;
    }

    [Serializable]
    public sealed class ScriptBlockSnapshot
    {
        public string raw;
        public int sourceOffset;
        public string[] commands;
        public AttributeSnapshot[] attributes;
        public ScriptReferenceSnapshot[] references;
    }

    [Serializable]
    public sealed class ScriptReferenceSnapshot
    {
        public string kind;
        public string name;
        public AttributeSnapshot[] attributes;
        public int sourceOffset;
        public string targetStatus;
    }

    [Serializable]
    public sealed class AttributeSnapshot
    {
        public string key;
        public string value;
    }

    [Serializable]
    public sealed class VariableReferenceSnapshot
    {
        public string raw;
        public string name;
        public string category;
        public int sourceOffset;
    }

    [Serializable]
    public sealed class SnapshotWarning
    {
        public string code;
        public string message;
        public string source;

        public SnapshotWarning()
        {
        }

        public SnapshotWarning(string code, string message, string source = null)
        {
            this.code = code;
            this.message = message;
            this.source = source ?? string.Empty;
        }
    }
}
