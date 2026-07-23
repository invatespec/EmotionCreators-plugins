using System.Collections.Generic;
using HEdit;
using Map;
using UnityEngine;
using UnityEngine.Rendering;

namespace EC_LightingEnhance
{
    internal static class LightingEnhanceCore
    {
        private const int CharaLayer = 10;
        private const int MapLayer = 11;
        private const string CameraCharaLightPath = "Camera/Main Camera/Directional Light";
        private static readonly Dictionary<Renderer, ShadowProxyEntry> ShadowProxies =
            new Dictionary<Renderer, ShadowProxyEntry>();
        private static Light _cachedMapLightDir;
        private static Light _lastMainLight;
        private static Light _lastMapLightDir;
        private static int _mapLightRetryFrame;
        private static Light _cachedCameraCharaLight;
        private static int _cameraCharaLightRetryFrame;
        private static Quaternion _targetCameraCharaRotation = Quaternion.identity;
        private static bool _hasTargetRotation;
        private static bool _shadowProxySyncNeeded = true;
        private const int MapLightRetryInterval = 120; // 每2秒重试一次
        private const int CameraCharaLightRetryInterval = 120; // 每2秒重试一次

        internal class LightSetup
        {
            public Light MainLight;
            public Light MapLightDir;
            public bool IsModMap;
        }

        private sealed class ShadowProxyEntry
        {
            public GameObject GameObject;
            public Renderer Renderer;
        }

        /// <summary>
        /// 从 EC_SunlightSync 移植：将游戏内光照控件的值同步到地图形 Sunlight。
        /// </summary>
        internal static void SyncToSunlight(Light gameLight)
        {
            if (gameLight == null) return;
            if (!Singleton<Map.Map>.IsInstance()) return;

            var map = Singleton<Map.Map>.Instance;
            if (map.sunLightInfo == null || map.sunLightInfo.targetLight == null) return;

            var sunLight = map.sunLightInfo.targetLight;

            sunLight.color = gameLight.color;
            sunLight.transform.rotation = gameLight.transform.rotation;

            float mult = LightingEnhancePlugin.SunIntensityMultiplier.Value;
            sunLight.intensity = gameLight.intensity * mult;
        }

        /// <summary>
        /// 检测地图光源配置：
        /// - mod 地图：Map/Sunlight + MapLight/DirectionalLight 两个方向光
        /// - 内置地图：仅 MapLight/DirectionalLight
        /// </summary>
        internal static LightSetup DetectLightSetup()
        {
            Light sunLight = null;
            Light mapLightDir = null;

            if (Singleton<Map.Map>.IsInstance())
            {
                var map = Singleton<Map.Map>.Instance;
                if (map.sunLightInfo != null)
                    sunLight = map.sunLightInfo.targetLight;
            }

            if (_cachedMapLightDir == null)
            {
                var mapLightGo = GameObject.Find("MapLight/DirectionalLight");
                if (mapLightGo != null)
                    _cachedMapLightDir = mapLightGo.GetComponent<Light>();
            }
            mapLightDir = _cachedMapLightDir;

            bool isModMap = (sunLight != null);
            Light mainLight = isModMap ? sunLight : mapLightDir;

            return new LightSetup { MainLight = mainLight, MapLightDir = mapLightDir, IsModMap = isModMap };
        }

        /// <summary>
        /// 强制 Unity 重新生成阴影贴图：短暂切换 shadows 模式再恢复。
        /// 作用于同一帧，不会产生可见闪烁。
        /// </summary>
        private static void ForceShadowMapRebuild(Light light)
        {
            if (light == null || light.type != LightType.Directional) return;
            var old = light.shadows;
            light.shadows = LightShadows.None;
            light.shadows = old;
        }

        private static bool TryParseRenderMode(string value, LightRenderMode fallback, out LightRenderMode mode)
        {
            mode = fallback;
            if (value == "Keep")
                return false;

            try
            {
                mode = (LightRenderMode)System.Enum.Parse(typeof(LightRenderMode), value);
                return true;
            }
            catch
            {
                mode = fallback;
                return true;
            }
        }

