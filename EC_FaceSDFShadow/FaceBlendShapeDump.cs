#if DEBUG
using UnityEngine;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// DEBUG 快捷键(Ctrl+F10):枚举面部 mesh 全部 BlendShape 通道(索引/名字/权重)打印到日志。
    /// 通道名在源码与资产里都查不到(FBSAssist 闭源、mesh 名字块被压缩),只能运行时枚举。
    /// </summary>
    internal static class FaceBlendShapeDump
    {
        // 面部表情相关 mesh。cf_O_face 直接用 cha.rendFace，其余按名查找。
        private static readonly string[] ExtraMeshNames =
        {
            "cf_O_mayuge",  // 眉
            "cf_O_tooth",   // 牙
        };

        internal static void Run()
        {
            if (!Manager.Character.IsInstance())
            {
                FaceSDFShadowPlugin.Log.LogWarning("[BlendShapeDump] Manager.Character not ready");
                return;
            }

            var dict = Manager.Character.Instance.dictEntryChara;
            if (dict == null || dict.Count == 0)
            {
                FaceSDFShadowPlugin.Log.LogWarning("[BlendShapeDump] No characters in scene");
                return;
            }

            foreach (var cha in dict.Values)
            {
                if (cha == null) continue;

                string chaName = cha.fileParam != null ? cha.fileParam.fullname : "?";
                FaceSDFShadowPlugin.Log.LogInfo($"[BlendShapeDump] character: {chaName}");

                Dump(cha.rendFace as SkinnedMeshRenderer, "cf_O_face");

                for (int i = 0; i < ExtraMeshNames.Length; i++)
                {
                    var t = FindChildByName(cha.transform, ExtraMeshNames[i]);
                    Dump(t != null ? t.GetComponent<SkinnedMeshRenderer>() : null, ExtraMeshNames[i]);
                }

                break; // 只打第一个角色，避免日志刷屏
            }
        }

        private static void Dump(SkinnedMeshRenderer smr, string label)
        {
            if (smr == null || smr.sharedMesh == null)
            {
                FaceSDFShadowPlugin.Log.LogWarning($"[BlendShapeDump] {label}: not found");
                return;
            }

            var mesh = smr.sharedMesh;
            int n = mesh.blendShapeCount;
            var sb = new System.Text.StringBuilder();
            sb.Append($"[BlendShapeDump] {label}: {n} channels");
            for (int i = 0; i < n; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                float w = smr.GetBlendShapeWeight(i);
                sb.Append($" | {i}:'{name}'={w:F0}");
            }
            FaceSDFShadowPlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>
        /// 自写递归查找（不用 FindLoop 扩展：定义在外部库，避免命名空间依赖）。
        /// </summary>
        private static Transform FindChildByName(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var hit = FindChildByName(root.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
#endif
