using HarmonyLib;
using HEdit;
using Map;
using UnityEngine;
using UnityEngine.UI;

namespace EC_LightingEnhance.Hooks
{
    internal static class HEditLightUIHooks
    {
        private static bool _uiInjected = false;
        private static GameObject _sunSlider;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PartInfoLightUI), "LightUpdate")]
        internal static void LightUpdatePostfix(int _kind)
        {
            bool isMap = _kind == 1;

            if (_sunSlider != null)
                _sunSlider.SetActive(isMap);

            if (!isMap)
            {
                LightingEnhanceCore.ApplyCharaRenderMode();
                SyncCharaLightEulerFromPart();
                return;
            }
            if (!Singleton<HEditGlobal>.IsInstance()) return;
            var lightMap = Singleton<HEditGlobal>.Instance.lightMap;
            if (lightMap == null) return;

            LightingEnhanceCore.SyncToSunlight(lightMap);
            LightingEnhanceCore.OnLightUpdate();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PartInfoLightUI), "InitUI")]
        internal static void InitUIPostfix(PartInfoLightUI __instance)
        {
            // 场景切换后 slider 已销毁，重置标志允许重新注入
            if (_uiInjected && _sunSlider == null)
                _uiInjected = false;

            if (_uiInjected) return;
            _uiInjected = true;
            try
            {
                InjectIntensitySlider(__instance);
                ListenToMapToggle(__instance);
            }
            catch (System.Exception ex)
            {
                LightingEnhancePlugin.Log.LogWarning("HEdit UI injection failed: " + ex.Message);
                _uiInjected = false;
            }
        }

        private static void InjectIntensitySlider(PartInfoLightUI ui)
        {
            var siIntensity = Traverse.Create(ui).Field("siIntensity").GetValue<SliderAndTextMeshInput>();
            if (siIntensity == null || siIntensity.slider == null) return;

            bool isHPart = IsHPartPanel(ui);

            Transform intensityNode;
            Transform cloneParent;

            if (isHPart)
            {
                intensityNode = siIntensity.slider.transform.parent?.parent?.parent;
                cloneParent = intensityNode?.parent;
            }
            else
            {
                intensityNode = siIntensity.slider.transform.parent;
                cloneParent = intensityNode?.parent;
            }

            if (intensityNode == null || cloneParent == null) return;

            var clone = Object.Instantiate(intensityNode.gameObject, cloneParent);
            clone.name = "SunIntensity";
            clone.SetActive(false);
            clone.transform.SetSiblingIndex(intensityNode.GetSiblingIndex() + 1);

            var slider = clone.GetComponentInChildren<Slider>();
            if (slider == null)
            {
                Object.Destroy(clone);
                return;
            }

            var childrenToDelete = new System.Collections.Generic.List<Transform>();

            if (isHPart)
            {
                foreach (Transform child in clone.transform)
                {
                    if (child.name == "Item")
                    {
                        foreach (Transform itemChild in child)
                        {
                            if (itemChild.name == "Do")
                            {
                                foreach (Transform doChild in itemChild)
                                {
                                    if (doChild.GetComponent<Slider>() == null)
                                        childrenToDelete.Add(doChild);
                                }
                            }
                            else
                            {
                                childrenToDelete.Add(itemChild);
                            }
                        }
                    }
                    else
                    {
                        childrenToDelete.Add(child);
                    }
                }
            }
            else
            {
                foreach (Transform child in clone.transform)
                {
                    if (child.GetComponent<Slider>() == null)
                        childrenToDelete.Add(child);
                }
            }

            foreach (var child in childrenToDelete)
                Object.Destroy(child.gameObject);

            slider.name = "SunIntensitySlider";
            slider.minValue = 0f;
            slider.maxValue = 3f;
            slider.value = LightingEnhancePlugin.SunIntensityMultiplier.Value;
            slider.onValueChanged.RemoveAllListeners();
            slider.onValueChanged.AddListener(val => LightingEnhancePlugin.SunIntensityMultiplier.Value = val);

            _sunSlider = clone;
        }

        private static bool IsHPartPanel(PartInfoLightUI ui)
        {
            var siIntensity = Traverse.Create(ui).Field("siIntensity").GetValue<SliderAndTextMeshInput>();
            if (siIntensity?.slider == null) return false;

            Transform current = siIntensity.slider.transform;
            for (int i = 0; i < 10 && current != null; i++)
            {
                if (current.name == "HPart" || current.name == "PartInfoSetting")
                    return true;
                current = current.parent;
            }
            return false;
        }

        private static void SyncCharaLightEulerFromPart()
        {
            if (!Singleton<HEditGlobal>.IsInstance()) return;

            var hpart = Singleton<HEditGlobal>.Instance.SelectPart as HPart;
            var lightInfos = hpart?.cameraImage?.lightInfos;
            if (lightInfos == null || lightInfos.Length == 0) return;

            LightingEnhanceCore.SetCameraCharaLightEuler(lightInfos[0]);
        }

        private static void ListenToMapToggle(PartInfoLightUI ui)
        {
            var tglKindTypes = Traverse.Create(ui).Field("tglKindTypes").GetValue<Toggle[]>();
            if (tglKindTypes == null || tglKindTypes.Length < 2) return;

            var tglMap = tglKindTypes[1];
            if (tglMap == null) return;

            tglMap.onValueChanged.AddListener(isOn =>
            {
                if (_sunSlider != null)
                    _sunSlider.SetActive(isOn);
            });
        }
    }
}
