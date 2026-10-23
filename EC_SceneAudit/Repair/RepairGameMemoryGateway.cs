using System;
using System.Collections.Generic;
using ADVCreate;
using HEdit;
using UnityEngine;
using YS_Node;

namespace EC_SceneAudit.Repair
{
    // 游戏内存适配：Part/NodeBase 走原生 Save/Load 往返；连线槽位直接读写 NodeControl.dictNode
    // 的 NodeBase（编辑器实时连线以它为准，HEditData.nodeBases 不参与）。
    //
    // GenerationOwner 绑定 nodeControl.dictNode 实例：该字典每次 RebuildNodeUI 都会换新，
    // 所以切换 Scene 或同路径重新加载都会让旧快照失效。
    internal sealed class RepairGameMemoryGateway : IRepairMemoryHost
    {
        private const int NodeSlotCount = 5;

        private readonly HEditData data;
        private readonly NodeControl nodeControl;

        public RepairGameMemoryGateway(HEditData data, NodeControl nodeControl)
        {
            this.data = data;
            this.nodeControl = nodeControl;
        }

        public int SlotCount => NodeSlotCount;

        public object SceneOwner => data;

        public object NodeOwner => nodeControl;

        public object GenerationOwner => nodeControl == null ? null : nodeControl.dictNode;

        public IEnumerable<string> NodeUids
        {
            get
            {
                var dict = nodeControl == null ? null : nodeControl.dictNode;
                return dict == null ? (IEnumerable<string>)new string[0] : new List<string>(dict.Keys);
            }
        }

        public bool TryReadPart(string uid, out RepairPartValue value, out string error)
        {
            value = null;
            HEdit.ADVPart part;
            if (!TryGetAdvPart(uid, out part, out error))
                return false;
            byte[] payload;
            string codecError;
            if (!RepairPartCodec.TrySerialize(part, out payload, out codecError))
            {
                error = "Part 序列化失败（" + uid + "）：" + codecError;
                return false;
            }
            value = new RepairPartValue { Uid = uid, Data = payload };
            return true;
        }

        public bool TryWritePart(RepairPartValue value, out string error)
        {
            error = null;
            if (value == null || string.IsNullOrEmpty(value.Uid))
            {
                error = "目标 Part 记录无效。";
                return false;
            }
            if (data?.nodes == null)
            {
                error = "HEditData 不可用。";
                return false;
            }

            HEdit.ADVPart current;
            if (!TryGetAdvPart(value.Uid, out current, out error))
                return false;

            var restored = RepairPartCodec.Deserialize(value.Data, RepairPartCodec.FormatVersion(data), out var codecError);
            if (restored == null)
            {
                error = "Part 反序列化失败（" + value.Uid + "）：" + codecError;
                return false;
            }
            // 保留原 UID，避免快照与节点键错位。
            restored.uuId = value.Uid;

            if (!data.nodes.ContainsKey(value.Uid))
            {
                error = "目标 Part 已不存在：" + value.Uid;
                return false;
            }
            data.nodes[value.Uid] = restored;
            RepointPartReferences(current, restored);
            return true;
        }

        public bool TryReadNode(string uid, out RepairNodeValue value, out string error)
        {
            value = null;
            NodeUI node;
            if (!TryGetNode(uid, out node, out error))
                return false;
            byte[] payload;
            string codecError;
            if (!RepairNodeCodec.TrySerialize(node.nodeBase, out payload, out codecError))
            {
                error = "节点序列化失败（" + uid + "）：" + codecError;
                return false;
            }
            value = new RepairNodeValue { Uid = uid, Data = payload };
            return true;
        }

        public bool TryWriteNode(RepairNodeValue value, out string error)
        {
            error = null;
            if (value == null || string.IsNullOrEmpty(value.Uid))
            {
                error = "目标节点记录无效。";
                return false;
            }
            NodeUI node;
            if (!TryGetNode(value.Uid, out node, out error))
                return false;

            string codecError;
            if (!RepairNodeCodec.TryLoadInto(node.nodeBase, value.Data, RepairPartCodec.FormatVersion(data), out codecError))
            {
                error = "节点反序列化失败（" + value.Uid + "）：" + codecError;
                return false;
            }
            // Copy 只改数据，视觉连线要按新槽位重建。
            SyncAllLines(node);
            return true;
        }

        public bool TryReadSlot(string sourceUid, int index, out RepairSlotValue value, out string error)
        {
            value = default(RepairSlotValue);
            NodeUI node;
            if (!TryGetNode(sourceUid, out node, out error))
                return false;
            var nodeBase = node.nodeBase;
            if (!HasSlot(nodeBase, index))
            {
                error = "节点槽位越界：" + sourceUid + "/slot:" + index;
                return false;
            }
            value = new RepairSlotValue
            {
                ChildUid = nodeBase.childUID[index] ?? string.Empty,
                EndConditionType = (int)nodeBase.endConditionType[index],
                EndConditionCount = nodeBase.endConditionCount[index],
                FadeType = nodeBase.fadeType[index],
                FadeTime = nodeBase.fadeTime[index]
            };
            return true;
        }

        public bool TryWriteSlot(string sourceUid, int index, RepairSlotValue value, out string error)
        {
            error = null;
            NodeUI node;
            if (!TryGetNode(sourceUid, out node, out error))
                return false;
            if (!HasSlot(node.nodeBase, index))
            {
                error = "节点槽位越界：" + sourceUid + "/slot:" + index;
                return false;
            }

            var child = value.ChildUid ?? string.Empty;
            // DisconnectLine 会清空 childUID 并销毁旧线，所以先记下新值再回填。
            node.DisconnectLine(index);
            var nodeBase = node.nodeBase;
            nodeBase.childUID[index] = child;
            nodeBase.endConditionType[index] = (EndConditionType)value.EndConditionType;
            nodeBase.endConditionCount[index] = value.EndConditionCount;
            nodeBase.fadeType[index] = value.FadeType;
            nodeBase.fadeTime[index] = value.FadeTime;
            if (!string.IsNullOrEmpty(child))
                node.CreateConnectLine(index, child);
            return true;
        }

