using ADV;
using BepInEx.Logging;
using HEdit;
using HPlay;
using System;

namespace EC_BackVarSync
{
    /// <summary>
    /// 一次性位置跳转（读自动存档用）。照搬 EC_back 已实测的跳转链，但状态自持、不依赖 EC_back：
    /// TryArm 置 isNextPart → HPlayScene.Update 调 GetNextUID（Postfix 改目标 uid）
    /// → InitPart → ADVPlay.SetPart（Prefix 改 cutIndex 并 NextCut）→ 目标 cut 同步加载完回调 onLanded。
    /// </summary>
    internal sealed class PositionJump
    {
        private enum Stage { Idle, WaitNextUid, WaitSetPart }

        private readonly ManualLogSource _log;
        private Stage _stage;
        private string _sceneTitle = "";
        private string _uid = "";
        private int _cut;
        private Action _onLanded;

        public PositionJump(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>挂起中：此时前进键/自动前进应暂停，避免中间 cut 的脚本改写已恢复的变量。</summary>
        public bool Armed => _stage != Stage.Idle;

        /// <summary>目标 part 是有效 ADV part 且 cut 未越界才挂起；cut 为 0 基（与存档 cutIndex 一致）。</summary>
        public bool TryArm(string sceneTitle, string uid, int cut, Action onLanded)
        {
            if (Armed || !IsValidTarget(uid, cut))
                return false;

            var hpd = Singleton<HPlayData>.Instance;
            if (hpd == null || hpd.nodeBase == null)
                return false;

            _sceneTitle = sceneTitle ?? "";
            _uid = uid;
            _cut = cut;
            _onLanded = onLanded;
            _stage = Stage.WaitNextUid;

            hpd.isNextPart = true;
            hpd.nextPart = 0;
            return true;
        }

        public void Cancel(string reason)
        {
            if (!Armed)
                return;
            _stage = Stage.Idle;
            _onLanded = null;
            _log.LogInfo($"自动存档跳转已取消：{reason}");
        }

        /// <summary>GetNextUID Postfix 调用。nextPart 为 -1 表示场景从头开始，挂起作废。</summary>
        public void OverrideNextUid(int nextPart, ref string result)
        {
            if (_stage != Stage.WaitNextUid)
                return;

            if (nextPart < 0 || VarStore.GetSceneTitle() != _sceneTitle || !IsValidTarget(_uid, _cut))
            {
                Cancel("场景已重新开始或目标失效");
                return;
            }

            var hpd = Singleton<HPlayData>.Instance;
            hpd.isNextScene = false; // 当前 part 若连着 node_end，原方法已置结束标志，这里撤回
            hpd.nextPart = 0;        // Update 随后按 nextPart 读 fadeType，固定取 0 号连线的淡入淡出
            result = _uid;
            _stage = Stage.WaitSetPart;
        }

        /// <summary>SetPart Prefix 调用。返回 true 表示已接管（原方法应跳过）。</summary>
        public bool TryTakeOverSetPart(ADVPlay instance, global::HEdit.ADVPart part,
                                        ref global::HEdit.ADVPart fieldPart, ref int fieldCutIndex)
        {
            if (_stage != Stage.WaitSetPart)
                return false;

            if (part == null || part.uuId != _uid || part.cuts == null || _cut >= part.cuts.Count)
            {
                Cancel("进入的 part 与目标不一致");
                return false;
            }

            Action landed = _onLanded;
            _stage = Stage.Idle;
            _onLanded = null;

            fieldPart = part;
            fieldCutIndex = _cut;
            try
            {
                instance.NextCut(); // 同步执行目标 cut 的 LoadCut（含 VariableMod 脚本与本插件记账）
            }
            finally
            {
                // 其他插件的 LoadCut 补丁抛异常时也要补做落地处理，异常照常上抛
                InvokeLanded(landed);
            }
            return true;
        }

        private void InvokeLanded(Action landed)
        {
            try
            {
                landed?.Invoke();
            }
            catch (Exception ex)
            {
                _log.LogWarning($"自动存档跳转落地处理异常: {ex.Message}");
            }
        }

        private static bool IsValidTarget(string uid, int cut)
        {
            if (string.IsNullOrEmpty(uid) || cut < 0)
                return false;

            var data = Singleton<HEditData>.Instance;
            if (data?.nodes == null || data.nodeBases == null || !data.nodeBases.ContainsKey(uid))
                return false;

            return data.nodes.TryGetValue(uid, out var basePart)
                && basePart is global::HEdit.ADVPart adv
                && adv.cuts != null
                && cut < adv.cuts.Count;
        }
    }
}
