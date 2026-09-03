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
    /// <summary>
    /// 发影形态（HairShadow 01_Form）：ConfigurationManager 按枚举名出下拉，
    /// 数值即推给 shader 的 _HairShadowForm。
    /// </summary>
    internal enum HairShadowForm
    {
        ScreenSpace = 0,
        LightSpace = 1,
    }

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
        public const string Version = "2.0.0";

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

        // 显示模式一键预设(Advanced):一次写入四项显示参数,走各自既有生效链
        internal static ConfigEntry<string> DisplayPreset;

        // 手绘 SDF（唯一的阈值图来源）
        internal static ConfigEntry<bool> ManualContourInterpolation;
        internal static ConfigEntry<float> ManualSDFBlurSigma;
        internal static ConfigEntry<float> ManualEndpointSnapDegrees;
        // 颈带 N·L 复制品标定：SDF 窗口 yaw-only、body 侧着色响应完整光向，
        // 面身缝两侧阴影 terminator 不同步。复制品分支在颈带内用世界 N·L 过软硬可调的
        // smoothstep 出阴影量，再走与 SDF 分支同款的 _ShadowColor 混色——不查 ramp
        // 纹理（ramp 暗端纯黑会把复制品压成全黑），颜色与 02_ShadowColor 同步。
        internal static ConfigEntry<float> NeckRampScale;
        internal static ConfigEntry<float> NeckRampBias;
        internal static ConfigEntry<float> NeckEdgeSoftness;
        internal static ConfigEntry<float> NeckBandTopV;
        // 复制品压黑上限：自阴影开+底光时复制品全黑会叠在引擎 receive_shadows
        // 投影上成双层全黑分割线（下颚→颈上段）。调低上限只减淡这层压黑、原生投影
        // 层次透出来，非逐方向补丁；默认 1 = 与不设限逐位一致。
        internal static ConfigEntry<float> NeckReplicaCap;
        // 真实阴影消费权重：底色由发影总开关清干净后，复制品采样屏幕空间
        // 阴影图、与 N·L 合成单个光能（与 body 单次着色同构）；0 = 独立判光。
        // 全程乘算、因子 ≤1——除法补偿会因 HDR 缓冲不钳位过驱泛白。
        internal static ConfigEntry<float> NeckShadowCompensation;
        // 发影总开关（00）：只管发影（HairShadowPass 活跃门控 + 发影权重推 1/0）。
        // 脖颈干净底色（receiveShadows）不跟本项——那是颈带复制品正确工作的前提，
        // 改由 General 00_Enabled 主开关直接驱动。
        internal static ConfigEntry<bool> ShadowEnabled;
        // 脸自身投影源切除（Advanced）：屏幕空间阴影图分不出投影来源，脸自投影
        // 回来是纯黑，只能从源头切 cast（外部投影保留）。代价：LightingEnhance
        // 地面 proxy 缺脸部贡献。ShadowsOnly 会使 renderer 整个隐形，禁止使用。
        internal static ConfigEntry<bool> FaceNoSelfCast;

        // 发影 X 移动量程(02,米):光驱动的影带水平位移上限(百分比映射,135° 满程)。
        internal static ConfigEntry<float> HairShadowShiftX;

        // 发影 Y 移动量程(03,米):竖直位移上限,保持小于 02 才有动画观感。
        internal static ConfigEntry<float> HairShadowShiftY;

        // 发影初始 X/Y 偏移(04/05,米):不依赖光向的影带基础位移,负值可反侧。
        internal static ConfigEntry<float> HairShadowBaseX;
        internal static ConfigEntry<float> HairShadowBaseY;

        // 发影软边(06,0..1)：形态 A 0=1×RT 单 tap、>0=2× 超采样 RT+
        // 3×3 软核(间距 v×3px);形态 B=PCF 块间距 v×3texel。RT 倍率跨 0 才重建。
        internal static ConfigEntry<float> HairShadowSoft;

        // 发影形态(01):ScreenSpace=屏幕位移/LightSpace=光空间正交投影。
        // 改档触发重建(签名含形态)。
        internal static ConfigEntry<HairShadowForm> HairShadowForm;

        // 光空间形态的 RT 边长(07):档位越高边缘越细腻,显存代价平方增长。
        internal static ConfigEntry<int> HairShadowRes;

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
        internal static ConfigEntry<KeyboardShortcut> HairRTViewKey;     // 全屏回显 hair mask RT
        internal static bool ExportPngOnce;                             // 一次性导出标志，烘焙时消费
