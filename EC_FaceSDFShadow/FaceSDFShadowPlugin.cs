using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KKAPI;
using KKAPI.Chara;
using UnityEngine;

namespace EC_FaceSDFShadow
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInDependency("com.bepis.bepinex.extendedsave", BepInDependency.DependencyFlags.HardDependency)]
    // 逐角色 SDF 目录靠 EC_Profile 写在卡上的描述文本指定；未装时读不到文本、静默回全局，故为软依赖。
    // 声明它只为保证 Profile 先加载完（其扩展数据由 ESF 读，本插件不引用其程序集）。
    [BepInDependency("com.deathweasel.bepinex.profile", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(KoikatuAPI.GUID, KoikatuAPI.VersionConst)]
    public class FaceSDFShadowPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_FaceSDFShadow";
        public const string PluginName = "Face SDF Shadow";
        public const string Version = "1.0.0";

        /// 数据根目录：UserData/PluginData/EC_FaceSDFShadow。
        /// 手绘帧(纯数字 png)、SDF.png、逐角色目录、诊断导出统一放这里，
        /// 与 BepInEx/config 分离（config 只放配置，不放资产）。
        internal static string DataDir =>
            Path.Combine(BepInEx.Paths.GameRootPath, "UserData", "PluginData", GUID);

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<UnityEngine.KeyCode> ColorMixerKey;
        internal static ConfigEntry<bool> NeutralizeFaceRamp;
        internal static ConfigEntry<Color> ShadowColor;
        internal static ConfigEntry<float> SoftnessAngle;
        internal static ConfigEntry<float> ThresholdBias;
        internal static ConfigEntry<float> FaceRealtimeShadowG;
        internal static ConfigEntry<int> RenderQueue;

        // 手绘 SDF（唯一的阈值图来源）
        internal static ConfigEntry<bool> ManualContourInterpolation;
        internal static ConfigEntry<float> ManualSDFBlurSigma;
        internal static ConfigEntry<float> ManualEndpointSnapDegrees;
        // 颈带 N·L 复制品标定（路线 1.1）：SDF 窗口 yaw-only、body 侧着色响应完整光向，
        // 面身缝两侧阴影 terminator 不同步。复制品分支在颈带内用世界 N·L 过软硬可调的
        // smoothstep 出阴影量，再走与 SDF 分支同款的 _ShadowColor 混色——不查 ramp
        // 纹理（ramp 暗端纯黑会把复制品压成全黑），颜色与 02_ShadowColor 同步。
        internal static ConfigEntry<float> NeckRampScale;
        internal static ConfigEntry<float> NeckRampBias;
        internal static ConfigEntry<float> NeckEdgeSoftness;
        internal static ConfigEntry<float> NeckBandTopV;

        // 表情 UV 补偿（分区 affine，见 FaceBlendCompensation）
        internal static ConfigEntry<bool> BlendCompensation;
        internal static ConfigEntry<bool> BlendCompMouth;
        internal static ConfigEntry<float> BlendCompDefClBase;
        internal static ConfigEntry<float> BlendCompGain;
        internal static ConfigEntry<bool> BlendCompLive;

#if DEBUG
        // DEBUG 构建诊断快捷键 + 一次性导出标志（Release 不注册、不进配置面板）
        internal static ConfigEntry<KeyboardShortcut> ExportPngKey;      // 导出烘焙阈值图 PNG
        internal static ConfigEntry<KeyboardShortcut> DumpMaterialKey;   // 材质属性诊断
        internal static ConfigEntry<KeyboardShortcut> BlendShapeDumpKey; // 枚举面部 BlendShape 通道
        internal static ConfigEntry<KeyboardShortcut> BlendCompDumpKey;  // 表情 UV 补偿累加诊断
        internal static ConfigEntry<KeyboardShortcut> BlendCompVerifyKey; // BakeMesh 分离 blendshape/骨骼贡献
        internal static bool ExportPngOnce;                             // 一次性导出标志，烘焙时消费
#endif

        private int _pollCounter;
        private const int PollInterval = 15;
        private bool _wasEnabled = true;

        internal void Awake()
        {
            Log = Logger;

            // 注册 CharaController（管理逐角色配置，存储到角色卡扩展数据）
            CharacterApi.RegisterExtraBehaviour<FaceSDFShadowController>(GUID);

            // 头骨矩阵推送时机：见 OnPreCull 注释（LateUpdate 会读到 NeckLook 写入前的姿势）
            Camera.onPreCull += OnPreCull;

#if DEBUG
            // DEBUG 诊断快捷键：保存 Config.Bind 返回值，运行时用 .Value.IsDown() 读取，
            // 修复旧 DiagnosticKey 丢弃返回值导致配置修改无效的问题。
            ExportPngKey = Config.Bind("Debug", "ExportPngKey",
                new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
                "Hotkey: bake + export SDF threshold PNGs for all face meshes.");
            DumpMaterialKey = Config.Bind("Debug", "DumpMaterialKey",
                new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl),
                "Hotkey: dump face material texture properties to log.");
            BlendShapeDumpKey = Config.Bind("Debug", "BlendShapeDumpKey",
                new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl),
                "Hotkey: dump face mesh BlendShape channels (index/name/weight) to log.");
            BlendCompDumpKey = Config.Bind("Debug", "BlendCompDumpKey",
                new KeyboardShortcut(KeyCode.F11, KeyCode.LeftControl),
                "Hotkey: dump the next frame's blend-compensation accumulation (non-zero " +
                "channel weights + per-region affine displacement) to log.");
            BlendCompVerifyKey = Config.Bind("Debug", "BlendCompVerifyKey",
                new KeyboardShortcut(KeyCode.F12, KeyCode.LeftControl),
                "Hotkey: BakeMesh verify on the first character - separates blendshape-only " +
                "vertex displacement from bone/other-system displacement, per region, to log.");
