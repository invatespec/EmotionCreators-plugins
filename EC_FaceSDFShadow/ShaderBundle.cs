using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 从嵌入资源加载 shader assetbundle。手法同 MaterialEditor
    /// （Reference/KK_Plugins-master/src/MaterialEditor.Core/Core.MaterialEditor.cs:854-882）。
    ///
    /// 坑：bundle 里的资产键是小写化的工程路径（assets/shaders/xxx.shader），
    /// 不是 shader 声明名（Rainbowing/Xxx）。用声明名调 LoadAsset 会静默返回 null。
    /// 故统一走 LoadAllAssets 再按 shader.name 匹配。
    /// </summary>
    internal static class ShaderBundle
    {
        private const string ResourceName = "EC_FaceSDFShadow.Resources.ec_facesdf.unity3d";

        internal const string OverlayName = "Rainbowing/FaceSDFOverlay";

        private static Dictionary<string, Shader> _shaders;
        private static bool _loadAttempted;

        internal static Shader Overlay => Get(OverlayName);

        private static Shader Get(string name)
        {
            EnsureLoaded();
            if (_shaders == null) return null;
            return _shaders.TryGetValue(name, out var s) ? s : null;
        }

        private static void EnsureLoaded()
        {
            if (_loadAttempted) return;
            _loadAttempted = true;
            _shaders = LoadAll();
        }

        private static Dictionary<string, Shader> LoadAll()
        {
            byte[] bytes = ReadEmbeddedResource(ResourceName);
            if (bytes == null)
            {
                FaceSDFShadowPlugin.Log.LogError(
                    $"Embedded resource '{ResourceName}' not found. " +
                    "Build the bundle in UnityProject/ and copy it to Resources/.");
                return null;
            }

            AssetBundle bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromMemory(bytes);
                if (bundle == null)
                {
                    FaceSDFShadowPlugin.Log.LogError(
                        "Failed to load shader bundle. Was it built with Unity 2017.4.24?");
                    return null;
                }

                var loaded = bundle.LoadAllAssets<Shader>();
                if (loaded == null || loaded.Length == 0)
                {
                    FaceSDFShadowPlugin.Log.LogError("Shader bundle contains no shaders.");
                    return null;
                }

                var map = new Dictionary<string, Shader>();
                foreach (var s in loaded)
                {
                    if (s == null) continue;

                    // 加载成功 ≠ 可用：编译失败的 shader 会把材质渲染成粉红
                    if (!s.isSupported)
                    {
                        FaceSDFShadowPlugin.Log.LogError(
                            $"Shader '{s.name}' is not supported on this GPU/build. " +
                            "Rebuild the bundle with Unity 2017.4.24.");
                        continue;
                    }

                    map[s.name] = s;
                    FaceSDFShadowPlugin.Log.LogInfo($"Shader loaded: {s.name}");
                }

                return map.Count > 0 ? map : null;
            }
            catch (Exception ex)
            {
                FaceSDFShadowPlugin.Log.LogError($"Shader bundle load threw: {ex}");
                return null;
            }
            finally
            {
                // false = 只卸载 bundle 容器，保留已取出的 shader 资产
                if (bundle != null)
                    bundle.Unload(false);
            }
        }

        private static byte[] ReadEmbeddedResource(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream(name))
            {
                if (stream == null)
                    return null;

                var buffer = new byte[stream.Length];
                int read = 0;
                // 单次 Read 不保证读满，是隐蔽的"bundle 损坏"来源
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return read == buffer.Length ? buffer : null;
            }
        }
    }
}
