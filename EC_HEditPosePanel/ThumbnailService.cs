using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace EC_HEditPosePanel
{
    internal sealed class ThumbnailService
    {
        private readonly MonoBehaviour _owner;
        private readonly Func<bool> _getVisible;
        private readonly Action<bool> _setVisible;
        private bool _pending;

        internal ThumbnailService(MonoBehaviour owner, Func<bool> getVisible, Action<bool> setVisible)
        {
            _owner = owner;
            _getVisible = getVisible;
            _setVisible = setVisible;
        }

        internal bool CaptureNextFrame(string path, Action<bool, string> completed)
        {
            if (_pending || _owner == null || string.IsNullOrEmpty(path)) return false;
            _pending = true;
            try
            {
                _owner.StartCoroutine(CaptureRoutine(path, completed));
                return true;
            }
            catch
            {
                _pending = false;
                return false;
            }
        }

        internal static Texture2D LoadPreview(string path, out string error)
        {
            error = null;
            Texture2D texture = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                texture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                if (!TryLoadImage(texture, bytes))
                {
                    ReleasePreview(ref texture);
                    error = "PNG 解码失败";
                    return null;
                }
                return texture;
            }
            catch (Exception ex)
            {
                ReleasePreview(ref texture);
                error = ex.Message;
                return null;
            }
        }

        internal static void ReleasePreview(ref Texture2D texture)
        {
            if (texture != null && texture != Texture2D.whiteTexture)
                UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        private IEnumerator CaptureRoutine(string path, Action<bool, string> completed)
        {
            bool visible = _getVisible != null && _getVisible();
            try
            {
                if (_setVisible != null) _setVisible(false);
            }
            catch (Exception ex)
            {
                _pending = false;
                completed?.Invoke(false, ex.Message);
                yield break;
            }

            yield return new WaitForEndOfFrame();
            Texture2D source = null;
            Texture2D output = null;
            RenderTexture target = null;
            RenderTexture previous = null;
            string temp = path + ".tmp";
            bool success = false;
            string failure = null;
            try
            {
                int width = Mathf.Max(1, Screen.width);
                int height = Mathf.Max(1, Screen.height);
                source = new Texture2D(width, height, TextureFormat.RGB24, false);
                source.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                source.Apply(false, false);

                target = RenderTexture.GetTemporary(320, 180, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, target);
                previous = RenderTexture.active;
                RenderTexture.active = target;
                output = new Texture2D(320, 180, TextureFormat.RGB24, false);
                output.ReadPixels(new Rect(0f, 0f, 320f, 180f), 0, 0, false);
                output.Apply(false, false);
                RenderTexture.active = previous;

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllBytes(temp, EncodePng(output));
                ReplaceFile(temp, path);
                success = true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                TryDeleteTemp(temp);
            }
            finally
            {
                ReleasePreview(ref source);
                ReleasePreview(ref output);
                if (previous != null || RenderTexture.active == target)
                    RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                try
                {
                    if (_setVisible != null) _setVisible(visible);
                }
                catch (Exception ex)
                {
                    success = false;
                    failure = "恢复插件窗口失败: " + ex.Message;
                }
                _pending = false;
            }
            completed?.Invoke(success, failure);
        }

        private static void ReplaceFile(string temp, string target)
        {
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
        }

        private static bool TryLoadImage(Texture2D texture, byte[] bytes)
        {
            MethodInfo method = typeof(Texture2D).GetMethod("LoadImage", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(byte[]), typeof(bool) }, null)
                ?? typeof(Texture2D).GetMethod("LoadImage", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(byte[]) }, null);
            object result;
            if (method != null)
            {
                result = method.GetParameters().Length == 2
                    ? method.Invoke(texture, new object[] { bytes, false })
                    : method.Invoke(texture, new object[] { bytes });
            }
            else
            {
                method = FindImageConversionMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) })
                    ?? FindImageConversionMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                if (method == null) return false;
                result = method.GetParameters().Length == 3
                    ? method.Invoke(null, new object[] { texture, bytes, false })
                    : method.Invoke(null, new object[] { texture, bytes });
            }
            return !(result is bool) || (bool)result;
        }

        private static byte[] EncodePng(Texture2D texture)
        {
            MethodInfo method = typeof(Texture2D).GetMethod("EncodeToPNG", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (method != null) return (byte[])method.Invoke(texture, null);
            method = FindImageConversionMethod("EncodeToPNG", new[] { typeof(Texture2D) });
            if (method == null) throw new MissingMethodException("Unity ImageConversion EncodeToPNG 不可用");
            return (byte[])method.Invoke(null, new object[] { texture });
        }

        private static MethodInfo FindImageConversionMethod(string name, Type[] parameters)
        {
            Type type = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", false)
                ?? Type.GetType("UnityEngine.ImageConversion, UnityEngine.CoreModule", false);
            return type?.GetMethod(name, BindingFlags.Static | BindingFlags.Public, null, parameters, null);
        }

        private static void TryDeleteTemp(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
