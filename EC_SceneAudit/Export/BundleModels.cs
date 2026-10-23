using System.Collections.Generic;

namespace EC_SceneAudit.Export
{
    public sealed class EvidenceReference
    {
        public string fileId;
        public string kind = "json-pointer";
        public string pointer;
        public int startLine;
        public int endLine;
    }

    public sealed class BundleFile
    {
        public string fileId;
        public string role;
        public string path;
        public string sha256;
        public int byteLength;
        public string sourcePath;
        public string capturedAt;
        public string encoding;
        public string parseStatus;
    }

    public sealed class ScriptDependency
    {
        public EvidenceReference from;
        public string requestedPath;
        public string targetFileId;
        public string status;
        public string reason;
        public string[] context;
    }

    public sealed class BundleLimitation
    {
        public string code;
        public EvidenceReference source;
        public string message;
    }

    public sealed class CapturedScript
    {
        public BundleFile file;
        public byte[] bytes;
    }

    public sealed class DependencyCollection
    {
        public readonly List<CapturedScript> scripts = new List<CapturedScript>();
        public readonly List<ScriptDependency> dependencies = new List<ScriptDependency>();
        public readonly List<BundleLimitation> limitations = new List<BundleLimitation>();
    }

    public sealed class BundleManifest
    {
        public string bundleId;
        public SceneMetadata scene;
        public readonly List<BundleFile> files = new List<BundleFile>();
        public DependencyCollection collection;
    }

    public sealed class CollectionLimits
    {
        public int fileBytes = 2 * 1024 * 1024;
        public int totalBytes = 16 * 1024 * 1024;
        public int files = 256;
        public int depth = 32;
    }
}
