namespace EC_SceneAudit.Export
{
    internal static partial class JsonSnapshotWriter
    {
        public static string SerializeBundle(BundleManifest manifest)
        {
            var writer = new JsonWriter();
            writer.StartObject();
            writer.Property("format", () => writer.String("ec-scene-review-bundle"));
            writer.Property("version", () => writer.Integer(1));
            writer.Property("bundleId", () => writer.String(manifest.bundleId));
            writer.Property("scene", () => WriteSceneMetadata(writer, manifest.scene));
            writer.Property("files", () => WriteArray(writer, manifest.files.ToArray(), WriteBundleFile));
            writer.Property("dependencies", () => WriteArray(writer, manifest.collection.dependencies.ToArray(), WriteDependency));
            writer.Property("limitations", () => WriteArray(writer, manifest.collection.limitations.ToArray(), WriteLimitation));
            writer.EndObject();
            return writer.ToString();
        }

        private static void WriteBundleFile(JsonWriter writer, BundleFile file)
        {
            writer.StartObject();
            writer.Property("fileId", () => writer.String(file.fileId));
            writer.Property("role", () => writer.String(file.role));
            writer.Property("path", () => writer.String(file.path));
            writer.Property("sha256", () => writer.String(file.sha256));
            writer.Property("byteLength", () => writer.Integer(file.byteLength));
            if (file.role == "dependency")
            {
                writer.Property("sourcePath", () => writer.String(file.sourcePath));
                writer.Property("capturedAt", () => writer.String(file.capturedAt));
                writer.Property("encoding", () => writer.String(file.encoding));
                writer.Property("parseStatus", () => writer.String(file.parseStatus));
            }
            writer.EndObject();
        }

        private static void WriteDependency(JsonWriter writer, ScriptDependency dependency)
        {
            writer.StartObject();
            writer.Property("from", () => WriteEvidenceReference(writer, dependency.from));
            writer.Property("requestedPath", () => writer.String(dependency.requestedPath));
            writer.Property("targetFileId", () => writer.String(dependency.targetFileId));
            writer.Property("status", () => writer.String(dependency.status));
            writer.Property("reason", () => writer.String(dependency.reason));
            writer.Property("context", () => WriteStringArray(writer, dependency.context));
            writer.EndObject();
        }

        private static void WriteLimitation(JsonWriter writer, BundleLimitation limitation)
        {
            writer.StartObject();
            writer.Property("code", () => writer.String(limitation.code));
            writer.Property("source", () => WriteEvidenceReference(writer, limitation.source));
            writer.Property("message", () => writer.String(limitation.message));
            writer.EndObject();
        }

        private static void WriteEvidenceReference(JsonWriter writer, EvidenceReference reference)
        {
            writer.StartObject();
            writer.Property("fileId", () => writer.String(reference.fileId));
            writer.Property("kind", () => writer.String(reference.kind));
            if (reference.kind == "json-pointer")
                writer.Property("pointer", () => writer.String(reference.pointer));
            else
            {
                writer.Property("startLine", () => writer.Integer(reference.startLine));
                writer.Property("endLine", () => writer.Integer(reference.endLine));
            }
            writer.EndObject();
        }
    }
}
