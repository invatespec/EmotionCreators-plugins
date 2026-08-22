using System;
using UnityEngine;

namespace EC_FaceSDFShadow
{
    internal static class TextureResampler
    {
        /// <summary>统一把合规方形输入重采样到运行时尺寸。</summary>
        internal static Texture2D ResizeSquare(Texture2D source, int targetSize, string name)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var rt = RenderTexture.GetTemporary(
                targetSize, targetSize, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            var previousActive = RenderTexture.active;
            Texture2D result = null;
            try
            {
                rt.filterMode = FilterMode.Bilinear;
                rt.wrapMode = TextureWrapMode.Clamp;
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                result = new Texture2D(targetSize, targetSize, TextureFormat.RGBA32, false, true)
                {
                    name = name,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                result.ReadPixels(new Rect(0, 0, targetSize, targetSize), 0, 0, false);
                result.Apply(false, false);
                return result;
            }
            catch
            {
                if (result != null) UnityEngine.Object.Destroy(result);
                throw;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
