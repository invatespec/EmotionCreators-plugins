using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 向 ME 注入标记 shader 的白名单条目(LoadedShaders + XMLShaderProperties),
    /// 使其出现在 Change Shader 列表且可被 ME 读卡恢复。反射而非强引用:
    /// ME 未装/成员不符时降级禁用饰品受影,其余功能不受影响。Start 注入一次即可——
    /// BepInEx 链式加载完成后 ME 程序集必已就绪;ME 的 LoadXML 只写具体键不清字典,
    /// 注入条目不会被洗掉。
    /// </summary>
    internal static class MaterialEditorMarker
    {
        internal const string MarkerShaderName = "Rainbowing/HairShadowMarker";

        private static bool _attempted;

        internal static bool Injected { get; private set; }

        /// <summary>幂等;失败(warning 一条)后不再重试,饰品受影整体禁用。</summary>
        internal static bool TryInject(Shader marker)
        {
            if (Injected) return true;
            if (_attempted || marker == null) return false;
            _attempted = true;

            // 反射样板复用 MaterialEditorRegistry(类型定位/字典访问/空桶构造),不另起一套
            var baseType = MaterialEditorRegistry.FindMaterialEditorBase();
            if (baseType == null)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    "MaterialEditor not present; accessory hair shadow disabled.");
                return false;
            }

            try
            {
                var loaded = GetStaticDictionary(baseType, "LoadedShaders");
                var xml = MaterialEditorRegistry.GetXmlProperties(baseType);
                var shaderDataType = MaterialEditorRegistry.FindNestedType(baseType, "ShaderData");
                if (loaded == null || xml == null || shaderDataType == null)
                {
                    FaceSDFShadowPlugin.Log.LogWarning(
                        "MaterialEditor members missing (LoadedShaders/XMLShaderProperties/" +
                        "ShaderData); accessory hair shadow disabled.");
                    return false;
                }

                if (loaded.Contains(MarkerShaderName))
                {
                    // 已注入过(热重载等场景),幂等收口
                    Injected = true;
                    return true;
                }

                // ShaderOptimization=false:避开 ME AssetLoadedHook 对加载资产的 shader 替换
                var data = Activator.CreateInstance(shaderDataType,
                    new object[] { marker, MarkerShaderName, null, "false" });
                loaded[MarkerShaderName] = data;
                // XML 空桶:仅让 shader 进下拉列表,换 shader 时无属性重置副作用
                MaterialEditorRegistry.EnsureBucket(xml, MarkerShaderName);

                Injected = true;
                FaceSDFShadowPlugin.Log.LogInfo(
                    "Injected " + MarkerShaderName + " into MaterialEditor shader whitelist.");
                return true;
            }
            catch (Exception ex)
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    "MaterialEditor injection failed; accessory hair shadow disabled. " +
                    ex.Message);
                return false;
            }
        }

        private static IDictionary GetStaticDictionary(Type type, string fieldName)
        {
            var fi = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            return fi?.GetValue(null) as IDictionary;
        }
    }
}
