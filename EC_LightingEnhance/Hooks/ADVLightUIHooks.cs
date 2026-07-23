using ADVPart.Manipulate;
using ADVCreate;
using HarmonyLib;
using HEdit;
using Map;
using UnityEngine;
using UnityEngine.UI;

namespace EC_LightingEnhance.Hooks
{
    internal static class ADVLightUIHooks
    {
        private static bool _uiInjected = false;
        private static GameObject _sunSlider;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LightUICtrl), "LightUpdate")]
        internal static void LightUpdatePostfix(int _kind)
        {
            bool isMap = _kind == 1;

            if (_sunSlider != null)
                _sunSlider.SetActive(isMap);

            if (!isMap)
            {
                LightingEnhanceCore.ApplyCharaRenderMode();
                SyncCharaLightEulerFromCut();
                return;
            }
            if (!Singleton<HEditGlobal>.IsInstance()) return;
            var lightMap = Singleton<HEditGlobal>.Instance.lightMap;
            if (lightMap == null) return;

            LightingEnhanceCore.SyncToSunlight(lightMap);
            LightingEnhanceCore.OnLightUpdate();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LightUICtrl), "Init")]
        internal static void InitPostfix(LightUICtrl __instance)
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
                LightingEnhancePlugin.Log.LogWarning("ADV UI injection failed: " + ex.Message);
                _uiInjected = false;
            }
        }

        private static void InjectIntensitySlider(LightUICtrl ctrl)
        {
            var siIntensity = Traverse.Create(ctrl).Field("siIntensity").GetValue<SliderAndTextMeshInput>();
            if (siIntensity == null || siIntensity.slider == null) return;

            var intensityNode = siIntensity.slider.transform.parent;
            if (intensityNode == null || intensityNode.name != "Intensity") return;

            var imageBackground = intensityNode.parent;
            if (imageBackground == null) return;

            var clone = Object.Instantiate(intensityNode.gameObject, imageBackground);
            clone.name = "SunIntensity";
            clone.SetActive(false);
            clone.transform.SetSiblingIndex(intensityNode.GetSiblingIndex() + 1);

            Transform sliderTransform = null;
            foreach (Transform child in clone.transform)
            {
                if (child.GetComponent<Slider>() != null)
                {
                    sliderTransform = child;
                    break;
                }
            }

            var childrenToDelete = new System.Collections.Generic.List<Transform>();
            foreach (Transform child in clone.transform)
            {
                if (child != sliderTransform)
                    childrenToDelete.Add(child);
            }

            foreach (var child in childrenToDelete)
                Object.Destroy(child.gameObject);

            if (sliderTransform == null)
            {
                Object.Destroy(clone);
                return;
            }

            sliderTransform.name = "SunIntensitySlider";
            var cloneSlider = sliderTransform.GetComponent<Slider>();
            if (cloneSlider != null)
            {
                cloneSlider.minValue = 0f;
                cloneSlider.maxValue = 3f;
                cloneSlider.value = LightingEnhancePlugin.SunIntensityMultiplier.Value;
                cloneSlider.onValueChanged.RemoveAllListeners();
                cloneSlider.onValueChanged.AddListener(val => LightingEnhancePlugin.SunIntensityMultiplier.Value = val);
            }

            _sunSlider = clone;
        }

        private static void ListenToMapToggle(LightUICtrl ctrl)
        {
            var tglKindTypes = Traverse.Create(ctrl).Field("tglKindTypes").GetValue<Toggle[]>();
            if (tglKindTypes == null || tglKindTypes.Length < 2) return;

            var tglMap = tglKindTypes[1];
            if (tglMap == null) return;

            tglMap.onValueChanged.AddListener(isOn =>
            {
                if (_sunSlider != null)
                    _sunSlider.SetActive(isOn);
            });
        }

        private static void SyncCharaLightEulerFromCut()
        {
            if (!Singleton<ADVPartUICtrl>.IsInstance()) return;

            var cut = Singleton<ADVPartUICtrl>.Instance.cut;
            var lightInfos = cut?.cameraImageEffectInfo?.lightInfos;
            if (lightInfos == null || lightInfos.Length == 0) return;

            LightingEnhanceCore.SetCameraCharaLightEuler(lightInfos[0]);
        }
    }
}
