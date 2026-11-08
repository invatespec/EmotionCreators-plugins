using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace EC_SceneAutoSave.Storage
{
    // 会话清单：数据文件名为空表示沿用基准文件里的那份。
    [XmlRoot("SceneAutoSaveSession")]
    public sealed class SessionManifest
    {
        public const int CurrentFormat = 1;

        [XmlAttribute] public int Format = CurrentFormat;
        [XmlAttribute] public string GameVersion;
        [XmlAttribute] public string BasePath;
        [XmlAttribute] public long BaseLength;
        [XmlAttribute] public long BaseWriteTicksUtc;
        [XmlAttribute] public long CreatedTicksUtc;
        [XmlAttribute] public long UpdatedTicksUtc;
        [XmlAttribute] public int Seq;
        [XmlAttribute] public string GraphBlob;
        [XmlAttribute] public string MapsBlob;
        [XmlAttribute] public string InfoBlob;

        [XmlArray("Parts"), XmlArrayItem("Part")]
        public List<SessionPartEntry> Parts = new List<SessionPartEntry>();

        [XmlArray("Charas"), XmlArrayItem("Chara")]
        public List<SessionCharaEntry> Charas = new List<SessionCharaEntry>();

        public IEnumerable<string> ReferencedBlobs()
        {
            if (!string.IsNullOrEmpty(GraphBlob)) yield return GraphBlob;
            if (!string.IsNullOrEmpty(MapsBlob)) yield return MapsBlob;
            if (!string.IsNullOrEmpty(InfoBlob)) yield return InfoBlob;
            foreach (var part in Parts)
                if (!string.IsNullOrEmpty(part.Blob)) yield return part.Blob;
            foreach (var chara in Charas)
                if (!string.IsNullOrEmpty(chara.Blob)) yield return chara.Blob;
        }

        public int RecordedPartCount => Parts.Count(p => !string.IsNullOrEmpty(p.Blob));

        // 只比较内容引用，不比较时间与序号；用于判断本次是否需要提交新清单。
        public bool SameContent(SessionManifest other)
        {
            if (other == null)
                return false;
            if (GraphBlob != other.GraphBlob || MapsBlob != other.MapsBlob || InfoBlob != other.InfoBlob)
                return false;
            if (Parts.Count != other.Parts.Count || Charas.Count != other.Charas.Count)
                return false;
            for (var i = 0; i < Parts.Count; i++)
            {
                if (!Parts[i].SameAs(other.Parts[i]))
                    return false;
            }
            for (var i = 0; i < Charas.Count; i++)
            {
                if (Charas[i].Index != other.Charas[i].Index || Charas[i].Blob != other.Charas[i].Blob)
                    return false;
            }
            return true;
        }

        public void Validate()
        {
            if (Format != CurrentFormat)
                throw new InvalidDataException("不支持的清单格式版本：" + Format);
            if (string.IsNullOrEmpty(BasePath) || string.IsNullOrEmpty(GameVersion))
                throw new InvalidDataException("清单缺少基准路径或游戏版本。");
            var uids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in Parts)
            {
                if (part == null || string.IsNullOrEmpty(part.Uid) || !uids.Add(part.Uid))
                    throw new InvalidDataException("Part uid 为空或重复。");
                if (part.Kind != 0 && part.Kind != 1)
                    throw new InvalidDataException("Part 类型无效：" + part.Uid);
            }
            var indices = new HashSet<int>();
            foreach (var chara in Charas)
            {
                if (chara == null || chara.Index < 0 || !indices.Add(chara.Index) || string.IsNullOrEmpty(chara.Blob))
                    throw new InvalidDataException("角色序号无效或重复。");
            }
            foreach (var blob in ReferencedBlobs())
            {
                if (!IsSafeBlobName(blob))
                    throw new InvalidDataException("数据文件名无效：" + blob);
            }
        }

        // 清单可能被手动改过，数据文件名只允许落在会话目录内。
        public static bool IsSafeBlobName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains("..");
        }
    }

    public sealed class SessionPartEntry
    {
        [XmlAttribute] public string Uid;
        [XmlAttribute] public int Kind;
        [XmlAttribute] public int MapId;
        [XmlAttribute] public string Blob;

        public bool SameAs(SessionPartEntry other)
        {
            return other != null && Uid == other.Uid && Kind == other.Kind && MapId == other.MapId && Blob == other.Blob;
        }
    }

    public sealed class SessionCharaEntry
    {
        [XmlAttribute] public int Index;
        [XmlAttribute] public string Blob;
    }
}