        private static void SetDirectionalRenderMode(Light light, LightRenderMode mode)
        {
            if (light == null || light.type != LightType.Directional) return;
            if (light.renderMode == mode) return;

            light.renderMode = mode;
        }

        private static bool ShouldUseShadowProxies()
        {
            return LightingEnhancePlugin.ShadowQualityEnabled.Value
                   && LightingEnhancePlugin.CullingMaskAddChara.Value
                   && LightingEnhancePlugin.UseShadowProxies.Value;
        }

        internal static void MarkShadowProxiesDirty()
        {
            _shadowProxySyncNeeded = true;
            _mapLightRetryFrame = 0;
        }

        internal static bool RetryMapLightShadowApplyIfReady()
        {
            if (!LightingEnhancePlugin.ShadowQualityEnabled.Value)
                return false;

            if (_cachedMapLightDir != null)
                return false;

            if (_mapLightRetryFrame > 0)
            {
                _mapLightRetryFrame--;
                return false;
            }

            _mapLightRetryFrame = MapLightRetryInterval;
            var setup = DetectLightSetup();
            if (setup.MainLight == null)
                return false;

            return OnLightUpdate(refreshShadowProxies: true);
        }

        internal static void SyncShadowProxyStates()
        {
            if (!ShouldUseShadowProxies())
            {
                SyncShadowProxies(force: false);
                return;
            }

            if (ShadowProxies.Count == 0) return;

            var stale = new List<Renderer>();
            foreach (var kv in ShadowProxies)
            {
                var source = kv.Key;
                var entry = kv.Value;
                if (source == null || entry == null || entry.GameObject == null || entry.Renderer == null)
                {
                    stale.Add(source);
                    continue;
                }

                if (!IsShadowProxyCandidate(source))
                {
                    stale.Add(source);
                    continue;
                }

                entry.Renderer.enabled = source.enabled;
                bool active = IsShadowProxyActive(source);
                if (entry.GameObject.activeSelf != active)
                    entry.GameObject.SetActive(active);
            }

            foreach (var source in stale)
                RemoveShadowProxy(source);
        }

        internal static void MarkCameraCharaLightDirty()
        {
            _cachedCameraCharaLight = null;
            _cameraCharaLightRetryFrame = 0;
        }

        internal static void SetCameraCharaLightEuler(CameraImageEffectInfo.LightInfo lightInfo)
        {
            if (lightInfo == null)
                return;

            SetCameraCharaLightEuler(lightInfo.rot);
        }

        internal static void SetCameraCharaLightEuler(Vector2 rot)
        {
            _targetCameraCharaRotation = Quaternion.Euler(rot.x, rot.y, 0f);
            _hasTargetRotation = true;
        }

        internal static bool ApplyCameraCharaLightEulerSync(bool allowResolve)
        {
            // Render callbacks may run once per camera; only the resolving path advances retry cooldown.
            if (allowResolve && _cameraCharaLightRetryFrame > 0)
                _cameraCharaLightRetryFrame--;

            if (!LightingEnhancePlugin.SyncCameraDirectionalLightEulerAngles.Value)
                return true;

            if (!_hasTargetRotation)
                return false;

            var cameraLight = GetCameraCharaLight(allowResolve);
            if (cameraLight == null)
                return false;

            cameraLight.transform.rotation = _targetCameraCharaRotation;
            return true;
        }

        private static Light GetCameraCharaLight(bool allowResolve)
        {
            if (_cachedCameraCharaLight != null)
                return _cachedCameraCharaLight;

            if (!allowResolve || _cameraCharaLightRetryFrame > 0)
                return null;

            _cameraCharaLightRetryFrame = CameraCharaLightRetryInterval;

            var cameraLightGo = GameObject.Find(CameraCharaLightPath);
            if (cameraLightGo == null)
                return null;

            var cameraLight = cameraLightGo.GetComponent<Light>();
            if (cameraLight == null || cameraLight.type != LightType.Directional)
                return null;

            _cachedCameraCharaLight = cameraLight;
            return _cachedCameraCharaLight;
        }

