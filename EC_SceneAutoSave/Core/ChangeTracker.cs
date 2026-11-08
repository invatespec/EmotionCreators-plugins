using System;
using System.Collections.Generic;
using HEdit;

namespace EC_SceneAutoSave.Core
{
    // 记录自上次自动保存以来可能改动的 Part 与角色。只登记候选，是否真的变化由内容哈希决定。
    internal sealed class ChangeTracker
    {
        private readonly Dictionary<string, BasePart> refs = new Dictionary<string, BasePart>(StringComparer.Ordinal);
        private readonly HashSet<string> touched = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<int> swapped = new HashSet<int>();
        private BasePart lastSelected;

        public IEnumerable<int> Swapped => swapped;

        public bool AnySwapped => swapped.Count > 0;

        public void Reset(Dictionary<string, BasePart> nodes)
        {
            RebuildRefs(nodes);
            touched.Clear();
            swapped.Clear();
        }

        // 试玩返回与应用会话后，Part 对象全部重新创建，内容未变，只换引用。
        // 不清空候选：上次自动保存失败时，这些 uid 仍需在下次写入。
        public void RebuildRefs(Dictionary<string, BasePart> nodes)
        {
            refs.Clear();
            foreach (var pair in nodes)
                refs[pair.Key] = pair.Value;
            lastSelected = null;
        }

        // 读档时 SelectPart 可能还指向旧场景的对象，读档完成后才被原生清空，不能当成新打开的 Part。
        // 返回值为仍在编辑、需要记录基准哈希的当前 Part。
        public BasePart Adopt(HEditGlobal global)
        {
            var selected = global?.SelectPart;
            lastSelected = selected;
            if (selected == null || !IsBaseRef(selected.uuId, selected))
                return null;
            touched.Add(selected.uuId);
            return selected;
        }

        // 引用变化在重建引用表后就看不出来了，记录前先转成按 uid 的候选。
        public void PromoteCandidates(Dictionary<string, BasePart> nodes)
        {
            foreach (var pair in nodes)
            {
                if (!IsBaseRef(pair.Key, pair.Value))
                    touched.Add(pair.Key);
            }
        }

        public bool IsBaseRef(string uid, BasePart part)
        {
            return uid != null && refs.TryGetValue(uid, out var known) && ReferenceEquals(known, part);
        }

        public bool IsTouched(string uid) => uid != null && touched.Contains(uid);

        public void MarkPart(string uid)
        {
            if (!string.IsNullOrEmpty(uid))
                touched.Add(uid);
        }

        public void MarkChara(int index)
        {
            if (index >= 0)
                swapped.Add(index);
        }

        // 已有场景中 SelectPart 非空只发生在进入 Part 编辑。
        public void Poll(HEditGlobal global)
        {
            var selected = global.SelectPart;
            if (ReferenceEquals(selected, lastSelected))
                return;
            lastSelected = selected;
            if (selected != null)
                MarkPart(selected.uuId);
        }

        // 候选 = 打开过的 Part + 新增或对象被替换的 Part。
        public List<string> Candidates(Dictionary<string, BasePart> nodes)
        {
            var result = new List<string>();
            foreach (var pair in nodes)
            {
                if (touched.Contains(pair.Key) || !IsBaseRef(pair.Key, pair.Value))
                    result.Add(pair.Key);
            }
            return result;
        }

        public bool PartSetChanged(Dictionary<string, BasePart> nodes)
        {
            if (nodes.Count != refs.Count)
                return true;
            foreach (var pair in nodes)
            {
                if (!IsBaseRef(pair.Key, pair.Value))
                    return true;
            }
            return false;
        }

        // 提交成功后以当前状态为新起点；仍在编辑的 Part 继续作为候选。
        public void AfterCommit(Dictionary<string, BasePart> nodes, HEditGlobal global)
        {
            RebuildRefs(nodes);
            touched.Clear();
            swapped.Clear();
            var selected = global?.SelectPart;
            lastSelected = selected;
            if (selected != null)
                touched.Add(selected.uuId);
        }
    }
}
