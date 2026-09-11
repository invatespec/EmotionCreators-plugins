using System;
using System.Collections.Generic;
using ADVPart.Manipulate.Item;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using ADVCtrl = ADV.ADV;
using ItemState = HEdit.ADVPart.ItemState;
using OCItem = Map.OCItem;

namespace EC_ItemParentPreserve
{
    internal static class Hooks
    {
        private const string ButtonName = "PreservePosition";
        private const string ButtonLabelOn = "保持位置:开";
        private const string ButtonLabelOff = "保持位置:关";
        private const float ButtonGap = 4f;
        private static readonly Color DisabledTint = new Color(0.45f, 0.45f, 0.45f, 1f);

        // LoadItem 末尾也会调 UpdateParent,此时物品尚未挂父级,快照/恢复必须跳过
        private static bool _loading;

        private static bool _hasSnapshot;
        private static Vector3 _savedPos;
        private static Quaternion _savedRot;

        private static TransformUICtrl _transformUICtrl;
        private static Button _injectedButton;

        // ---------- 加载路径屏蔽 ----------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ADVCtrl), nameof(ADVCtrl.LoadItem))]
        private static void LoadItemPrefix()
        {
            _loading = true;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(ADVCtrl), nameof(ADVCtrl.LoadItem))]
        private static void LoadItemFinalizer()
        {
            _loading = false;
        }

