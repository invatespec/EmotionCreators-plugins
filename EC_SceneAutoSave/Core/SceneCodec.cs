using System;
using System.Collections.Generic;
using System.IO;
using HEdit;
using Map;
using YS_Node;

namespace EC_SceneAutoSave.Core
{
    // 全部使用游戏自身的序列化，与原生场景保存的字节格式一致。
    // 读回版本取当前写出版本：Save 无条件写全部字段，低版本 Load 会跳字段造成错位。
    internal static class SceneCodec
    {
        private static readonly Version MinimumFormatVersion = new Version(1, 0, 2, 2);

        // NodeControl.Save 开头是画布平移 x/y 与缩放，三个 float；判断是否编辑时跳过。
        public const int GraphViewBytes = 12;

        public static Version FormatVersion(HEditData data)
        {
            var version = data?.version;
            return version != null && version.CompareTo(MinimumFormatVersion) >= 0 ? version : MinimumFormatVersion;
        }

        public static byte[] SavePart(BasePart part) => Write(w => part.Save(w));

        public static byte[] SaveGraph(NodeControl control) => control.Save();

        public static byte[] SaveInfo(HEditData.InfoData info) => Write(w => info.Save(w, false));

        public static byte[] SaveChara(ChaControl chara) => Write(w => chara.chaFile.SaveCharaFile(w, true));

        // maps[0] 是固定的“无地图”，原生保存同样从 1 开始。每张地图带长度前缀，便于逐张比较。
        public static byte[] SaveMaps(List<MapInfo> maps)
        {
            return Write(w =>
            {
                w.Write(maps.Count - 1);
                for (var i = 1; i < maps.Count; i++)
                {
                    var bytes = Write(m => maps[i].Save(m, false));
                    w.Write(bytes.Length);
                    w.Write(bytes);
                }
            });
        }

        public static List<byte[]> SplitMaps(byte[] blob)
        {
            var result = new List<byte[]>();
            using (var reader = new BinaryReader(new MemoryStream(blob)))
            {
                var count = reader.ReadInt32();
                if (count < 0 || count > 10000)
                    throw new InvalidDataException("地图数量无效：" + count);
                for (var i = 0; i < count; i++)
                {
                    var length = reader.ReadInt32();
                    var bytes = length < 0 ? null : reader.ReadBytes(length);
                    if (bytes == null || bytes.Length != length)
                        throw new InvalidDataException("地图数据长度无效。");
                    result.Add(bytes);
                }
                RequireEnd(reader);
            }
            return result;
        }

        public static List<byte[]> SplitCurrentMaps(List<MapInfo> maps) => SplitMaps(SaveMaps(maps));

        public static BasePart LoadPart(byte[] blob, int kind, Version version)
        {
            BasePart part = kind == 0 ? new HPart() : (BasePart)new HEdit.ADVPart(0);
            Read(blob, r => part.Load(r, version));
            return part;
        }

        public static MapInfo LoadMap(byte[] blob)
        {
            var map = new MapInfo();
            map.Init();
            Read(blob, r => map.Load(r, false));
            return map;
        }

        public static void LoadMapInto(MapInfo target, byte[] blob)
        {
            target.Init();
            Read(blob, r => target.Load(r, false));
        }

        public static HEditData.InfoData LoadInfo(byte[] blob, Version version)
        {
            var info = new HEditData.InfoData();
            Read(blob, r => info.Load(r, version));
            return info;
        }

        public static void LoadInfoInto(HEditData.InfoData target, byte[] blob, Version version)
        {
            Read(blob, r => target.Load(r, version));
        }

        // 不碰 NodeControl 实例，只确认节点图字节能完整解析。
        public static void ValidateGraph(byte[] blob, Version version)
        {
            Read(blob, r =>
            {
                r.ReadSingle();
                r.ReadSingle();
                r.ReadSingle();
                var count = r.ReadInt32();
                if (count < 0 || count > 100000)
                    throw new InvalidDataException("节点数量无效：" + count);
                for (var i = 0; i < count; i++)
                    new NodeBase().Load(r, version);
            });
        }

        // 角色卡末尾可能还有扩展数据，不要求读到末尾，只要求游戏能读出。
        public static void ValidateChara(byte[] blob)
        {
            var file = new ChaFileControl();
            using (var reader = new BinaryReader(new MemoryStream(blob ?? new byte[0])))
            {
                if (!file.LoadCharaFile(reader, false, true))
                    throw new InvalidDataException("角色数据无法读取。");
            }
        }

        private static byte[] Write(Action<BinaryWriter> body)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                {
                    body(writer);
                    writer.Flush();
                }
                return stream.ToArray();
            }
        }

        // 读完后必须恰好到达末尾，多出或缺少字节都说明格式错位。
        private static void Read(byte[] blob, Action<BinaryReader> body)
        {
            using (var reader = new BinaryReader(new MemoryStream(blob ?? new byte[0])))
            {
                body(reader);
                RequireEnd(reader);
            }
        }

        private static void RequireEnd(BinaryReader reader)
        {
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("数据长度与格式不一致。");
        }
    }
}
