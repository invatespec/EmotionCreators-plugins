using System;
using System.Collections;
using System.Reflection;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 运行时向 MaterialEditor 注入 Rainbowing/FaceSDFOverlay 的可调属性（专属桶）。
    ///
    /// 为什么用反射而非直接引用：本插件不依赖 ME 程序集（csproj 无 reference）；
    /// ME 也可能未安装，此时静默跳过，功能仍由 00_Enabled 全局开关保底。
    ///
    /// 桶内必 push：Enable(Float, Range 0~1) + ShadowColor(Color) +
    /// ThresholdBias/SoftnessAngle(Float 滑条)。
    /// - Enable：ME 写回 SetFloat("_Enable")，shader 内 _Enable<0.5 输出白=软关闭。
    ///   Float 带 Range 渲染成滑条（群主决策 F2，弃 Keyword toggle——反射注入 Keyword
    ///   的 ctor 跨程序集绑定太脆）。
    /// - ShadowColor：进专属桶后 UI.cs:677 只取这一个桶不再回退 default，
    ///   必须一起登记否则原靠 default 桶列出的调色能力丢失。
    /// - 两个形态参数：逐角色定制阴影分界/边缘，Range 与 Config AcceptableValueRange 一致。
    ///
    /// 核心时序坑（round 1/3 实测崩过）：必须先备好所有反射对象，再写桶，再刷新，
    /// 刷新后验证 PropertyOrganizer.PropertyOrganization 真含本键；不含则回滚桶，
    /// 否则 UI.cs:677 走专属桶却取不到 → KeyNotFoundException（比"没登记走 default"更糟）。
    ///
    /// 未安装 vs 未加载：baseType==null（AppDomain 无该类型）=未安装→永久放弃；
    /// baseType 有但 ME 刚启动字段未就绪=未加载→本轮放弃但不设永久标志，下轮重试。
    /// </summary>
    internal static class MaterialEditorRegistry
    {
        private const string ShaderKey = "Rainbowing/FaceSDFOverlay";
        private const string EnableProp = "Enable";        // F2：Float 属性，ME 写回 SetFloat("_Enable")
        private const string ShadowColorProp = "ShadowColor";
        private const string ThresholdBiasProp = "ThresholdBias";      // Float 滑条，ME 写回 _ThresholdBias
        private const string SoftnessAngleProp = "SoftnessAngle";    // Float 滑条，ME 写回 _SoftnessAngle

        private static bool _installed = true;   // 默认乐观；仅在确认 ME 不存在时置 false
        private static bool _registered;

        internal static bool IsRegistered => _registered;

        /// <summary>
        /// 尝试注入。幂等：已注册或已确认未安装直接返回；未就绪则下轮重试。
        /// 失败路径一律保证 XMLShaderProperties 不留半写桶，避免 UI KeyNotFound。
        /// </summary>
        internal static void TryRegister()
        {
            if (_registered || !_installed) return;

            // 阶段 A：备料——全部就绪前不碰任何静态字段，避免半写。
            Type baseType = FindMaterialEditorBase();
            if (baseType == null)
            {
                _installed = false; // AppDomain 里确实没有 ME 类型 = 未安装
                FaceSDFShadowPlugin.Log.LogDebug("MaterialEditor not present; skipping property registration.");
                return;
            }

            Type propDataType = FindNestedType(baseType, "ShaderPropertyData");
            Type propTypeEnum = FindShaderPropertyType(baseType.Assembly);
            Type organizerType = FindType(baseType.Assembly, "MaterialEditorAPI.PropertyOrganizer");
            if (propDataType == null || propTypeEnum == null || organizerType == null)
            {
                // ME 还在加载、嵌套类型未就绪 → 本轮跳过，下轮重试，不放弃。
                FaceSDFShadowPlugin.Log.LogDebug(
                    $"ME types not ready (propData={propDataType != null}, propEnum={propTypeEnum != null}, " +
                    $"organizer={organizerType != null}); retrying next poll.");
                return;
            }

            IDictionary dict = GetXmlProperties(baseType);
            if (dict == null)
            {
                // ME 字段未初始化 → 未加载，下轮重试。
                FaceSDFShadowPlugin.Log.LogDebug(
                    "ME XMLShaderProperties not initialized yet; retrying next poll.");
                return;
            }

            object floatValue;
            object colorValue;
            try
            {
                floatValue = Enum.Parse(propTypeEnum, "Float");
                colorValue = Enum.Parse(propTypeEnum, "Color");
            }
            catch (Exception)
            {
                // enum 成员名对不上 → ME 版本不兼容，下轮再试也无效，按未安装处理。
                _installed = false;
                FaceSDFShadowPlugin.Log.LogWarning("MaterialEditor ShaderPropertyType enum mismatch; skipping registration.");
                return;
            }

            // 阶段 B：构造属性数据（不写桶）。任一为 null = 构造失败。
            // Enable(Float) 带 Range 0~1 → 滑条；ShadowColor(Color) 不带 range；
            // 两个形态参数(Float) 带与 Config AcceptableValueRange 一致的 min/max。
            object enableData = CreatePropData(propDataType, EnableProp, floatValue, "0", "1");
            object colorData = CreatePropData(propDataType, ShadowColorProp, colorValue, null, null);
            object thresholdBiasData = CreatePropData(propDataType, ThresholdBiasProp, floatValue, "-1", "1");
            object softnessAngleData = CreatePropData(propDataType, SoftnessAngleProp, floatValue, "0", "30");
            if (enableData == null || colorData == null ||
                thresholdBiasData == null || softnessAngleData == null)
            {
                FaceSDFShadowPlugin.Log.LogWarning("MaterialEditor ShaderPropertyData ctor not found; skipping registration.");
                return;
            }

            // 阶段 C：建/取专属桶并写入。写入前备份"是否新建桶"以便回滚。
            bool bucketExisted = dict.Contains(ShaderKey);
            IDictionary bucket = EnsureBucket(dict, ShaderKey);
            if (bucket.Contains(EnableProp))
            {
                // 已被登记过（含 ME 自身刷新），视为完成，避免重复刷新抖动 UI。
                _registered = true;
                return;
            }
            bucket[EnableProp] = enableData;
            bucket[ShadowColorProp] = colorData;
            bucket[ThresholdBiasProp] = thresholdBiasData;
            bucket[SoftnessAngleProp] = softnessAngleData;

            // 阶段 D：触发 ME 重建组织表，随后验证同步。
            InvokeRefresh(baseType);

            if (VerifyOrganization(organizerType, ShaderKey))
            {
                _registered = true;
                FaceSDFShadowPlugin.Log.LogInfo("Registered ME properties (Enable/ShadowColor/ThresholdBias/SoftnessAngle) for " + ShaderKey + ".");
            }
            else
            {
                // 刷新没把本桶同步进 PropertyOrganization → 半写状态会让 UI 崩。
                // 回滚：移除本桶，UI 回退 default，功能退回"Enable 不显示但插件正常"。
                Rollback(dict, ShaderKey, bucketExisted);
                FaceSDFShadowPlugin.Log.LogWarning(
                    "ME Refresh did not sync " + ShaderKey + " into PropertyOrganization; rolled back. " +
                    "Enable toggle unavailable, global 00_Enabled toggle still works.");
            }
        }

        private static Type FindMaterialEditorBase()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType("MaterialEditorAPI.MaterialEditorPluginBase"); }
                catch { /* 跨 AppDomain 访问可能抛，忽略 */ }
                if (t != null) return t;
            }
            return null;
        }

        private static Type FindType(Assembly asm, string fullTypeName)
        {
            try { return asm.GetType(fullTypeName); }
            catch { return null; }
        }

        /// <summary>
        /// 定位 ShaderPropertyType 枚举。它嵌在 MaterialAPI 类里
        /// （MaterialAPI.cs:10 class MaterialAPI + :877 enum ShaderPropertyType），
        /// 反射全名是 MaterialEditorAPI.MaterialAPI+ShaderPropertyType（嵌套类用 + 分隔，
        /// 不是 .）。先按该全名精确找；失败再跨 assembly 所有类型扫 IsEnum 兜底，
        /// 避免 ME 升级改嵌套位置/命名细节再踩空。
        /// </summary>
        private static Type FindShaderPropertyType(Assembly asm)
        {
            Type t = FindType(asm, "MaterialEditorAPI.MaterialAPI+ShaderPropertyType");
            if (t != null) return t;

            // fallback：跨类型扫描，Name 匹配且是枚举
            try
            {
                foreach (var mod in asm.GetModules())
                {
                    foreach (var type in mod.GetTypes())
                    {
                        if (type.Name == "ShaderPropertyType" && type.IsEnum)
                            return type;
                    }
                }
            }
            catch { /* 个别模块不可枚举时忽略，走 null 让阶段 A 重试 */ }
            return null;
        }

        private static Type FindNestedType(Type type, string nestedName)
        {
            foreach (var nt in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (nt.Name == nestedName) return nt;
            return null;
        }

        private static IDictionary GetXmlProperties(Type baseType)
        {
            var fi = baseType.GetField(
                "XMLShaderProperties",
                BindingFlags.Public | BindingFlags.Static);
            return fi?.GetValue(null) as IDictionary;
        }

        private static IDictionary EnsureBucket(IDictionary dict, string key)
        {
            if (dict.Contains(key))
                return (IDictionary)dict[key];

            // Dictionary<string, ShaderPropertyData>：无参构造即空字典。
            // 专属桶不在 default.xml → ME 的 LoadXML 不会清掉它。
            var bucketType = dict.GetType().GetGenericArguments()[1];
            var bucket = (IDictionary)Activator.CreateInstance(bucketType);
            dict[key] = bucket;
            return bucket;
        }

        private static object CreatePropData(Type propDataType, string name, object typeValue, string minValue, string maxValue)
        {
            // 构造签名（11 参，见 ME PluginBase.ShaderPropertyData）：
            //   (name, type, defaultValue=null, defaultValueAB=null, anisoLevel=null,
            //    filterMode=null, wrapMode=null, minValue=null, maxValue=null,
            //    hidden=null, category=null)
            // 用 Activator.CreateInstance 而非 GetConstructor：binder 自动匹配重载，
            // 绕开 round3 里 GetConstructor 精确匹配失败（ctor 跨程序集绑定脆弱）的问题。
            var args = new object[]
            {
                name,            // name
                typeValue,       // type (boxed enum)
                null,            // defaultValue
                null,            // defaultValueAB
                null,            // anisoLevel
                null,            // filterMode
                null,            // wrapMode
                minValue,        // minValue
                maxValue,        // maxValue
                null,            // hidden
                null,            // category
            };
            try { return Activator.CreateInstance(propDataType, args); }
            catch { return null; }
        }

        private static void InvokeRefresh(Type baseType)
        {
            // RefreshPropertyOrganization 是 protected static；非 Public 要 NonPublic。
            var mi = baseType.GetMethod(
                "RefreshPropertyOrganization",
                BindingFlags.NonPublic | BindingFlags.Static);
            try { mi?.Invoke(null, null); }
            catch { /* 内部抛已吞，由后续 VerifyOrganization 决定是否回滚 */ }
        }

        /// <summary>
        /// 反射读 MaterialEditorAPI.PropertyOrganizer.PropertyOrganization，
        /// 验证 Refresh 后真的含本 shader 键。
        /// </summary>
        private static bool VerifyOrganization(Type organizerType, string key)
        {
            if (organizerType == null) return false;
            var fi = organizerType.GetField(
                "PropertyOrganization",
                BindingFlags.NonPublic | BindingFlags.Static);
            var org = fi?.GetValue(null) as IDictionary;
            return org != null && org.Contains(key);
        }

        /// <summary>
        /// 回滚半写桶：若是本次新建则整键移除；若早已存在（本次只追加 props）则只删本次加的两项。
        /// </summary>
        private static void Rollback(IDictionary dict, string key, bool bucketExisted)
        {
            if (!dict.Contains(key)) return;
            if (!bucketExisted)
            {
                dict.Remove(key);
                return;
            }
            // 桶原就存在：只移除本次 push 的几项，尽量不破坏既有状态。
            var bucket = (IDictionary)dict[key];
            if (bucket.Contains(EnableProp)) bucket.Remove(EnableProp);
            if (bucket.Contains(ShadowColorProp)) bucket.Remove(ShadowColorProp);
            if (bucket.Contains(ThresholdBiasProp)) bucket.Remove(ThresholdBiasProp);
            if (bucket.Contains(SoftnessAngleProp)) bucket.Remove(SoftnessAngleProp);
        }
    }
}
