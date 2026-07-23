using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using HEdit;
using YS_Node;

namespace EC_NodePlayabilityDiag
{
    // 动机：原版 PlayCheck 失败只弹「不可玩」UI，不告诉是哪个 Part。
    // 只挂用户主动路径 NodeSettingCanvas.PlayCheck，避免保存元数据检查刷日志。
    // 只读诊断，不改 playable / 弹窗文案。
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    public sealed class NodePlayabilityDiagPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_NodePlayabilityDiag";
        public const string PluginName = "EC Node Playability Diag";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            var harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(Hooks));
            Log.LogInfo($"{PluginName} v{Version} loaded.");
        }

        private static class Hooks
        {
            // 仅用户主动检查/试玩；__result==false 时才诊断。
            [HarmonyPostfix]
            [HarmonyPatch(typeof(NodeSettingCanvas), "PlayCheck")]
            private static void PlayCheckPostfix(bool __result)
            {
                if (__result)
                    return;

                try
                {
                    Diagnose();
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"diagnose failed: {ex.Message}");
                }
            }
        }

        private static void Diagnose()
        {
            var nodeControl = Singleton<HEditGlobal>.Instance?.nodeControl;
            if (nodeControl == null || nodeControl.dictNode == null)
            {
                Log.LogMessage("不可玩：nodeControl 不可用，跳过详细诊断。");
                return;
            }

            // 与 PlayCheck 相同参数重跑，拿到 CheckInfo 做分类（不缓存原结果，原版未暴露）。
            var checkInfo = nodeControl.CheckPlayableNode(true);
            if (checkInfo == null)
            {
                Log.LogMessage("不可玩：CheckPlayableNode 返回 null（start 缺失 / start 直连 end 等）。");
                return;
            }

            if (!checkInfo.playable)
            {
                LogFlagsAndNamedNodes(nodeControl, checkInfo);
            }

            // playable 仍可能因 HPart 无角色被 PlayCheck 否决；图失败时也一并列出。
            LogHPartNoChara(checkInfo);
        }

        private static void LogFlagsAndNamedNodes(NodeControl nodeControl, NodeCheck.CheckInfo info)
        {
            if (!info.canEnd)
                Log.LogMessage("无法到达终点node（!canEnd）。");

            if (info.deadEnd)
            {
                var names = CollectDeadEndNames(nodeControl, info);
                LogNamedList("死胡同（可达且无有效输出）", names);
            }

            if (info.disconnectedOutput)
            {
                var names = CollectDisconnectedOutputNames(nodeControl, info);
                LogNamedList("输出未完全连接", names);
            }

            if (info.lstUnreachableWithoutIsolated != null && info.lstUnreachableWithoutIsolated.Count > 0)
            {
                var names = info.lstUnreachableWithoutIsolated
                    .Select(GetDisplayName)
                    .Where(n => n != null)
                    .Distinct()
                    .ToList();
                LogNamedList("可达但到不了终点node", names);
            }
        }

        // 原版只标 deadEnd 布尔；点名：从 start 前向可达、非 end、且 childUID 全空。
        private static List<string> CollectDeadEndNames(NodeControl nodeControl, NodeCheck.CheckInfo info)
        {
            var reachable = CollectReachableFromStart(nodeControl, info);
            var names = new List<string>();
            foreach (var node in reachable)
            {
                if (node?.nodeBase == null)
                    continue;
                var uid = node.nodeBase.uid;
                if (uid == "node_start" || uid == "node_end")
                    continue;
                if (!HasAnyChild(node.nodeBase))
                {
                    var name = GetDisplayName(node);
                    if (name != null)
                        names.Add(name);
                }
            }
            return names.Distinct().ToList();
        }

        // 原版只标 disconnectedOutput 布尔；点名：可达且 !IsConnectedConditionAll()。
        private static List<string> CollectDisconnectedOutputNames(NodeControl nodeControl, NodeCheck.CheckInfo info)
        {
            var reachable = CollectReachableFromStart(nodeControl, info);
            var names = new List<string>();
            foreach (var node in reachable)
            {
                if (node?.nodeBase == null)
                    continue;
                var uid = node.nodeBase.uid;
                if (uid == "node_start" || uid == "node_end")
                    continue;
                try
                {
                    if (!node.IsConnectedConditionAll())
                    {
                        var name = GetDisplayName(node);
                        if (name != null)
                            names.Add(name);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogDebug($"IsConnectedConditionAll failed for {uid}: {ex.Message}");
                }
            }
            return names.Distinct().ToList();
        }

        // 优先用 lstRoot 聚合；为空时回退 start 前向 BFS（兼容 Alternative 未填 lstRoot）。
        private static HashSet<NodeUI> CollectReachableFromStart(NodeControl nodeControl, NodeCheck.CheckInfo info)
        {
            var set = new HashSet<NodeUI>();
            if (info.lstRoot != null && info.lstRoot.Count > 0)
            {
                foreach (var path in info.lstRoot)
                {
                    if (path == null)
                        continue;
                    foreach (var n in path)
                    {
                        if (n != null)
                            set.Add(n);
                    }
                }
                if (set.Count > 0)
                    return set;
            }

            if (!nodeControl.dictNode.TryGetValue("node_start", out var start) || start == null)
                return set;

            var stack = new Stack<NodeUI>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == null || !set.Add(cur))
                    continue;
                var childUIDs = cur.nodeBase?.childUID;
                if (childUIDs == null)
                    continue;
                foreach (var childUid in childUIDs)
                {
                    if (string.IsNullOrEmpty(childUid))
                        continue;
                    if (nodeControl.dictNode.TryGetValue(childUid, out var child) && child != null)
                        stack.Push(child);
                }
            }
            return set;
        }

        private static bool HasAnyChild(NodeBase nodeBase)
        {
            if (nodeBase?.childUID == null)
                return false;
            for (int i = 0; i < nodeBase.childUID.Length; i++)
            {
                if (!string.IsNullOrEmpty(nodeBase.childUID[i]))
                    return true;
            }
            return false;
        }

        // 与 PlayCheck 额外规则一致：可达 HPart（排除孤立不可达）groups[0].infoCharas 为空。
        private static void LogHPartNoChara(NodeCheck.CheckInfo checkInfo)
        {
            if (!Singleton<HEditData>.IsInstance() || Singleton<HEditData>.Instance.nodes == null)
                return;

            var isolatedUids = new HashSet<string>();
            if (checkInfo.lstUnreachable != null)
            {
                foreach (var n in checkInfo.lstUnreachable)
                {
                    if (n?.nodeBase == null)
                        continue;
                    if (checkInfo.lstUnreachableWithoutIsolated != null
                        && checkInfo.lstUnreachableWithoutIsolated.Contains(n))
                        continue;
                    isolatedUids.Add(n.nodeBase.uid);
                }
            }

            var names = new List<string>();
            var dictNode = Singleton<HEditGlobal>.Instance?.nodeControl?.dictNode;
            foreach (var kvp in Singleton<HEditData>.Instance.nodes)
            {
                if (kvp.Value == null || kvp.Value.kind != 0)
                    continue;
                if (isolatedUids.Contains(kvp.Key))
                    continue;

                var hpart = kvp.Value as HPart;
                // 与 PlayCheck 一致：看 groups[0].infoCharas；额外防空避免诊断本身 NRE。
                if (hpart?.groups == null || hpart.groups.Count == 0)
                    continue;
                var group0 = hpart.groups[0];
                if (group0?.infoCharas != null && group0.infoCharas.Count != 0)
                    continue;

                var name = GetDisplayNameByUid(dictNode, kvp.Key);
                if (name != null)
                    names.Add(name);
            }

            if (names.Count > 0)
                LogNamedList("HPart 未分配角色", names);
        }

        private static void LogNamedList(string reason, List<string> names)
        {
            if (names == null || names.Count == 0)
            {
                Log.LogMessage($"{reason}：未能点名具体 Part。");
                return;
            }

            var sb = new StringBuilder();
            sb.Append(reason).Append("：");
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(names[i]);
            }
            Log.LogMessage(sb.ToString());
        }

        // 显示名：nodeBase.name；空则 uid；系统节点跳过（返回 null）。
        private static string GetDisplayName(NodeUI nodeUI)
        {
            if (nodeUI?.nodeBase == null)
                return null;
            var uid = nodeUI.nodeBase.uid;
            if (uid == "node_start" || uid == "node_end")
                return null;
            var name = nodeUI.nodeBase.name;
            return string.IsNullOrEmpty(name) ? uid : name;
        }

        private static string GetDisplayNameByUid(Dictionary<string, NodeUI> dictNode, string uid)
        {
            if (uid == "node_start" || uid == "node_end")
                return null;
            if (dictNode != null
                && dictNode.TryGetValue(uid, out var nodeUI)
                && nodeUI?.nodeBase != null)
            {
                var name = nodeUI.nodeBase.name;
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            return uid;
        }
    }
}