        private static void SyncShadowProxies(bool force)
        {
            if (!ShouldUseShadowProxies())
            {
                ClearShadowProxies();
                _shadowProxySyncNeeded = true;
                return;
            }

            if (!force && !_shadowProxySyncNeeded)
                return;

            var seen = new HashSet<Renderer>();
            var renderers = Object.FindObjectsOfType<Renderer>();
            foreach (var source in renderers)
            {
                if (!IsShadowProxyCandidate(source)) continue;

                seen.Add(source);
                if (!ShadowProxies.TryGetValue(source, out var entry) || entry.GameObject == null || entry.Renderer == null)
                {
                    entry = CreateShadowProxy(source);
                    if (entry == null) continue;
                    ShadowProxies[source] = entry;
                }

                UpdateShadowProxy(source, entry);
            }

            RemoveStaleShadowProxies(seen);
            _shadowProxySyncNeeded = seen.Count == 0;
        }

        private static bool IsShadowProxyCandidate(Renderer source)
        {
            if (source == null) return false;
            if (source.gameObject.layer != CharaLayer) return false;
            if (source.shadowCastingMode == ShadowCastingMode.Off) return false;

            if (source is SkinnedMeshRenderer skinned)
                return skinned.sharedMesh != null;

            if (source is MeshRenderer)
            {
                var filter = source.GetComponent<MeshFilter>();
                return filter != null && filter.sharedMesh != null;
            }

            return false;
        }

        private static ShadowProxyEntry CreateShadowProxy(Renderer source)
        {
            var proxyGo = new GameObject("[EC_LightingEnhance ShadowProxy] " + source.name)
            {
                hideFlags = HideFlags.DontSave,
                layer = MapLayer
            };
            proxyGo.transform.SetParent(source.transform.parent, false);
            CopyTransform(source.transform, proxyGo.transform);

            Renderer proxyRenderer = null;
            if (source is SkinnedMeshRenderer sourceSkinned)
            {
                var proxySkinned = proxyGo.AddComponent<SkinnedMeshRenderer>();
                proxySkinned.sharedMesh = sourceSkinned.sharedMesh;
                proxySkinned.sharedMaterials = sourceSkinned.sharedMaterials;
                proxySkinned.bones = sourceSkinned.bones;
                proxySkinned.rootBone = sourceSkinned.rootBone;
                proxySkinned.localBounds = sourceSkinned.localBounds;
                proxySkinned.quality = sourceSkinned.quality;
                proxySkinned.updateWhenOffscreen = sourceSkinned.updateWhenOffscreen;
                proxyRenderer = proxySkinned;
            }
            else if (source is MeshRenderer sourceMesh)
            {
                var sourceFilter = source.GetComponent<MeshFilter>();
                var proxyFilter = proxyGo.AddComponent<MeshFilter>();
                proxyFilter.sharedMesh = sourceFilter.sharedMesh;

                var proxyMesh = proxyGo.AddComponent<MeshRenderer>();
                proxyMesh.sharedMaterials = sourceMesh.sharedMaterials;
                proxyRenderer = proxyMesh;
            }

            if (proxyRenderer == null)
            {
                Object.Destroy(proxyGo);
                return null;
            }

            proxyRenderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            proxyRenderer.receiveShadows = false;

            return new ShadowProxyEntry
            {
                GameObject = proxyGo,
                Renderer = proxyRenderer
            };
        }

