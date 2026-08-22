// AssetBundle 构建脚本。菜单 Rainbowing/Build FaceSDF Bundle 一键出包。
//
// 约束：必须用 Unity 2017.4.24（EC 引擎版本）执行。跨版本构建的 bundle
// 在 EC 里加载会静默失败或 shader 变粉。
//
// 产出 EC_FaceSDF.unity3d，需手工复制到 plugin/EC_FaceSDFShadow/Resources/。
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildBundle
{
    private const string BundleName = "EC_FaceSDF.unity3d";
    private const string OutputDir = "AssetBundles";

    [MenuItem("Rainbowing/Build FaceSDF Bundle")]
    public static void Build()
    {
        if (!Directory.Exists(OutputDir))
            Directory.CreateDirectory(OutputDir);

        // 显式列出要打进 bundle 的 shader。
        var assetNames = new[]
        {
            "Assets/Shaders/FaceSDFOverlay.shader",
        };

        var build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = assetNames,
        };

        var manifest = BuildPipeline.BuildAssetBundles(
            OutputDir,
            new[] { build },
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64);

        if (manifest == null)
        {
            Debug.LogError("[BuildBundle] 构建失败，见上方错误。");
            return;
        }

        var full = Path.GetFullPath(Path.Combine(OutputDir, BundleName));
        Debug.Log("[BuildBundle] 完成: " + full + "\n复制到 plugin/EC_FaceSDFShadow/Resources/ 后重新 dotnet build。");
        EditorUtility.RevealInFinder(full);
    }
}