#endif

            Enabled = Config.Bind(
                "General", "00_Enabled", true,
                "Master switch. Turning off restores original face materials.");

            NeutralizeFaceRamp = Config.Bind(
                "General", "01_NeutralizeFaceRamp", true,
                "Override the face material's ramp with a white texture, so the game's built-in " +
                "ramp shading no longer darkens the face. Body and hair keep the global ramp. " +
                "Turn this off if you want the SDF shadow layered on top of the original shading.");

            ShadowColor = Config.Bind(
                "General", "02_ShadowColor",
                new Color(0.79f, 0.43f, 0f, 0.27f),
                "Shadow tint applied to all characters that have NOT customized this property " +
                "in MaterialEditor. RGB is multiplied onto the face; alpha controls how strongly " +
                "the tint is applied. Takes effect immediately on change (also outside the maker). " +
                "Once a card's shadow color is edited via MaterialEditor (or the color mixer), " +
                "that card locks its own value and is no longer affected by this setting.");

            ThresholdBias = Config.Bind(
                "General", "04_ThresholdBias", 0f,
                new ConfigDescription(
                    "Light/shadow boundary shift for all characters that have NOT customized " +
                    "this property in MaterialEditor. Positive = less shadow. Takes effect " +
                    "immediately on change; ME-edited cards keep their own value.",
                    new AcceptableValueRange<float>(-1f, 1f)));

            SoftnessAngle = Config.Bind(
                "General", "05_SoftnessAngle", 3f,
                new ConfigDescription(
                    "Shadow edge softness in DEGREES for all characters that have NOT " +
                    "customized this property in MaterialEditor. Constant in angle space: the " +
                    "transition band keeps the same width regardless of camera distance (the old " +
                    "screen-pixel scheme revealed hand-drawn frame C1 kinks as white lines when " +
                    "zoomed out). 0 = hard edge; larger = wider/softer terminator. Takes effect " +
                    "immediately on change; ME-edited cards keep their own value.",
                    new AcceptableValueRange<float>(0f, 30f)));

            FaceRealtimeShadowG = Config.Bind(
                "General", "07_FaceRealtimeShadowG", 1f,
                new ConfigDescription(
                    "Exclude the face's own realtime self-shadow via the shader's _FaceShadowG " +
                    "intensity (user-tested: scalar, 0 = original, 1 = own self-shadow gone " +
                    "while hair shadow on the face only slightly fades). Middle values attenuate " +
                    "the self-shadow partially. Only affects the face material instance; " +
                    "body/hair keep their shadows. Default 1 = exclude (the SDF shadow layer " +
                    "replaces the face's realtime self-shadow).",
                    new AcceptableValueRange<float>(0f, 1f)));

            ColorMixerKey = Config.Bind(
                "Advanced", "ColorMixerKey", UnityEngine.KeyCode.F7,
                "Single key that toggles the shadow color mixer window, only active in the " +
                "character maker (CustomScene). The mixer reverse-solves the overlay " +
                "_ShadowColor from two picked colors: the vanilla shadowed skin color S and " +
                "the current lit skin base P, then logs the result for copy-paste. " +
                "See the workflow text inside the window.");

            RenderQueue = Config.Bind(
                "Advanced", "RenderQueue", 2360,
                new ConfigDescription(
                    "Render queue of the overlay pass. EC's face material sits at 2350, " +
                    "so this must be higher or the overlay stays invisible.",
                    new AcceptableValueRange<int>(2000, 3500)));

            ManualContourInterpolation = Config.Bind(
                "StructuredSDF", "ManualContourInterpolation", true,
                "Contour/SDF sub-frame interpolation for the complete set of manual angle " +
                "frames. Hand-authored sets are typically ~9 frames, where the raw per-frame " +
                "thresholds sweep visibly in steps, so this defaults ON. It does not affect " +
                "external SDF.png. Invalid contour input falls back to the raw frame thresholds.");

            ManualSDFBlurSigma = Config.Bind(
                "StructuredSDF", "ManualSDFBlurSigma", 2f,
                new ConfigDescription(
                    "Spatial Gaussian blur sigma (in texels) applied to the manual SDF threshold " +
                    "field before writing the RGBAHalf map. Smooths the C1-discontinuous steps at " +
                    "hand-drawn frame edges, removing the aliasing/white-line that shader-side AA " +
                    "cannot fix. 0 = disabled. Larger = smoother but shifts contours outward. " +
                    "2 is the tested default.",
                    new AcceptableValueRange<float>(0f, 8f)));

            ManualEndpointSnapDegrees = Config.Bind(
                "StructuredSDF", "ManualEndpointSnapDegrees", 1.5f,
                new ConfigDescription(
                    "Clamp the light sweep angle away from both endpoints (front 0 deg / " +
                    "back 180 deg) by this many degrees. At the exact endpoints the visible " +
                    "shadow edge is the zero-distance iso-curve riding the raw hand-drawn " +
                    "contour staircase (polyline edge); clamping displays the already-smooth " +
                    "1-degree-off shading instead. Side effect: the shadow shape freezes for " +
                    "about 2x this many degrees while the light sweeps across front/back. " +
                    "0 = disabled.",
                    new AcceptableValueRange<float>(0f, 5f)));

            NeckRampScale = Config.Bind(
                "StructuredSDF", "NeckRampScale", 0.82f,
                new ConfigDescription(
                    "Neck band N.L replica: scale on the half-lambert light term before the " +
                    "shadow smoothstep (t = NdotL01 * scale + bias; negative flips which side " +
                    "is lit). Calibrate against the body-side neck shading in game. Pure display " +
                    "parameter: takes effect immediately, no re-bake.",
                    new AcceptableValueRange<float>(-2f, 2f)));

            NeckRampBias = Config.Bind(
                "StructuredSDF", "NeckRampBias", 0f,
                new ConfigDescription(
                    "Neck band N.L replica: bias added to the scaled light term before the " +
                    "shadow smoothstep (see NeckRampScale). Calibrate against the body-side " +
                    "neck shading in game. Pure display parameter: takes effect immediately, " +
                    "no re-bake.",
                    new AcceptableValueRange<float>(-1f, 1f)));

            NeckEdgeSoftness = Config.Bind(
                "StructuredSDF", "NeckEdgeSoftness", 0.008f,
                new ConfigDescription(
                    "Neck band N.L replica: half-width w of the shadow edge smoothstep " +
                    "(s = smoothstep(0.5 - w, 0.5 + w, light term)). 0 = hard edge; larger = " +
                    "softer/wider terminator. The shadow multiplier itself uses the same " +
                    "_ShadowColor formula as the face SDF branch, so the color always follows " +
                    "02_ShadowColor. Pure display parameter: takes effect immediately, no re-bake.",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            NeckBandTopV = Config.Bind(
                "StructuredSDF", "NeckBandTopV", 0.2f,
                new ConfigDescription(
                    "Neck band crossfade: the V coordinate where the band top blends fully back " +
                    "to the pure face SDF window (band bottom 0.05 stays a pure N.L replica). " +
                    "Raise this to cover leftover static SDF shapes on both sides of the jaw " +
                    "and to narrow the vertical gradient. Pure display parameter: takes effect " +
                    "immediately, no re-bake.",
                    new AcceptableValueRange<float>(0.1f, 0.5f)));

            BlendCompensation = Config.Bind(
                "BlendCompensation", "BlendCompensation", true,
                "Counter skin-movement of the SDF threshold pattern during expressions " +
                "(blink/squint/wink pull the shadow pattern with the face skin). Drives a 5-region " +
                "affine UV offset from blendshape weights each frame. Default ON (user-validated); " +
                "requires the rebuilt shader bundle.");

            BlendCompMouth = Config.Bind(
                "BlendCompensation", "BlendCompMouth", true,
                "Also apply the mouth-region affine (user-validated: mouth moves with the skin " +
                "well enough with it ON). Offline fitting shows the mouth region is only weakly " +
                "corrected by an affine (residual ~= original), so this stays as an " +
                "observation/rollback switch; cheek and eye regions are always compensated when " +
                "the master switch is on.");

            BlendCompDefClBase = Config.Bind(
                "BlendCompensation", "DefClBase", 0f,
                new ConfigDescription(
                    "Baseline weight of the def_cl (eyelid-close) blendshape subtracted before " +
                    "accumulating compensation. 0 = neutral pose is fully-open eyes (default, " +
                    "matches the offline fit). If the threshold map was authored at the game's " +
                    "'eye open 1' pose (def_cl=23/def_op=77) and a static offset is observed on " +
                    "the neutral face, set this to 23 to re-zero it.",
                    new AcceptableValueRange<float>(0f, 100f)));

            BlendCompGain = Config.Bind(
                "BlendCompensation", "Gain", 0.6f,
                new ConfigDescription(
                    "Multiplier on the compensation magnitude. 0.6 is the user-tested optimum " +
                    "after the 2026-08-16 Gram-projection fix (1.0 = full offline-fitted " +
                    "magnitude, slightly overshooting on screen). Lower if the pattern moves " +
                    "against the skin; raise towards 1 if still dragging.",
                    new AcceptableValueRange<float>(0f, 4f)));

            BlendCompLive = Config.Bind(
                "BlendCompensation", "LiveMode", true,
                "Runtime-measured compensation: per-frame BakeMesh against a neutral reference " +
                "captured whenever the face is expressionless (stable for ~10 frames; the eye/" +
                "mouth def interpolation channels stay exempt, so the neutral closed-mouth pose " +
                "counts). Correct for any head mesh and any driver (blendshape, FBSAssist cheek " +
                "animation, bones, third-party). Until a reference is captured the offline " +
                "table fills in (blink/wink already fine there). No manual step needed: the " +
                "reference builds automatically the first time the character is expressionless, " +
                "which the chara-maker default already is.");

            // 只影响显示的参数：改了直接推材质。
            // 02/04/05 也订阅：v0.10 起持续生效于所有"未在 ME 定制该属性"的角色
            // （定制卡由哨兵检测锁存、不受全局影响），scene 里没有 ME 界面也能调。
            NeutralizeFaceRamp.SettingChanged += OnDisplayChanged;
            ShadowColor.SettingChanged += OnDisplayChanged;
            ThresholdBias.SettingChanged += OnDisplayChanged;
            SoftnessAngle.SettingChanged += OnDisplayChanged;
            FaceRealtimeShadowG.SettingChanged += OnDisplayChanged;
            RenderQueue.SettingChanged += OnDisplayChanged;
            ManualContourInterpolation.SettingChanged += OnStructuredParamChanged;
            ManualSDFBlurSigma.SettingChanged += OnStructuredParamChanged;
            // 端点钳位是纯显示参数（shader uniform），不触发阈值图重烘焙
            ManualEndpointSnapDegrees.SettingChanged += OnDisplayChanged;
            // 颈带 N·L 复制品标定同样是纯显示参数（shader uniform），不触发重烘焙
            NeckRampScale.SettingChanged += OnDisplayChanged;
            NeckRampBias.SettingChanged += OnDisplayChanged;
            NeckEdgeSoftness.SettingChanged += OnDisplayChanged;
            NeckBandTopV.SettingChanged += OnDisplayChanged;

            // 反算工具窗：常驻 MonoBehaviour（DontDestroyOnLoad），Ctrl+快捷键呼出
            ShadowColorMixer.Create();

            Log.LogInfo($"{PluginName} v{Version} started.");
        }

        private static void OnDisplayChanged(object sender, System.EventArgs e)
        {
            FaceOverlayCore.PushConfigAll();
        }

        private static void OnStructuredParamChanged(object sender, System.EventArgs e)
        {
            FaceStructuredSDFBaker.InvalidateAll();
            FaceOverlayCore.PushConfigAll();
        }

#if DEBUG
        private void DiagnoseDumpFaceMaterialProperties()
        {
            Log.LogWarning("=== Face Material Texture Properties Diagnostic ===");

            if (!Manager.Character.IsInstance())
            {
                Log.LogWarning("Manager.Character not ready");
                return;
            }

            var dict = Manager.Character.Instance.dictEntryChara;
            if (dict == null || dict.Count == 0)
            {
                Log.LogWarning("No characters in scene");
                return;
            }

            foreach (var cha in dict.Values)
            {
                if (cha == null) continue;

                var smr = cha.rendFace as SkinnedMeshRenderer;
                if (smr == null) continue;

                var mats = smr.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                Log.LogWarning($"Character: {cha.fileParam?.fullname ?? "Unknown"}");

                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;

                    Log.LogWarning($"  Material[{i}]: {mat.name} (shader: {mat.shader?.name})");

                    // 尝试读取常见的 ramp 相关属性
                    string[] rampProps = { "_RampG", "_Ramp", "_RampTex", "_ToonRamp", "_ShadowRamp" };
                    foreach (var propName in rampProps)
                    {
                        try
                        {
                            var tex = mat.GetTexture(propName);
                            if (tex != null)
                                Log.LogWarning($"    Texture: {propName} = {tex.name}");
                        }
                        catch (System.Exception ex)
                        {
                            Log.LogWarning($"    {propName}: FAILED - {ex.Message}");
                        }
                    }

                    // 特别检查 ChaShader._RampG 的值
                    Log.LogWarning($"    [ChaShader._RampG ID] = {ChaShader._RampG}");
                    try
                    {
                        var rampG = mat.GetTexture(ChaShader._RampG);
                        Log.LogWarning($"    [GetTexture via ChaShader._RampG] = {rampG?.name ?? "null"}");
                    }
                    catch (System.Exception ex)
                    {
                        Log.LogWarning($"    [GetTexture via ChaShader._RampG] FAILED: {ex.Message}");
                    }

                    // 检查 ChaShader.texRamp 全局值
                    Log.LogWarning($"    [ChaShader.texRamp global] = {ChaShader.texRamp?.name ?? "null"}");
                }

                break; // 只诊断第一个角色
            }

            Log.LogWarning("=== End Diagnostic ===");
        }
