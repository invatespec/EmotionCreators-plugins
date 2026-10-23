using System;
using System.Reflection;
using UnityEngine;
using YS_Node;

namespace EC_SceneAudit.Repair
{
    // 可选桥接：只认 EC_NodeSidePanel.Api.CanvasExtensionApi v1，缺少插件时不改 sizeDelta 绕过缓存。
    internal static class NodePatchCanvasBridge
    {
        private const string ApiTypeName = "EC_NodeSidePanel.Api.CanvasExtensionApi, EC_NodeSidePanel";
        private const string StateTypeName = "EC_NodeSidePanel.Api.CanvasState, EC_NodeSidePanel";

        private static bool initialized;
        private static Type apiType;
        private static Type stateType;
        private static MethodInfo getStateMethod;
        private static MethodInfo resizeMethod;
        private static FieldInfo currentSizeField;

        public static bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return apiType != null && getStateMethod != null && resizeMethod != null && currentSizeField != null;
            }
        }

        public static bool TryGetState(NodeControl control, out Vector2 size, out string error)
        {
            size = default(Vector2);
            error = null;
            EnsureInitialized();
            if (apiType == null || getStateMethod == null || currentSizeField == null)
            {
                error = "未找到 EC_NodeSidePanel CanvasExtensionApi v1。";
                return false;
            }
            try
            {
                var args = new object[] { control, null, null };
                var ok = getStateMethod.Invoke(null, args) is bool result && result;
                error = args[2] as string;
                if (!ok || args[1] == null)
                {
                    if (string.IsNullOrEmpty(error))
                        error = "扩展 API 未返回画布状态。";
                    return false;
                }
                var value = currentSizeField.GetValue(args[1]);
                if (!(value is Vector2))
                {
                    error = "扩展 API 返回的画布尺寸类型不匹配。";
                    return false;
                }
                size = (Vector2)value;
                return true;
            }
            catch (Exception ex)
            {
                error = "调用扩展 API 失败：" + ex.Message;
                return false;
            }
        }

        public static bool TryResizeAbsolute(
            NodeControl control,
            Vector2 desired,
            out Vector2 actual,
            out string error)
        {
            actual = default(Vector2);
            error = null;
            EnsureInitialized();
            if (apiType == null || resizeMethod == null || currentSizeField == null)
            {
                error = "未找到 EC_NodeSidePanel CanvasExtensionApi v1。";
                return false;
            }
            try
            {
                var args = new object[] { control, desired, true, null, null };
                var ok = resizeMethod.Invoke(null, args) is bool result && result;
                error = args[4] as string;
                if (!ok || args[3] == null)
                {
                    if (string.IsNullOrEmpty(error))
                        error = "扩展 API 未返回实际画布状态。";
                    return false;
                }
                var value = currentSizeField.GetValue(args[3]);
                if (!(value is Vector2))
                {
                    error = "扩展 API 返回的画布尺寸类型不匹配。";
                    return false;
                }
                actual = (Vector2)value;
                return true;
            }
            catch (Exception ex)
            {
                error = "调用绝对扩展 API 失败：" + ex.Message;
                return false;
            }
        }

        private static void EnsureInitialized()
        {
            if (initialized)
                return;
            initialized = true;
            try
            {
                apiType = Type.GetType(ApiTypeName, false);
                stateType = Type.GetType(StateTypeName, false);
                if (apiType == null || stateType == null)
                    return;
                var version = apiType.GetField("ApiVersion", BindingFlags.Public | BindingFlags.Static);
                if (version == null || !(version.GetValue(null) is int) || (int)version.GetValue(null) < 1)
                    return;
                getStateMethod = apiType.GetMethod(
                    "TryGetState",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(NodeControl), stateType.MakeByRefType(), typeof(string).MakeByRefType() },
                    null);
                resizeMethod = apiType.GetMethod(
                    "TryResizeAbsolute",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(NodeControl), typeof(Vector2), typeof(bool), stateType.MakeByRefType(), typeof(string).MakeByRefType() },
                    null);
                currentSizeField = stateType.GetField("CurrentSize", BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                apiType = null;
                stateType = null;
                getStateMethod = null;
                resizeMethod = null;
                currentSizeField = null;
            }
        }
    }
}