        private static void UpdateShadowProxy(Renderer source, ShadowProxyEntry entry)
        {
            entry.GameObject.layer = MapLayer;
            CopyTransform(source.transform, entry.GameObject.transform);

            entry.Renderer.sharedMaterials = source.sharedMaterials;
            entry.Renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            entry.Renderer.receiveShadows = false;
            entry.Renderer.enabled = source.enabled;

            if (source is SkinnedMeshRenderer sourceSkinned && entry.Renderer is SkinnedMeshRenderer proxySkinned)
            {
                proxySkinned.sharedMesh = sourceSkinned.sharedMesh;
                proxySkinned.bones = sourceSkinned.bones;
                proxySkinned.rootBone = sourceSkinned.rootBone;
                proxySkinned.localBounds = sourceSkinned.localBounds;
                proxySkinned.quality = sourceSkinned.quality;
                proxySkinned.updateWhenOffscreen = sourceSkinned.updateWhenOffscreen;
            }
            else if (source is MeshRenderer && entry.Renderer is MeshRenderer)
            {
                var sourceFilter = source.GetComponent<MeshFilter>();
                var proxyFilter = entry.GameObject.GetComponent<MeshFilter>();
                if (sourceFilter != null && proxyFilter != null)
                    proxyFilter.sharedMesh = sourceFilter.sharedMesh;
            }

            bool active = IsShadowProxyActive(source);
            if (entry.GameObject.activeSelf != active)
                entry.GameObject.SetActive(active);
        }

        private static bool IsShadowProxyActive(Renderer source)
        {
            return source != null
                   && source.enabled
                   && source.gameObject.activeInHierarchy
                   && source.shadowCastingMode != ShadowCastingMode.Off;
        }

        private static void CopyTransform(Transform source, Transform target)
        {
            target.localPosition = source.localPosition;
            target.localRotation = source.localRotation;
            target.localScale = source.localScale;
        }

        private static void RemoveStaleShadowProxies(HashSet<Renderer> seen)
        {
            var stale = new List<Renderer>();
            foreach (var kv in ShadowProxies)
            {
                if (kv.Key == null || kv.Value.GameObject == null || !seen.Contains(kv.Key))
                    stale.Add(kv.Key);
            }

            foreach (var source in stale)
                RemoveShadowProxy(source);
        }

        private static void ClearShadowProxies()
        {
            if (ShadowProxies.Count == 0) return;

            var sources = new List<Renderer>(ShadowProxies.Keys);
            foreach (var source in sources)
                RemoveShadowProxy(source);
        }

        private static void RemoveShadowProxy(Renderer source)
        {
            if (!ShadowProxies.TryGetValue(source, out var entry)) return;

            if (entry.GameObject != null)
                Object.Destroy(entry.GameObject);

            ShadowProxies.Remove(source);
        }

        private static string GetEffectiveMapRenderModeValue()
        {
            var value = LightingEnhancePlugin.MapLightRenderMode.Value;

            if (ShouldUseShadowProxies() && (value == "Keep" || value == "ForceVertex"))
                return "Auto";

            return value;
        }

        internal static void ApplyCharaRenderMode()
        {
            if (!LightingEnhancePlugin.CharaLightPriorityEnabled.Value) return;
            if (!Singleton<HEdit.HEditGlobal>.IsInstance()) return;

            var charaLight = Singleton<HEdit.HEditGlobal>.Instance.lightChara;
            if (TryParseRenderMode(
                LightingEnhancePlugin.CharaLightRenderMode.Value,
                LightRenderMode.ForcePixel,
                out var charaMode))
            {
                SetDirectionalRenderMode(charaLight, charaMode);
            }
        }

        private static void ApplyRenderModes(LightSetup setup)
        {
            ApplyCharaRenderMode();

            if (!LightingEnhancePlugin.CharaLightPriorityEnabled.Value) return;
            if (TryParseRenderMode(
                GetEffectiveMapRenderModeValue(),
                LightRenderMode.Auto,
                out var mapMode))
            {
                SetDirectionalRenderMode(setup.MainLight, mapMode);
                if (setup.MapLightDir != null && setup.MapLightDir != setup.MainLight)
                    SetDirectionalRenderMode(setup.MapLightDir, mapMode);
            }
        }

