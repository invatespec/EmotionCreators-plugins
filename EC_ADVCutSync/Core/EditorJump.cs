using System;
using ADVCreate;
using ADVPart.List;
using ADVPart.Manipulate;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using AdvPart = HEdit.ADVPart;

namespace EC_ADVCutSync
{
    internal static class EditorJump
    {
        // 赋值 cut 即切换编辑器选中（reactiveCut 订阅自动重建画面与面板）
        public static bool JumpTo(AdvPart part, int cutIndex, int charIndex = -1, ItemKind? locate = null)
        {
            if (!Singleton<ADVPartUICtrl>.IsInstance())
                return false;
            var ctrl = Singleton<ADVPartUICtrl>.Instance;
            if (ctrl.advPart != part || part == null)
                return false;
            if (cutIndex < 0 || cutIndex >= part.cuts.Count)
                return false;
            ctrl.cut = part.cuts[cutIndex];
            // 列表单元格的高亮在各 cell 的 toggle 上，直接赋值不会触发；刷新可见单元格
            try
            {
                var list = Traverse.Create(ctrl).Field("listUICtrl").GetValue<ListUICtrl>();
                list?.cutListUICtrl.UpdateUI(part);
            }
            catch (Exception e)
            {
                CutSyncPlugin.Log.LogWarning("cut list refresh failed: " + e.Message);
            }
            if (locate.HasValue)
                LocatePanel(ctrl, charIndex, locate.Value);
            return true;
        }

        // 跳转后打开对应 Manipulate 面板并滚动到目标栏目；必须与 cut 赋值同帧之后执行
        // （reactiveCut 订阅会把所有面板置 inactive，随后再激活才不会被覆盖）
        private static void LocatePanel(ADVPartUICtrl ctrl, int charIndex, ItemKind kind)
        {
            try
            {
                var manip = ctrl.ManipulateUICtrl;
                if (manip == null)
                    return;
                if (kind == ItemKind.Bgm)
                {
                    var bgm = Traverse.Create(manip).Field("bgmUICtrl").GetValue<BGMUICtrl>();
                    if (bgm != null)
                        bgm.active = true;
                    return;
                }
                var chara = Traverse.Create(manip).Field("charaUICtrl").GetValue<CharaUICtrl>();
                if (chara == null)
                    return;
                chara.active = true;
                // 选中角色：selects[i].toggle.isOn=true 触发原生 Select（刷新全部子面板）
                if (charIndex >= 0)
                {
                    var selects = Traverse.Create(chara).Field("selects").GetValue() as Array;
                    if (selects != null && charIndex < selects.Length)
                    {
                        var toggle = Traverse.Create(selects.GetValue(charIndex)).Field("toggle").GetValue<Toggle>();
                        if (toggle != null)
                            toggle.isOn = true;
                    }
                }
                var section = FindSection(chara, kind);
                if (section >= 0)
                    Traverse.Create(chara).Method("SetScrollBar", section).GetValue();
            }
            catch (Exception e)
            {
                CutSyncPlugin.Log.LogWarning("panel locate failed: " + e.Message);
            }
        }

        // 栏目序号：优先按 rtState 物体名匹配，退化用 deriveds 序号（0 显示/5 服装/6 穿脱/7 饰品/8 汁液/10 音声）
        private static int FindSection(CharaUICtrl chara, ItemKind kind)
        {
            var candidates = SectionNames(kind);
            var exclude = kind == ItemKind.Visible ? new[] { "sun" } : null;
            if (candidates.Length > 0)
            {
                var rtState = Traverse.Create(chara).Field("rtState").GetValue<RectTransform[]>();
                if (rtState != null)
                {
                    for (var i = 0; i < rtState.Length; i++)
                    {
                        if (rtState[i] == null)
                            continue;
                        var name = rtState[i].gameObject.name.ToLowerInvariant();
                        if (exclude != null && ContainsAny(name, exclude))
                            continue;
                        if (ContainsAny(name, candidates))
                            return i;
                    }
                }
            }
            switch (kind)
            {
                case ItemKind.Visible: return 0;
                case ItemKind.Coordinate: return 5;
                case ItemKind.Clothes: return 6;
                case ItemKind.Accessory: return 7;
                case ItemKind.Liquid: return 8;
                case ItemKind.Voice: return 10;
                default: return -1;
            }
        }

        private static string[] SectionNames(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Visible: return new[] { "visible" };
                case ItemKind.Coordinate: return new[] { "coordinate" };
                case ItemKind.Clothes: return new[] { "clothes", "wear" };
                case ItemKind.Accessory: return new[] { "accessory" };
                case ItemKind.Liquid: return new[] { "liquid", "siru" };
                case ItemKind.Voice: return new[] { "voice" };
                default: return new string[0];
            }
        }

        private static bool ContainsAny(string name, string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (name.Contains(candidate))
                    return true;
            }
            return false;
        }
    }
}
