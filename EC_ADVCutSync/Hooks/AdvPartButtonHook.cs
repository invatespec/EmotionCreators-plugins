using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EC_ADVCutSync
{
    // ADV Part 编辑器 Manipulate 工具行注入 CutSync 按钮（仿 EC_ADVUIImporve 的 display 按钮）
    internal static class AdvPartButtonHook
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ADVPart.ManipulateUICtrl), "Init")]
        internal static void ManipulateInitPostfix()
        {
            try
            {
                CreateButton();
            }
            catch (Exception e)
            {
                CutSyncPlugin.Log.LogWarning("button inject failed: " + e.Message);
            }
        }

        internal static void CreateButton()
        {
            var adv = GameObject.Find("ADVPart");
            if (adv == null)
                return;
            var root = adv.transform.Find("Canvas ADVPart/Manipulate/Button Root");
            var endBtn = adv.transform.Find("Canvas ADVPart/Button End");
            var fadeBtn = adv.transform.Find("Canvas ADVPart/Manipulate/Button Root/Button Fade");
            if (root == null || endBtn == null || fadeBtn == null)
                return;
            if (root.Find("btnCutSync") != null)
                return;

            var clone = UnityEngine.Object.Instantiate(endBtn.transform, root);
            clone.name = "btnCutSync";
            var image = clone.GetComponent<Image>();
            var srcImage = fadeBtn.GetComponent<Image>();
            if (image != null && srcImage != null)
            {
                image.sprite = srcImage.sprite;
                image.rectTransform.sizeDelta = srcImage.rectTransform.sizeDelta;
            }
            var button = clone.GetComponent<Button>();
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(CutSyncWindow.ToggleFromButton);
            }
            var label = clone.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
                label.text = "CutSync";
        }
    }
}
