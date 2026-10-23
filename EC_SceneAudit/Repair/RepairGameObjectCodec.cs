using System;
using System.IO;
using HEdit;
using YS_Node;

namespace EC_SceneAudit.Repair
{
    // 独立深拷贝用游戏自身序列化：ADVPart 拷贝构造会共用部分嵌套引用，
    // Save/Load 内存往返才能得到与来源完全独立的副本。
    // 读回版本必须 >= 1.0.2.2：Part.Save 无条件写全部字段，而 Load 按版本跳过字段，
    // 版本过低会造成读写错位（丢 TextLayout.range、Cut.fade 等）。
    internal static class RepairPartCodec
    {
        private static readonly Version MinimumFormatVersion = new Version(1, 0, 2, 2);

        public static Version FormatVersion(HEditData data)
        {
            var version = data != null ? data.version : null;
            return version != null && version.CompareTo(MinimumFormatVersion) >= 0
                ? version
                : MinimumFormatVersion;
        }

        // 限定 HEdit.：命名空间 ADVPart 与类型 HEdit.ADVPart 同名，限定名会先命中命名空间。
        public static bool TrySerialize(HEdit.ADVPart part, out byte[] data, out string error)
        {
            data = null;
            error = null;
            if (part == null)
            {
                error = "Part 为空。";
                return false;
            }
            try
            {
                using (var stream = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(stream))
                    {
                        part.Save(writer);
                        writer.Flush();
                    }
                    data = stream.ToArray();
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static HEdit.ADVPart Deserialize(byte[] data, Version version, out string error)
        {
            error = null;
            try
            {
                using (var stream = new MemoryStream(data ?? new byte[0]))
                using (var reader = new BinaryReader(stream))
                {
                    var part = new HEdit.ADVPart(0);
                    part.Load(reader, version);
                    return part;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }
    }

    // 目标 NodeBase 的快照与恢复：Load 到临时对象后 Copy 进既有实例，
    // 保住 NodeUI 持有的 NodeBase 引用，不重建节点 UI。
    internal static class RepairNodeCodec
    {
        public static bool TrySerialize(NodeBase node, out byte[] data, out string error)
        {
            data = null;
            error = null;
            if (node == null)
            {
                error = "节点为空。";
                return false;
            }
            try
            {
                using (var stream = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(stream))
                    {
                        node.Save(writer);
                        writer.Flush();
                    }
                    data = stream.ToArray();
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryLoadInto(NodeBase target, byte[] data, Version version, out string error)
        {
            error = null;
            if (target == null)
            {
                error = "目标节点为空。";
                return false;
            }
            try
            {
                var temp = new NodeBase();
                using (var stream = new MemoryStream(data ?? new byte[0]))
                using (var reader = new BinaryReader(stream))
                    temp.Load(reader, version);
                target.Copy(temp);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
