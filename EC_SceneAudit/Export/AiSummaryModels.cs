using System;

namespace EC_SceneAudit.Export
{
    [Serializable]
    internal sealed class AiSummarySnapshot
    {
        public string schemaVersion;
        public string sourceEvidenceFile;
        public AiSceneSummary scene;
        public AiSceneStatistics statistics;
        public AiPartIndex[] parts;
        public AiScriptSummary[] scripts;
        public AiFinding[] findings;
    }

    [Serializable]
    internal sealed class AiSceneSummary
    {
        public string title;
        public string exportedAt;
    }

    [Serializable]
    internal sealed class AiSceneStatistics
    {
        public int nodes;
        public int parts;
        public int cuts;
        public int textBoxes;
        public int scriptBlocks;
        public int warnings;
    }

    [Serializable]
    internal sealed class AiPartIndex
    {
        public string nodeUid;
        public string uid;
        public string name;
        public string type;
        public int cutCount;
        public int textBoxCount;
        public string rawRef;
    }

    [Serializable]
    internal sealed class AiScriptSummary
    {
        public string rawRef;
        public string parseStatus;
        public string[] commands;
        public AiVariable[] declarations;
        public AiVariable[] reads;
        public AiVariable[] writes;
        public AiLogicStep[] steps;
        public AiEventTrigger[] events;
        public AiFinding[] findings;
    }

    [Serializable]
    internal sealed class AiVariable
    {
        public string name;
        public string category;
        public string operation;
        public string expression;
        public int sourceOffset;
        public string rawRef;
    }

    [Serializable]
    internal sealed class AiLogicStep
    {
        public string kind;
        public string name;
        public AttributeSnapshot[] attributes;
        public string targetStatus;
        public int sourceOffset;
        public string rawRef;
        public AiLogicStep[] children;
    }

    [Serializable]
    internal sealed class AiEventTrigger
    {
        public string kind;
        public AttributeSnapshot[] attributes;
        public string targetStatus;
        public int sourceOffset;
        public string rawRef;
    }

    [Serializable]
    internal sealed class AiFinding
    {
        public string code;
        public string message;
        public string rawRef;

        public AiFinding()
        {
        }

        public AiFinding(string code, string message, string rawRef)
        {
            this.code = code;
            this.message = message;
            this.rawRef = rawRef;
        }
    }
}