        // ---------- 世界位姿快照 / 恢复 ----------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ADVCtrl), nameof(ADVCtrl.UpdateParent))]
        private static void UpdateParentPrefix(ADVCtrl __instance, ItemState _itemState)
        {
            // 每次都先清旧快照,避免原生抛异常后残留的快照被下一次调用误用
            _hasSnapshot = false;
            if (_loading || !ItemParentPreservePlugin.IsPreserveEnabled) return;

            OCItem ocitem = FindItem(__instance, _itemState);
            if (ocitem == null) return;

            // 世界位姿取自矩阵,Transform 只是 CalcTransform 的输出副本
            Matrix4x4 wm = ocitem.worldMatrix;
            _savedPos = wm.GetColumn(3);
            _savedRot = wm.rotation;
            _hasSnapshot = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ADVCtrl), nameof(ADVCtrl.UpdateParent))]
        private static void UpdateParentPostfix(ADVCtrl __instance, ItemState _itemState)
        {
            if (!_hasSnapshot) return;
            _hasSnapshot = false;

            try
            {
                OCItem ocitem = FindItem(__instance, _itemState);
                if (ocitem == null) return;
                RestoreWorldPose(ocitem);
                RefreshTransformUI(_itemState);
            }
            catch (Exception ex)
            {
                ItemParentPreservePlugin.Log.LogWarning("恢复物品位姿失败: " + ex);
            }
        }

        private static OCItem FindItem(ADVCtrl adv, ItemState itemState)
        {
            if (adv == null || itemState == null || itemState.oiItem == null) return null;
            OCItem ocitem;
            return adv.dicItem.TryGetValue(itemState.oiItem, out ocitem) ? ocitem : null;
        }

        // 反算新父级下的局部量写入 changeAmount;直接写 Transform 会被 ItemUpdate.LateUpdate 每帧覆盖
        private static void RestoreWorldPose(OCItem ocitem)
        {
            Matrix4x4 pm = ocitem.parentCtrl != null ? ocitem.parentCtrl.worldMatrix : Matrix4x4.identity;
            Vector3 localPos = pm.inverse.MultiplyPoint3x4(_savedPos);
            Quaternion localRot = Quaternion.Inverse(pm.rotation) * _savedRot;
            ocitem.SetLocalPositionAndLocalRotation(localPos, localRot);

            Matrix4x4 after = ocitem.worldMatrix;
            ItemParentPreservePlugin.Log.LogDebug(
                "保持位姿 pos " + _savedPos + " -> " + (Vector3)after.GetColumn(3) +
                " rot " + _savedRot.eulerAngles + " -> " + after.rotation.eulerAngles);
        }

        // 只刷 Transform 分页数值;ItemUICtrl.UpdateUI 会重置滚动条
        private static void RefreshTransformUI(ItemState itemState)
        {
            if (_transformUICtrl == null || _transformUICtrl.itemState != itemState) return;
            _transformUICtrl.UpdateUI();
        }

        // ---------- UI 注入 ----------

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransformUICtrl), nameof(TransformUICtrl.Init))]
        private static void TransformUICtrlInitPostfix(TransformUICtrl __instance)
        {
            _transformUICtrl = __instance;

            // 场景切换会销毁按钮,Unity 销毁对象判 null 为真,此时允许重新注入
            if (_injectedButton != null) return;

            try
            {
                InjectButton(__instance);
            }
            catch (Exception ex)
            {
                ItemParentPreservePlugin.Log.LogWarning("按钮注入失败,功能由配置 EnableByDefault 控制: " + ex);
            }
        }

        // 克隆 Transform 分页的 gizmo 编辑按钮放到其左侧;不用 Toggle 是因为 Parent 分页的 ToggleGroup 会把它一并关掉
        private static void InjectButton(TransformUICtrl ctrl)
        {
            var source = Traverse.Create(ctrl).Field("button").GetValue<Button>();
            if (source == null)
            {
                ItemParentPreservePlugin.Log.LogWarning("TransformUICtrl.button 为空,跳过注入");
                return;
            }
            ItemParentPreservePlugin.Log.LogDebug("TransformUICtrl 层级: " + HierarchyPath(source.transform));

            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
            clone.name = ButtonName;
            clone.transform.SetSiblingIndex(source.transform.GetSiblingIndex());

            Button button = clone.GetComponent<Button>();
            if (button == null)
            {
                UnityEngine.Object.Destroy(clone);
                ItemParentPreservePlugin.Log.LogWarning("克隆节点无 Button 组件,跳过注入");
                return;
            }

            PlaceLeftOf(clone.GetComponent<RectTransform>(), source.GetComponent<RectTransform>());
            ClearListeners(button.onClick);
            button.onClick.AddListener(() =>
            {
                ItemParentPreservePlugin.IsPreserveEnabled = !ItemParentPreservePlugin.IsPreserveEnabled;
                ApplyButtonState(button);
                ItemParentPreservePlugin.Log.LogDebug("保持位置: " + (ItemParentPreservePlugin.IsPreserveEnabled ? "启用" : "禁用"));
            });

            _injectedButton = button;
            ApplyButtonState(button);
        }

        private static void PlaceLeftOf(RectTransform rt, RectTransform anchor)
        {
            if (rt == null || anchor == null) return;
            rt.anchoredPosition = anchor.anchoredPosition - new Vector2(anchor.rect.width + ButtonGap, 0f);
        }

        // 按钮原生可能只有图标没有文字:有文字就写状态,同时用图标明暗表示开关
        private static void ApplyButtonState(Button button)
        {
            bool on = ItemParentPreservePlugin.IsPreserveEnabled;
            var tmp = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) tmp.text = on ? ButtonLabelOn : ButtonLabelOff;
            else
            {
                var text = button.GetComponentInChildren<Text>(true);
                if (text != null) text.text = on ? ButtonLabelOn : ButtonLabelOff;
            }
            if (button.image != null) button.image.color = on ? Color.white : DisabledTint;
        }

        // RemoveAllListeners 不清 Inspector 持久监听,需逐个关闭
        private static void ClearListeners(UnityEventBase evt)
        {
            evt.RemoveAllListeners();
            for (int i = 0; i < evt.GetPersistentEventCount(); i++)
                evt.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        private static string HierarchyPath(Transform t)
        {
            var names = new List<string>();
            for (int depth = 0; t != null && depth < 8; depth++, t = t.parent)
                names.Insert(0, t.name);
            return string.Join("/", names.ToArray());
        }
    }
}