        /// <summary>
        /// 按配置项覆盖 Directional Light 的阴影参数。
        /// applyCullingMask: 是否允许地图光通过直接 Chara bit 或 shadow proxy 取得角色轮廓。
        /// 禁用时清除已添加的 Chara bit，确保配置即时生效。
        /// </summary>
        internal static void ApplyShadowSettings(Light light, bool applyCullingMask)
        {
            if (light == null) return;

            // 禁用时仅清除已添加的 Chara bit，不做其他修改
            if (!LightingEnhancePlugin.ShadowQualityEnabled.Value)
            {
                if (applyCullingMask)
                    light.cullingMask &= ~(1 << CharaLayer);

                if (applyCullingMask)
                    ForceShadowMapRebuild(light);
                return;
            }

            if (light.type != LightType.Directional) return;

            // 总是先清除 Chara bit，再按配置决定是否添加，确保 true→false 即时生效
            if (applyCullingMask)
                light.cullingMask &= ~(1 << CharaLayer);

            if (LightingEnhancePlugin.ShadowResolution.Value != "FromQualitySettings")
            {
                var resolution = (UnityEngine.Rendering.LightShadowResolution)
                    System.Enum.Parse(typeof(UnityEngine.Rendering.LightShadowResolution),
                        LightingEnhancePlugin.ShadowResolution.Value);
                light.shadowResolution = resolution;
            }

            if (LightingEnhancePlugin.ShadowCustomResolution.Value > 0)
                light.shadowCustomResolution = LightingEnhancePlugin.ShadowCustomResolution.Value;

            light.shadowStrength = LightingEnhancePlugin.ShadowStrength.Value;
            light.shadowBias = LightingEnhancePlugin.ShadowBias.Value;
            light.shadowNormalBias = LightingEnhancePlugin.ShadowNormalBias.Value;
            light.shadowNearPlane = LightingEnhancePlugin.ShadowNearPlane.Value;

            if (applyCullingMask && LightingEnhancePlugin.CullingMaskAddChara.Value)
            {
                if (ShouldUseShadowProxies())
                    light.cullingMask |= (1 << MapLayer);
                else
                    light.cullingMask |= (1 << CharaLayer);
            }

            ForceShadowMapRebuild(light);
        }

        /// <summary>
        /// 光照更新时调用：应用阴影质量设置。
        /// 禁用时仍执行以清除残留的 Chara bit。
        /// 返回 true 表示成功应用，false 表示光照未就绪。
        /// </summary>
        internal static bool OnLightUpdate(bool refreshShadowProxies = false)
        {
            if (!ShouldUseShadowProxies())
                SyncShadowProxies(force: false);

            var setup = DetectLightSetup();
            if (setup.MainLight == null) return false;

            if (setup.MainLight != _lastMainLight || setup.MapLightDir != _lastMapLightDir)
            {
                _lastMainLight = setup.MainLight;
                _lastMapLightDir = setup.MapLightDir;
                MarkShadowProxiesDirty();
            }

            if (ShouldUseShadowProxies())
                SyncShadowProxies(force: refreshShadowProxies);

            if (setup.IsModMap)
            {
                ApplyShadowSettings(setup.MainLight, applyCullingMask: true);

                if (setup.MapLightDir != null && setup.MapLightDir != setup.MainLight)
                    ApplyShadowSettings(setup.MapLightDir, applyCullingMask: true);
            }
            else
            {
                ApplyShadowSettings(setup.MainLight, applyCullingMask: true);
            }

            ApplyRenderModes(setup);
            return true;
        }

        /// <summary>
        /// 配置变更时强制完整应用：先同步游戏光到 Sunlight，再覆盖阴影参数。
        /// 返回 true 表示成功应用，false 表示光照未就绪需下帧重试。
        /// </summary>
        internal static bool ForceApply()
        {
            Light gameLight = null;

            if (Singleton<HEdit.HEditGlobal>.IsInstance())
                gameLight = Singleton<HEdit.HEditGlobal>.Instance.lightMap;

            if (gameLight == null)
            {
                var setup = DetectLightSetup();
                gameLight = setup.MapLightDir ?? setup.MainLight;
            }

            if (gameLight != null)
                SyncToSunlight(gameLight);

            return OnLightUpdate(refreshShadowProxies: true);
        }
    }
}
