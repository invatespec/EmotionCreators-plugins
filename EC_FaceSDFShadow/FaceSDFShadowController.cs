using System;
using ExtensibleSaveFormat;
using KKAPI;
using KKAPI.Chara;
using UnityEngine;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 逐角色的面部 SDF 阴影配置，存储到角色卡扩展数据。
    /// Enable(bool) + ShadowColor(Color) + ThresholdBias/SoftnessAngle(float)。
    ///
    /// 软化在角度域：shader 过渡带 = max(fwidth×1.5px 抗锯齿, SoftnessAngle/180)，
    /// 不随镜头距离变化。旧卡的 EdgeSoftnessPx 键（屏幕像素域）语义已失效，
    /// 读取时直接忽略回退默认值，DataVersion 保持 1 —— 少一个键不影响旧版插件读新卡。
    ///
    /// 三个形态参数带 ME 定制锁存（Customized*）：被 ME/取色器改过的卡锁定
    /// 该属性不受全局配置影响，标志随卡持久化。旧卡缺键 = false = 跟随全局。
    /// </summary>
    public class FaceSDFShadowController : CharaCustomFunctionController
    {
        private const int DataVersion = 1;
        private const string KeyEnable = "Enable";
        private const string KeyShadowColor = "ShadowColor";
        private const string KeyThresholdBias = "ThresholdBias";
        private const string KeySoftnessAngle = "SoftnessAngle";
        private const string KeyCustomizedColor = "CustomizedColor";
        private const string KeyCustomizedBias = "CustomizedBias";
        private const string KeyCustomizedSoftness = "CustomizedSoftness";

        // 当前角色的配置（从卡读取，或首次装插件时的默认值）。
        // 三个形态参数的默认值与 Config 一致，但字段初始化器不能引用静态 Config，
        // 故这里写常量，OnReload 里再统一按 Config 兜底。
        public bool Enable { get; private set; } = true;
        public Color ShadowColor { get; private set; } = new Color(0.79f, 0.43f, 0f, 0.27f);
        public float ThresholdBias { get; private set; } = 0f;
        public float SoftnessAngle { get; private set; } = 3f;

        /// ME（含取色器）改过该属性的一次性锁存标志：置位后全局 02/04/05 不再覆盖该属性。
        /// 由 FaceOverlayCore 的哨兵检测 / 取色器直写置位，永不自动解锁，随卡持久化。
        public bool CustomizedColor { get; internal set; }
        public bool CustomizedBias { get; internal set; }
        public bool CustomizedSoftness { get; internal set; }

        internal int Revision { get; private set; }

        // 配置变化事件，通知 FaceOverlayCore 重新 Apply
        public event Action OnConfigChanged;

        protected override void OnCardBeingSaved(GameMode currentGameMode)
        {
            var pd = new PluginData { version = DataVersion };

            // 从当前 overlay 材质读回最新值（ME 滑条/调色板直接写材质，Controller 只读不写）
            // - Enable：ME 滑条写 _Enable float（0~1），>0.5 视为启用
            // - ShadowColor：ME 调色板写 _ShadowColor
            // - ThresholdBias/SoftnessAngle：ME 滑条写对应 float
            var smr = ChaControl?.rendFace as SkinnedMeshRenderer;
            if (smr != null && FaceOverlayCore.TryGetOverlayMaterial(smr, out var overlayMat))
            {
                if (overlayMat.HasProperty(ShaderIDs.Enable))
                    Enable = overlayMat.GetFloat(ShaderIDs.Enable) >= 0.5f;
                if (overlayMat.HasProperty(ShaderIDs.ShadowColor))
                    ShadowColor = overlayMat.GetColor(ShaderIDs.ShadowColor);
                if (overlayMat.HasProperty(ShaderIDs.ThresholdBias))
                    ThresholdBias = overlayMat.GetFloat(ShaderIDs.ThresholdBias);
                if (overlayMat.HasProperty(ShaderIDs.SoftnessAngle))
                    SoftnessAngle = overlayMat.GetFloat(ShaderIDs.SoftnessAngle);
            }

            pd.data[KeyEnable] = Enable;
            pd.data[KeyShadowColor] = new[] { ShadowColor.r, ShadowColor.g, ShadowColor.b, ShadowColor.a };
            pd.data[KeyThresholdBias] = ThresholdBias;
            pd.data[KeySoftnessAngle] = SoftnessAngle;
            // 锁存标志来自轮询检测的置位，不在此处从材质推断
            pd.data[KeyCustomizedColor] = CustomizedColor;
            pd.data[KeyCustomizedBias] = CustomizedBias;
            pd.data[KeyCustomizedSoftness] = CustomizedSoftness;

            SetExtendedData(pd);

            FaceSDFShadowPlugin.Log.LogDebug(
                $"[Controller] Saved card data: Enable={Enable}, " +
                $"ShadowColor={ShadowColor}, " +
                $"ThresholdBias={ThresholdBias}, SoftnessAngle={SoftnessAngle}, " +
                $"Customized=({CustomizedColor}/{CustomizedBias}/{CustomizedSoftness})");
        }

        protected override void OnReload(GameMode currentGameMode, bool maintainState)
        {
            if (maintainState) return;

            var data = GetExtendedData();
            if (data != null && data.version == DataVersion)
            {
                // 从卡读取配置
                Enable = data.data.TryGetValue(KeyEnable, out var valEnable) && valEnable is bool b && b;

                if (data.data.TryGetValue(KeyShadowColor, out var valColor) && valColor is object[] arr && arr.Length == 4)
                {
                    try
                    {
                        ShadowColor = new Color(
                            Convert.ToSingle(arr[0]),
                            Convert.ToSingle(arr[1]),
                            Convert.ToSingle(arr[2]),
                            Convert.ToSingle(arr[3]));
                    }
                    catch (Exception ex)
                    {
                        FaceSDFShadowPlugin.Log.LogWarning($"[Controller] Failed to parse ShadowColor: {ex.Message}");
                        ShadowColor = FaceSDFShadowPlugin.ShadowColor.Value;
                    }
                }
                else
                {
                    ShadowColor = FaceSDFShadowPlugin.ShadowColor.Value;
                }

                // 形态参数：旧卡（无这两个键）回退 Config 默认值，保证平滑升级
                ThresholdBias = ReadFloat(data.data, KeyThresholdBias, FaceSDFShadowPlugin.ThresholdBias.Value);
                SoftnessAngle = ReadFloat(data.data, KeySoftnessAngle, FaceSDFShadowPlugin.SoftnessAngle.Value);

                // 定制锁存：旧卡缺键 = false = 该属性跟随全局
                CustomizedColor = ReadBool(data.data, KeyCustomizedColor);
                CustomizedBias = ReadBool(data.data, KeyCustomizedBias);
                CustomizedSoftness = ReadBool(data.data, KeyCustomizedSoftness);

                FaceSDFShadowPlugin.Log.LogDebug(
                    $"[Controller] Loaded card data: Enable={Enable}, " +
                    $"ShadowColor={ShadowColor}, " +
                    $"ThresholdBias={ThresholdBias}, SoftnessAngle={SoftnessAngle}, " +
                    $"Customized=({CustomizedColor}/{CustomizedBias}/{CustomizedSoftness})");
            }
            else
            {
                // 首次装插件或数据版本不匹配 → 使用全局 Config 默认值
                Enable = true;  // 默认开启（保持"装上即生效"）
                ShadowColor = FaceSDFShadowPlugin.ShadowColor.Value;
                ThresholdBias = FaceSDFShadowPlugin.ThresholdBias.Value;
                SoftnessAngle = FaceSDFShadowPlugin.SoftnessAngle.Value;
                CustomizedColor = false;
                CustomizedBias = false;
                CustomizedSoftness = false;

                FaceSDFShadowPlugin.Log.LogDebug(
                    $"[Controller] No card data (first install or old version), using defaults: " +
                    $"Enable={Enable}, ShadowColor={ShadowColor}");
            }

            // 通知 FaceOverlayCore 应用新配置
            Revision++;
            OnConfigChanged?.Invoke();
        }

        /// <summary>
        /// 从 PluginData 读 float。MessagePack 反序列化可能是 float 或 double，
        /// 统一用 Convert.ToSingle 兜底；缺失或类型不符回退 fallback。
        /// </summary>
        private static float ReadFloat(System.Collections.Generic.Dictionary<string, object> data,
            string key, float fallback)
        {
            if (data.TryGetValue(key, out var v))
            {
                try { return Convert.ToSingle(v); }
                catch { /* 类型不符 → fallback */ }
            }
            return fallback;
        }

        /// <summary>
        /// 从 PluginData 读 bool。缺失或类型不符 = false（旧卡缺键即"未定制"）。
        /// </summary>
        private static bool ReadBool(System.Collections.Generic.Dictionary<string, object> data, string key)
        {
            return data.TryGetValue(key, out var v) && v is bool b && b;
        }

        /// <summary>
        /// 运行时修改 Enable 并触发重推（外部调用入口，本插件内无调用方）。
        /// </summary>
        public void SetEnable(bool enable)
        {
            if (Enable != enable)
            {
                Enable = enable;
                Revision++;
                OnConfigChanged?.Invoke();
                FaceSDFShadowPlugin.Log.LogDebug($"[Controller] Enable changed to {enable}");
            }
        }
    }
}