        public bool TryValidateTargets(ICollection<string> targetUids, out string error)
        {
            error = null;
            if (data?.nodes == null || nodeControl?.dictNode == null)
            {
                error = "HEditData 或 nodeControl 不可用。";
                return false;
            }
            if (IsAdvEditingActive())
            {
                error = "当前在 ADV 编辑界面，请先返回节点总览再应用或恢复。";
                return false;
            }
            foreach (var uid in targetUids ?? new List<string>())
            {
                NodeUI ignored;
                if (!TryGetNode(uid, out ignored, out error))
                    return false;
            }
            return true;
        }

        // pause 默认 true，Active() 置 false，Deactive() 置 true；false 表示 ADV 编辑界面正在使用中。
        private static bool IsAdvEditingActive()
        {
            try
            {
                if (!ADVPartUICtrl.IsInstance())
                    return false;
                var ctrl = ADVPartUICtrl.Instance;
                return ctrl != null && !ctrl.pause;
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetNode(string uid, out NodeUI node, out string error)
        {
            node = null;
            error = null;
            if (string.IsNullOrEmpty(uid))
            {
                error = "节点 UID 为空。";
                return false;
            }
            if (nodeControl?.dictNode == null)
            {
                error = "nodeControl 不可用。";
                return false;
            }
            if (!nodeControl.dictNode.TryGetValue(uid, out node) || node == null || node.nodeBase == null)
            {
                error = "找不到节点：" + uid;
                return false;
            }
            return true;
        }

        private bool TryGetAdvPart(string uid, out HEdit.ADVPart part, out string error)
        {
            part = null;
            error = null;
            BasePart basePart;
            if (string.IsNullOrEmpty(uid) || data?.nodes == null
                || !data.nodes.TryGetValue(uid, out basePart) || basePart == null)
            {
                error = "找不到 Part：" + uid;
                return false;
            }
            part = basePart as HEdit.ADVPart;
            if (part == null)
            {
                error = "目标不是 ADV Part：" + uid;
                return false;
            }
            return true;
        }

        private static bool HasSlot(NodeBase nodeBase, int index)
        {
            return nodeBase != null && index >= 0 && index < NodeSlotCount
                && nodeBase.childUID != null && nodeBase.childUID.Length >= NodeSlotCount
                && nodeBase.endConditionType != null && nodeBase.endConditionCount != null
                && nodeBase.fadeType != null && nodeBase.fadeTime != null;
        }

        private static void SyncAllLines(NodeUI node)
        {
            for (var index = 0; index < NodeSlotCount; index++)
            {
                var child = node.nodeBase.childUID[index];
                node.DisconnectLine(index);
                node.nodeBase.childUID[index] = child ?? string.Empty;
                if (!string.IsNullOrEmpty(child))
                    node.CreateConnectLine(index, child);
            }
        }

        // 替换 Part 后同步选择与总览信息面板引用，避免留下指向旧对象的引用；
        // ADV 编辑引用只在快照目标上原位换对象后再通知，ReloadCut 单独调用不会替换对象。
        private static void RepointPartReferences(HEdit.ADVPart oldPart, HEdit.ADVPart newPart)
        {
            try
            {
                if (HEditGlobal.IsInstance() && HEditGlobal.Instance != null
                    && ReferenceEquals(HEditGlobal.Instance.SelectPart, oldPart))
                    HEditGlobal.Instance.SelectPart = newPart;
            }
            catch
            {
                // 引用同步失败不回滚数据：数据已一致，重新进入编辑会重新绑定。
            }

            try
            {
                var infos = UnityEngine.Object.FindObjectsOfType<NodeInfo>();
                if (infos == null)
                    return;
                foreach (var info in infos)
                {
                    if (info != null && ReferenceEquals(info.part, oldPart))
                        info.part = newPart;
                }
            }
            catch
            {
                // 同上：仅引用同步，不影响数据一致性。
            }

            RepointAdvEditor(oldPart, newPart);
        }

        // ADV 编辑界面处于关闭状态（总览）时才改引用；先把 advPart/cut 指向恢复后的对象，再通知。
        private static void RepointAdvEditor(HEdit.ADVPart oldPart, HEdit.ADVPart newPart)
        {
            try
            {
                if (!ADVPartUICtrl.IsInstance())
                    return;
                var ctrl = ADVPartUICtrl.Instance;
                if (ctrl == null || !ReferenceEquals(ctrl.advPart, oldPart))
                    return;

                var index = 0;
                var previousCut = ctrl.cut;
                if (previousCut != null && oldPart.cuts != null)
                {
                    for (var i = 0; i < oldPart.cuts.Count; i++)
                    {
                        if (ReferenceEquals(oldPart.cuts[i], previousCut))
                        {
                            index = i;
                            break;
                        }
                    }
                }
                ctrl.advPart = newPart;
                var cuts = newPart.cuts;
                ctrl.cut = cuts != null && cuts.Count > 0 ? cuts[Math.Min(index, cuts.Count - 1)] : null;
                ctrl.ReloadCut();
            }
            catch
            {
                // 通知失败只影响界面刷新，下一次进入 ADV 编辑会重新绑定。
            }
        }
    }
}
