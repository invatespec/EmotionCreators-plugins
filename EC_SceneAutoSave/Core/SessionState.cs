using System;
using System.Collections.Generic;
using EC_SceneAutoSave.Storage;

namespace EC_SceneAutoSave.Core
{
    // 一个基准文件对应的自动保存状态。哈希都是“当前已记录内容”的哈希：基准里的那份或最近一次写入的那份。
    internal sealed class SessionState
    {
        public BaseFingerprint Base;
        public string SceneKey;
        public string Dir;
        public int Seq;
        public long CreatedTicksUtc;
        // 本次读档后已应用过会话；场景已不是纯基准，不能再应用第二个。
        public bool Applied;

        public readonly Dictionary<string, string> PartHash = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> PartBlob = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<int, string> CharaBlob = new Dictionary<int, string>();

        public string GraphHash;
        public string MapsHash;
        public string InfoHash;
        public string GraphBlob;
        public string MapsBlob;
        public string InfoBlob;

        // 读档或原生保存时的内容哈希，用来判断“读档后是否有新编辑”。
        public string LoadGraphHash;
        public string LoadMapsHash;
        public string LoadInfoHash;

        public SessionManifest Last;

        public DateTime LastCommitLocal;
        public int LastPartCount;
        public long LastBytes;
        public long LastMillis;
        public string LastError;
    }
}