#endif

#if DEBUG
        /// <summary>
        /// 诊断键边沿触发：KeyboardShortcut.IsPressed() 实测按一下会连发（刷屏），
        /// 统一改用 IsDown() + 上一帧状态自建上升沿。
        /// </summary>
        private static readonly Dictionary<ConfigEntry<KeyboardShortcut>, bool> _keyEdge =
            new Dictionary<ConfigEntry<KeyboardShortcut>, bool>();

        private static bool EdgePressed(ConfigEntry<KeyboardShortcut> key)
        {
            bool down = key.Value.IsDown();
            bool was = _keyEdge.TryGetValue(key, out var prev) && prev;
            _keyEdge[key] = down;
            return down && !was;
        }
#endif

        internal void Update()
        {
#if DEBUG
            // DEBUG 诊断快捷键（自建上升沿，按一下只触发一次）。
            if (EdgePressed(ExportPngKey))
                TriggerExportPng();
            if (EdgePressed(DumpMaterialKey))
                DiagnoseDumpFaceMaterialProperties();
            if (EdgePressed(BlendShapeDumpKey))
                FaceBlendShapeDump.Run();
            if (EdgePressed(BlendCompDumpKey))
            {
                FaceBlendCompensation.DumpOnce = true;
                // live 状态同帧直出（不等 Push 消费，诊断参照建立卡点）
                if (Manager.Character.IsInstance())
                {
                    var dictD = Manager.Character.Instance.dictEntryChara;
                    if (dictD != null)
                    {
                        foreach (var cha in dictD.Values)
                        {
                            if (cha == null) continue;
                            FaceLiveCompensation.DumpState(cha.rendFace as SkinnedMeshRenderer);
                            break;
                        }
                    }
                }
            }
            if (EdgePressed(BlendCompVerifyKey) && Manager.Character.IsInstance())
            {
                var dict0 = Manager.Character.Instance.dictEntryChara;
                if (dict0 != null)
                {
                    foreach (var cha in dict0.Values)
                    {
                        if (cha == null) continue;
                        var smr = cha.rendFace as SkinnedMeshRenderer;
                        if (smr == null || smr.sharedMesh == null) continue;
                        FaceBlendCompensation.VerifyBake(smr);
                        break; // 只验证第一个角色
                    }
                }
            }
#endif

            bool enabled = Enabled.Value;

            if (!enabled && _wasEnabled)
                FaceOverlayCore.RestoreAll();
            _wasEnabled = enabled;

            if (!enabled) return;

            if (++_pollCounter >= PollInterval)
            {
                _pollCounter = 0;
                // ME 属性登记延迟到此处：ME 多半在游戏首帧后才 LoadXML，
                // 每轮尝试一次直到成功（幂等），成功后永不重进。
                MaterialEditorRegistry.TryRegister();
                FaceOverlayCore.Poll();
            }
        }

        /// onPreCull 每台相机都会触发（主相机 / UI 相机 / 反射探针），同帧重复推送是白费开销。
        private static int _lastPushedFrame = -1;

        /// <summary>
        /// 把头骨矩阵推给 overlay 材质，让面部阴影跟随头部旋转。
        ///
        /// 必须挂 Camera.onPreCull，不能用 LateUpdate：头部朝向由 NeckLookControllerVer2
        /// 在 LateUpdate 阶段写入骨骼（ChaControl.cs:354 ForceLateUpdate），插件的 LateUpdate
        /// 与它同阶段、执行顺序不确定，实测读到的是 NeckLook 写入前的姿势——表现为
        /// 转 root（父级 transform，任何时候读都已生效）跟随，但转头/设置头朝向不跟随。
        /// onPreCull 在所有 LateUpdate 之后、渲染之前触发，保证拿到最终骨骼姿势。
        /// 同类做法见 IllusionFixes 的 EyebrowFix。
        /// </summary>
        private static void OnPreCull(Camera cam)
        {
            if (!Enabled.Value) return;

            int frame = Time.frameCount;
            if (_lastPushedFrame == frame) return;
            _lastPushedFrame = frame;

            FaceOverlayCore.PushHeadMatrices();
        }

#if DEBUG
        /// <summary>
        /// 快捷键触发：清烘焙缓存后重新烘焙所有角色，烘焙时消费 ExportPngOnce 导出 PNG。
        /// PushConfigAll 同步烘焙，故返回后可立即复位标志。
        /// </summary>
        private static void TriggerExportPng()
        {
            if (!Manager.Character.IsInstance()) return;

            ExportPngOnce = true;
            FaceStructuredSDFBaker.InvalidateAll();
            FaceOverlayCore.PushConfigAll();
            ExportPngOnce = false;

            Log.LogInfo("Exported baked SDF threshold PNGs to " + DataDir);
        }
#endif

        internal void OnDestroy()
        {
            Camera.onPreCull -= OnPreCull;
            FaceOverlayCore.RestoreAll();
            FaceStructuredSDFBaker.Dispose();
        }
    }
}