#endif

        private int _pollCounter;
        private const int PollInterval = 15;
        private bool _wasEnabled = true;

        /// <summary>显示模式预设的参数组(标定于阴影密度 0.55)。</summary>
        private struct DisplayPresetEntry
        {
            internal string Name;
            internal float SoftnessAngle;
            internal float NeckEdgeSoftness;
            internal float NeckRampScale;
            internal float HairShadowSoft;
        }

        private static readonly DisplayPresetEntry[] DisplayPresets =
        {
            new DisplayPresetEntry { Name = "light smooth",    SoftnessAngle = 5f,  NeckEdgeSoftness = 0.088f,  NeckRampScale = 0.78f,  HairShadowSoft = 0.3f },
            new DisplayPresetEntry { Name = "light hard",      SoftnessAngle = 0f,  NeckEdgeSoftness = 0.008f,  NeckRampScale = 0.819f, HairShadowSoft = 0.0f },
            new DisplayPresetEntry { Name = "depth smooth",    SoftnessAngle = 0f,  NeckEdgeSoftness = 0.1866f, NeckRampScale = 0.7f,   HairShadowSoft = 0.8f },
            new DisplayPresetEntry { Name = "depth hard",      SoftnessAngle = 0f,  NeckEdgeSoftness = 0.001f,  NeckRampScale = 0.664f, HairShadowSoft = 0.0f },
            new DisplayPresetEntry { Name = "absolute smooth", SoftnessAngle = 30f, NeckEdgeSoftness = 0.137f,  NeckRampScale = 0.65f,  HairShadowSoft = 1f  },
        };

        internal void Awake()
        {
            Log = Logger;

            // 注册 CharaController（管理逐角色配置，存储到角色卡扩展数据）
            CharacterApi.RegisterExtraBehaviour<FaceSDFShadowController>(GUID);

            // 头骨矩阵推送时机：见 OnPreCull 注释（LateUpdate 会读到 NeckLook 写入前的姿势）
            Camera.onPreCull += OnPreCull;

#if DEBUG
            // DEBUG 诊断快捷键：必须保存 Config.Bind 返回值，运行时用 .Value.IsDown()
            // 读取；丢弃返回值会使配置修改无效。
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
            HairRTViewKey = Config.Bind("Debug", "HairRTViewKey",
                new KeyboardShortcut(KeyCode.F6, KeyCode.LeftControl),
                "Hotkey: toggle full-screen display of the hair mask RT.");
#endif

            Enabled = Config.Bind(
                "General", "00_Enabled", true,
                "Master switch. Off restores the original face materials and the " +
                "face renderer's shadow state.");

            NeutralizeFaceRamp = Config.Bind(
                "General", "01_NeutralizeFaceRamp", true,
                "Override the face material's ramp with a white texture so the " +
                "built-in ramp shading no longer darkens the face; body and hair " +
                "keep the global ramp. Off = the SDF shadow layers on top of the " +
                "original shading.");

            ShadowColor = Config.Bind(
                "General", "02_ShadowColor",
                new Color(0.79f, 0.43f, 0f, 0.27f),
                "Shadow tint for characters that have not customized it in " +
                "MaterialEditor. RGB multiplies onto the face, alpha is the " +
                "strength. Cards edited via MaterialEditor or the color mixer " +
                "lock their own value.");

            ThresholdBias = Config.Bind(
                "General", "04_ThresholdBias", 0f,
                new ConfigDescription(
                    "Light/shadow boundary shift for characters that have not " +
                    "customized it in MaterialEditor. Positive = less shadow.",
                    new AcceptableValueRange<float>(-1f, 1f)));

            SoftnessAngle = Config.Bind(
                "General", "05_SoftnessAngle", 3f,
                new ConfigDescription(
                    "Shadow edge softness in degrees for characters that have not " +
                    "customized it in MaterialEditor; constant in angle space " +
                    "regardless of camera distance. 0 = hard edge, larger = softer.",
                    new AcceptableValueRange<float>(0f, 30f)));

            FaceRealtimeShadowG = Config.Bind(
                "General", "07_FaceRealtimeShadowG", 1f,
                new ConfigDescription(
                    "Strength of excluding the face's own realtime self-shadow " +
                    "via the face material's _FaceShadowG. 0 = original, 1 = " +
                    "fully excluded, middle values attenuate. Only affects the " +
                    "face material instance; body and hair keep their shadows.",
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

            DisplayPreset = Config.Bind(
                "Advanced", "DisplayPreset", "light smooth",
                new ConfigDescription(
                    "One-click preset for the game's display mode (sets " +
                    "SoftnessAngle, NeckEdgeSoftness, NeckRampScale, 06_Soft); " +
                    "calibrated at shadow density 0.55.",
                    new AcceptableValueList<string>(
                        "light smooth", "light hard", "depth smooth",
                        "depth hard", "absolute smooth")));

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

            NeckReplicaCap = Config.Bind(
                "StructuredSDF", "NeckReplicaCap", 1f,
                new ConfigDescription(
                    "Neck band N.L replica: cap on how dark the replica may multiply. With " +
                    "self-shadow ON and light from below, the un-capped replica goes full black " +
                    "on top of the engine's receive_shadows projection and forms a double-black " +
                    "separation line (jaw to upper neck). Lower this to only soften that layer " +
                    "so the native projection shows through. Not a per-direction patch: light " +
                    "from above/side keeps 1-neckLit below the cap and is unaffected. 1 = " +
                    "byte-identical to no cap. Pure display parameter: takes effect " +
                    "immediately, no re-bake.",
                    new AcceptableValueRange<float>(0f, 1f)));

            NeckShadowCompensation = Config.Bind(
                "StructuredSDF", "NeckShadowCompensation", 1f,
                new ConfigDescription(
                    "Neck band N.L replica real-shadow consumption weight. " +
                    "1 = the replica samples the screen-space shadow map and folds the " +
                    "attenuation into the light term BEFORE the shadow curve (same " +
                    "single-pass composition as the body). Requires the clean base " +
                    "(00_Enabled): the face " +
                    "base renderer must not carry its own native projection copy, otherwise " +
                    "this only stacks on top of it (division compensation cannot cancel it " +
                    "without overdriving the unclamped HDR buffer). 0 = legacy composition (N.L " +
                    "judged alone). With self-shadow off the shadow map is unbound and C# " +
                    "gates it off (_ShadowmapAvail=0), making both compositions identical. " +
                    "All factors stay <= 1; no division, no overdrive. Pure display parameter.",
                    new AcceptableValueRange<float>(0f, 1f)));

            // ---- HairShadow 节 ----
            ShadowEnabled = Config.Bind(
                "HairShadow", "00_ShadowEnabled", true,
                "Hair-only shadow master switch. Off stops the hair shadow and " +
                "releases its render textures. The face clean-base shading does " +
                "not follow this entry; it follows the main 00_Enabled switch.");

            HairShadowForm = Config.Bind(
                "HairShadow", "01_Form",
                // 字段与 enum 同名遮蔽,默认值须全限定
                EC_FaceSDFShadow.HairShadowForm.ScreenSpace,
                new ConfigDescription(
                    "Hair shadow projection form: ScreenSpace = screen-space " +
                    "displacement (02~05 apply); LightSpace = hair-only ortho " +
                    "shadow map that follows face relief and light rotation " +
                    "(06/07 apply). Changing it rebuilds the mask RT."));

            HairShadowShiftX = Config.Bind(
                "HairShadow", "02_SS_ShiftX", 0.01f,
                new ConfigDescription(
                    "ScreenSpace form: maximum light-driven horizontal shadow " +
                    "travel in meters.",
                    new AcceptableValueRange<float>(0f, 0.1f)));

            HairShadowShiftY = Config.Bind(
                "HairShadow", "03_SS_ShiftY", 0.008f,
                new ConfigDescription(
                    "ScreenSpace form: maximum light-driven vertical shadow " +
                    "travel in meters.",
                    new AcceptableValueRange<float>(0.0f, 0.1f)));

            HairShadowBaseX = Config.Bind(
                "HairShadow", "04_SS_BaseX", 0f,
                new ConfigDescription(
                    "ScreenSpace form: light-independent horizontal shadow " +
                    "offset in meters; negative moves the other way.",
                    new AcceptableValueRange<float>(-0.01f, 0.01f)));

            HairShadowBaseY = Config.Bind(
                "HairShadow", "05_SS_BaseY", 0f,
                new ConfigDescription(
                    "ScreenSpace form: light-independent vertical shadow " +
                    "offset in meters; negative moves the other way.",
                    new AcceptableValueRange<float>(-0.01f, 0.01f)));

            HairShadowSoft = Config.Bind(
                "HairShadow", "06_Soft", 0.25f,
                new ConfigDescription(
                    "Hair shadow edge softness. ScreenSpace form: 0 = " +
                    "full-resolution RT with single tap, above 0 = 2x " +
                    "supersampled RT + 3x3 kernel scaled by the value. " +
                    "LightSpace form: PCF block spacing = value x 6 texels.",
                    new AcceptableValueRange<float>(0f, 1f)));

            HairShadowRes = Config.Bind(
                "HairShadow", "07_LS_Resolution", 2048,
                new ConfigDescription(
                    "LightSpace form: shadow map edge length. VRAM is about " +
                    "8/32/128 MB per character at 1024/2048/4096.",
                    new AcceptableValueList<int>(1024, 2048, 4096)));

            FaceNoSelfCast = Config.Bind(
                "Advanced", "FaceNoSelfCast", true,
                new ConfigDescription(
                    "While the clean base is active, set the face renderer's " +
                    "shadowCastingMode to Off so the face itself stays out of " +
                    "the realtime shadow map; external projections are kept. " +
                    "ShadowsOnly is not used.",
                    null, "Advanced"));

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
                    "Multiplier on the compensation magnitude. 0.6 is the user-tested " +
                    "optimum (1.0 = full offline-fitted magnitude, slightly overshooting " +
                    "on screen). Lower if the pattern moves against the skin; raise " +
                    "towards 1 if still dragging.",
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
            // 02/04/05 也订阅：持续生效于所有"未在 ME 定制该属性"的角色
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
            // 复制品压黑上限同属纯显示参数（shader uniform），不触发重烘焙
            NeckReplicaCap.SettingChanged += OnDisplayChanged;
            NeckShadowCompensation.SettingChanged += OnDisplayChanged;
            FaceNoSelfCast.SettingChanged += OnDisplayChanged;
            // 发影栏:02~05/06 是纯显示参数(uniform + pass 活跃门控),不触发重烘焙;
            // 01/07(分辨率)靠 Poll 签名比对重建;深度门 bias 已定死常量
            HairShadowShiftX.SettingChanged += OnDisplayChanged;
            HairShadowShiftY.SettingChanged += OnDisplayChanged;
            HairShadowBaseX.SettingChanged += OnDisplayChanged;
            HairShadowBaseY.SettingChanged += OnDisplayChanged;
            HairShadowSoft.SettingChanged += OnDisplayChanged;
            // 显示模式预设:写入四项 ConfigEntry.Value,各自既有生效链接管
            DisplayPreset.SettingChanged += OnDisplayPresetChanged;

            // 反算工具窗：常驻 MonoBehaviour（DontDestroyOnLoad），Ctrl+快捷键呼出
            ShadowColorMixer.Create();

            Log.LogInfo($"{PluginName} v{Version} started.");
        }

        private static void OnDisplayChanged(object sender, System.EventArgs e)
        {
            FaceOverlayCore.PushConfigAll();
        }

        /// <summary>
        /// 显示模式预设应用:按名查表写四项 ConfigEntry.Value。
        /// 各项的 SettingChanged 已挂 OnDisplayChanged/重建链,无需在此再推。
        /// </summary>
        private static void OnDisplayPresetChanged(object sender, System.EventArgs e)
        {
            foreach (var p in DisplayPresets)
            {
                if (p.Name != DisplayPreset.Value) continue;
                SoftnessAngle.Value = p.SoftnessAngle;
                NeckEdgeSoftness.Value = p.NeckEdgeSoftness;
                NeckRampScale.Value = p.NeckRampScale;
                HairShadowSoft.Value = p.HairShadowSoft;
                Log.LogInfo($"[DisplayPreset] applied '{p.Name}'.");
                return;
            }
        }

        /// <summary>
        /// ME 注入挂 Start:链式加载完成后 ME 程序集必已就绪,Awake 时不保证;
        /// 下拉列表在进 maker 时构建,此刻注入赶得上。marker 缺失通常=bundle 未重打。
        /// </summary>
        internal void Start()
        {
            var marker = ShaderBundle.Marker;
            if (marker != null)
                MaterialEditorMarker.TryInject(marker);
            else
                Log.LogWarning(
                    "HairShadowMarker shader missing from bundle; accessory hair shadow " +
                    "disabled. Rebuild the bundle (BuildBundle.assetNames).");
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
        /// [HairMaskDiag] DEBUG 一次性导出当前 hair mask RT 的 PNG(直接看遮罩对不对)。
        internal static void DebugExportHairMask()
        {
            HairShadowPass.DebugExportMask();
        }

        /// <summary>
        /// 诊断键边沿触发：KeyboardShortcut.IsPressed() 按住会连发（刷屏），
        /// 用 IsDown() + 上一帧状态自建上升沿。
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
            {
                DiagnoseDumpFaceMaterialProperties();
                // [HairMaskDiag] 同一 Ctrl+F9 边沿里导出 hair mask RT PNG。
                DebugExportHairMask();
                // [HairDiag] 同边沿打印材质 uniform 实值(Ctrl+F9),供发影链路诊断
                FaceOverlayCore.LogHairDiag();
                // [HairMatDump] 头发原材质到底有哪些贴图属性(定位发丝 alpha 属性名)
                HairShadowPass.DebugDumpHairMaterials();
            }
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
            if (EdgePressed(HairRTViewKey))
            {
                HairShadowPass.DiagShowRT = !HairShadowPass.DiagShowRT;
                Log.LogInfo("[HairRTView] " + (HairShadowPass.DiagShowRT ? "on" : "off"));
                // 光空间对齐判据随按键打印一次
                FaceOverlayCore.LogHairLightSpaceDiag();
            }
#endif

            bool enabled = Enabled.Value;

            if (!enabled && _wasEnabled)
            {
                FaceOverlayCore.RestoreAll();
                // 关掉后走不到下面的 Poll,不在这里清场的话发影 CB 会留在相机上
                // 继续每帧画 RT(没人消费),遮罩材质与 RT 也一并滞留
                HairShadowPass.Dispose();
            }
            _wasEnabled = enabled;

            if (!enabled) return;

            if (++_pollCounter >= PollInterval)
            {
                _pollCounter = 0;
                // ME 属性登记延迟到此处：ME 多半在游戏首帧后才 LoadXML，
                // 每轮尝试一次直到成功（幂等），成功后永不重进。
                MaterialEditorRegistry.TryRegister();
                FaceOverlayCore.Poll();
                // 头发独占 RT:内部做签名比对,非活跃态幂等清场
                HairShadowPass.Poll();
            }
        }

#if DEBUG
        /// 全屏回显 hair mask RT(Ctrl+F6 切换)。IMGUI 直画,不碰渲染管线。
        internal void OnGUI()
        {
            HairShadowPass.DrawDiagOverlay();
        }
#endif

        /// onPreCull 每台相机都会触发（主相机 / UI 相机 / 反射探针），同帧重复推送是白费开销。
        private static int _lastPushedFrame = -1;

        /// <summary>
        /// 头骨矩阵推送必须挂 Camera.onPreCull:NeckLookControllerVer2 在 LateUpdate 写
        /// 骨骼,插件 LateUpdate 与它同阶段顺序不确定,实测会读到写入前姿势;
        /// onPreCull 在所有 LateUpdate 后、渲染前,保证拿到最终骨骼姿势。
        /// </summary>
        private static void OnPreCull(Camera cam)
        {
            if (!Enabled.Value) return;

            int frame = Time.frameCount;
            if (_lastPushedFrame == frame) return;
            _lastPushedFrame = frame;

            FaceOverlayCore.PushHeadMatrices();
            FaceOverlayCore.PushHairLight();
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
            HairShadowPass.Dispose();
        }
    }
}
