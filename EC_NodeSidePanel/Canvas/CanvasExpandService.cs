using System;
using System.Collections.Generic;
using BepInEx.Logging;
using EC_NodeSidePanel.Api;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using YS_Node;

namespace EC_NodeSidePanel
{
    // 原 EC_NodeCanvasExpand：扩 rtfGrid、中心缩放、pan 钳制、网格 RawImage 平铺。
    internal static class CanvasExpandService
    {
        private const float GridScaleMin = 0.5f;
        private const float DefaultZoomInScale = 1f;

        private static readonly HashSet<int> ExpandedIds = new HashSet<int>();
        private static readonly Dictionary<int, float> MinLocalScale = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector2> OriginalSizes = new Dictionary<int, Vector2>();
        private static readonly Dictionary<int, float> BackgroundPixelsPerUnit = new Dictionary<int, float>();

        internal static readonly AccessTools.FieldRef<NodeControl, float> GridScaleRef =
            AccessTools.FieldRefAccess<NodeControl, float>("gridScale");
        internal static readonly AccessTools.FieldRef<NodeControl, Vector2> VGridPosRef =
            AccessTools.FieldRefAccess<NodeControl, Vector2>("vGridPos");
        private static readonly AccessTools.FieldRef<NodeControl, float> ZoomSpeedRef =
            AccessTools.FieldRefAccess<NodeControl, float>("zoomSpeed");

        internal static ManualLogSource Log;
        internal static float SizeMultiplier = 5f;

        internal static float ScaleFromGridScale(float gridScale) => 1.5f - gridScale;

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static Vector2 ComputeMaxPan(RectTransform grid, RectTransform viewport, float scale)
        {
            Vector2 view = viewport.rect.size;
            if (view.x <= 0f || view.y <= 0f)
                view = viewport.sizeDelta;

            Vector2 scaled = grid.sizeDelta * scale;
            return new Vector2(
                Mathf.Max(0f, (scaled.x - view.x) * 0.5f),
                Mathf.Max(0f, (scaled.y - view.y) * 0.5f));
        }

        internal static void TryExpand(NodeControl control)
        {
            if (control == null || control.rtfGrid == null)
                return;

            int id = control.GetInstanceID();
            if (!ExpandedIds.Add(id))
                return;

            float mul = SizeMultiplier;
            if (mul <= 1.001f)
                return;

            var grid = control.rtfGrid;
            Vector2 before = grid.sizeDelta;
            if (before.x <= 0f || before.y <= 0f)
            {
                Log?.LogWarning($"rtfGrid.sizeDelta invalid: {before}, skip expand.");
                ExpandedIds.Remove(id);
                return;
            }

            CanvasState state;
            string error;
            if (!TryResizeAbsolute(control, before * mul, true, out state, out error))
            {
                Log?.LogWarning("absolute canvas expand failed: " + error);
                return;
            }
            Log?.LogInfo($"node canvas expanded x{mul}: {before} -> {state.CurrentSize}");
        }

        internal static bool TryGetState(NodeControl control, out CanvasState state, out string error)
        {
            state = null;
            error = null;
            try
            {
                if (control == null || control.rtfGrid == null)
                {
                    error = "NodeControl 或 rtfGrid 不可用。";
                    return false;
                }
                var grid = control.rtfGrid;
                var parent = grid.parent as RectTransform;
                if (parent == null)
                {
                    error = "rtfGrid 缺少视口父节点。";
                    return false;
                }
                int id = control.GetInstanceID();
                Vector2 current = grid.sizeDelta;
                if (current.x <= 0f || current.y <= 0f)
                {
                    error = "rtfGrid.sizeDelta 无效：" + current;
                    return false;
                }
                Vector2 original;
                if (!OriginalSizes.TryGetValue(id, out original))
                {
                    original = current;
                    OriginalSizes[id] = original;
                }
                float minLocal = GetMinLocalScale(control, grid, parent);
                state = new CanvasState
                {
                    CurrentSize = current,
                    NativeSize = original,
                    ViewportSize = parent.rect.size,
                    Pivot = grid.pivot,
                    Pan = VGridPosRef(control),
                    Scale = ScaleFromGridScale(GridScaleRef(control)),
                    MinScale = minLocal,
                    BackgroundReady = FindGridRawImage(grid) != null || FindGridImage(grid) != null
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool TryResizeAbsolute(
            NodeControl control,
            Vector2 desiredSize,
            bool preserveView,
            out CanvasState state,
            out string error)
        {
            state = null;
            error = null;
            if (control == null || control.rtfGrid == null)
            {
                error = "NodeControl 或 rtfGrid 不可用。";
                return false;
            }
            if (!IsFinite(desiredSize.x) || !IsFinite(desiredSize.y)
                || desiredSize.x <= 0f || desiredSize.y <= 0f)
            {
                error = "目标画布尺寸必须是正有限数：" + desiredSize;
                return false;
            }

            try
            {
                var grid = control.rtfGrid;
                var parent = grid.parent as RectTransform;
                if (parent == null)
                {
                    error = "rtfGrid 缺少视口父节点。";
                    return false;
                }
                int id = control.GetInstanceID();
                if (!OriginalSizes.ContainsKey(id))
                    OriginalSizes[id] = grid.sizeDelta;

                Vector2 localViewCenter = default(Vector2);
                if (preserveView)
                {
                    Vector3 viewportWorld = parent.TransformPoint(parent.rect.center);
                    localViewCenter = grid.InverseTransformPoint(viewportWorld);
                }

                EnsureCenterPivot(grid);
                grid.sizeDelta = desiredSize;

                float fit = ComputeFitScale(grid, parent);
                float minLocal = Mathf.Clamp(fit * 0.95f, 0.02f, 0.5f);
                MinLocalScale[id] = minLocal;

                float scale = Mathf.Clamp(ScaleFromGridScale(GridScaleRef(control)), minLocal, DefaultZoomInScale);
                GridScaleRef(control) = 1.5f - scale;
                grid.localScale = new Vector3(scale, scale, 1f);

                Vector2 pan = VGridPosRef(control);
                Vector2 maxPan = ComputeMaxPan(grid, parent, scale);
                pan.x = Mathf.Clamp(pan.x, -maxPan.x, maxPan.x);
                pan.y = Mathf.Clamp(pan.y, -maxPan.y, maxPan.y);
                grid.anchoredPosition = pan;

                if (preserveView)
                {
                    Vector2 viewportPoint = parent.InverseTransformPoint(grid.TransformPoint(localViewCenter));
                    Vector2 delta = parent.rect.center - viewportPoint;
                    pan += delta;
                    maxPan = ComputeMaxPan(grid, parent, scale);
                    pan.x = Mathf.Clamp(pan.x, -maxPan.x, maxPan.x);
                    pan.y = Mathf.Clamp(pan.y, -maxPan.y, maxPan.y);
                    grid.anchoredPosition = pan;
                }
                VGridPosRef(control) = pan;

                bool backgroundReady = EnsureBackground(control, grid, desiredSize);
                Log?.LogInfo($"absolute canvas resize -> {desiredSize}, minScale={minLocal:F3}, pan={pan}, background={backgroundReady}");
                if (!TryGetState(control, out state, out error))
                    return false;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool GridDragFuncPrefix(NodeControl instance, Vector2 move)
        {
            // 面板上吞中键拖动画布
            if (NodeSidePanelPlugin.IsMouseOverPanel())
                return false;
            Vector2 v = VGridPosRef(instance);
            v += move;
            VGridPosRef(instance) = v;
            return false;
        }

        internal static bool NodeControlUpdatePrefix(NodeControl instance)
        {
            var rtfGrid = instance.rtfGrid;
            if (rtfGrid == null)
                return false;

            var parent = rtfGrid.parent as RectTransform;
            if (parent == null)
                return false;

            float zoomSpeed = ZoomSpeedRef(instance);
            float gridScale = GridScaleRef(instance);
            Vector2 vGridPos = VGridPosRef(instance);

            float minLocal = GetMinLocalScale(instance, rtfGrid, parent);
            float oldScale = ScaleFromGridScale(gridScale);
            // 鼠标在侧边面板上时不缩放画布
            float delta = NodeSidePanelPlugin.IsMouseOverPanel()
                ? 0f
                : Input.GetAxis("Mouse ScrollWheel") * -zoomSpeed;
            if (!Mathf.Approximately(delta, 0f))
            {
                gridScale = Mathf.Clamp(gridScale + delta, GridScaleMin, 1.5f - minLocal);
                float newScale = Mathf.Clamp(ScaleFromGridScale(gridScale), minLocal, DefaultZoomInScale);
                gridScale = 1.5f - newScale;
                if (oldScale > 1e-4f)
                    vGridPos *= newScale / oldScale;
            }

            float scale = Mathf.Clamp(ScaleFromGridScale(gridScale), minLocal, DefaultZoomInScale);
            gridScale = 1.5f - scale;

            Vector2 maxPan = ComputeMaxPan(rtfGrid, parent, scale);
            vGridPos.x = Mathf.Clamp(vGridPos.x, -maxPan.x, maxPan.x);
            vGridPos.y = Mathf.Clamp(vGridPos.y, -maxPan.y, maxPan.y);

            rtfGrid.anchoredPosition = vGridPos;
            rtfGrid.localScale = new Vector3(scale, scale, 1f);
            GridScaleRef(instance) = gridScale;
            VGridPosRef(instance) = vGridPos;
            return false;
        }

        private static float GetMinLocalScale(NodeControl control, RectTransform grid, RectTransform viewport)
        {
            int id = control.GetInstanceID();
            float cached;
            if (MinLocalScale.TryGetValue(id, out cached))
                return cached;

            float fit = ComputeFitScale(grid, viewport);
            float minLocal = Mathf.Clamp(fit * 0.95f, 0.02f, 0.5f);
            MinLocalScale[id] = minLocal;
            return minLocal;
        }

        private static float ComputeFitScale(RectTransform grid, RectTransform viewport)
        {
            Vector2 view = viewport.rect.size;
            if (view.x <= 1f || view.y <= 1f)
                view = viewport.sizeDelta;
            if (view.x <= 1f || view.y <= 1f)
                return 0.1f;

            Vector2 size = grid.sizeDelta;
            if (size.x <= 1f || size.y <= 1f)
                return 0.1f;

            return Mathf.Min(view.x / size.x, view.y / size.y);
        }

        private static void EnsureCenterPivot(RectTransform grid)
        {
            Vector2 pivot = grid.pivot;
            if (Mathf.Abs(pivot.x - 0.5f) < 0.001f && Mathf.Abs(pivot.y - 0.5f) < 0.001f)
                return;

            Vector2 size = grid.rect.size;
            if (size.x <= 0f || size.y <= 0f)
                size = grid.sizeDelta;

            Vector2 deltaPivot = new Vector2(0.5f, 0.5f) - pivot;
            grid.pivot = new Vector2(0.5f, 0.5f);
            grid.anchoredPosition += new Vector2(deltaPivot.x * size.x, deltaPivot.y * size.y);
        }

        private static bool EnsureBackground(NodeControl control, RectTransform grid, Vector2 targetSize)
        {
            var existingRaw = FindGridRawImage(grid);
            if (existingRaw != null)
            {
                UpdateRawBackground(control, existingRaw, targetSize);
                return true;
            }

            var image = FindGridImage(grid);
            if (image == null)
            {
                Log?.LogWarning("grid Image/RawImage not found under rtfGrid; background unchanged.");
                return false;
            }
            FixBackground(control, image, targetSize);
            return FindGridRawImage(grid) != null;
        }

        private static void FixBackground(NodeControl control, Image image, Vector2 targetSize)
        {
            Sprite sprite = image.sprite;
            Color color = image.color;
            bool raycast = image.raycastTarget;
            float ppu = (sprite != null && sprite.pixelsPerUnit > 0f) ? sprite.pixelsPerUnit : 100f;
            var go = image.gameObject;
            string goName = go.name;

            Texture2D tileTex = null;
            if (sprite != null)
            {
                try { tileTex = BakeSpriteToRepeatableTexture(sprite); }
                catch (System.Exception ex)
                {
                    Log?.LogWarning($"bake sprite failed: {ex.Message}; fallback procedural grid.");
                }
            }
            if (tileTex == null)
                tileTex = CreateProceduralGridTexture(64, 16);

            float tileUiW = tileTex.width / ppu;
            float tileUiH = tileTex.height / ppu;
            if (tileUiW < 1f) tileUiW = tileTex.width;
            if (tileUiH < 1f) tileUiH = tileTex.height;

            float uvX = Mathf.Max(1f, targetSize.x / tileUiW);
            float uvY = Mathf.Max(1f, targetSize.y / tileUiH);

            UnityEngine.Object.DestroyImmediate(image);
            var raw = go.GetComponent<RawImage>() ?? go.AddComponent<RawImage>();
            if (raw == null)
            {
                Log?.LogError($"AddComponent<RawImage> failed on '{goName}'.");
                return;
            }

            raw.texture = tileTex;
            raw.color = color;
            raw.raycastTarget = raycast;
            raw.uvRect = new Rect(0f, 0f, uvX, uvY);
            raw.enabled = true;
            BackgroundPixelsPerUnit[control.GetInstanceID()] = ppu;
            Log?.LogInfo($"background '{goName}' -> RawImage uv=({uvX:F1},{uvY:F1}), tile={tileTex.width}x{tileTex.height}");
        }

        private static void UpdateRawBackground(NodeControl control, RawImage raw, Vector2 targetSize)
        {
            if (raw == null || raw.texture == null)
                return;
            float ppu;
            if (!BackgroundPixelsPerUnit.TryGetValue(control.GetInstanceID(), out ppu) || ppu <= 0f)
                ppu = 100f;
            float tileUiW = raw.texture.width / ppu;
            float tileUiH = raw.texture.height / ppu;
            if (tileUiW < 1f) tileUiW = raw.texture.width;
            if (tileUiH < 1f) tileUiH = raw.texture.height;
            raw.uvRect = new Rect(0f, 0f,
                Mathf.Max(1f, targetSize.x / tileUiW),
                Mathf.Max(1f, targetSize.y / tileUiH));
        }

        private static RawImage FindGridRawImage(RectTransform grid)
        {
            var self = grid.GetComponent<RawImage>();
            if (self != null)
                return self;

            var raws = grid.GetComponentsInChildren<RawImage>(true);
            for (int i = 0; i < raws.Length; i++)
            {
                var raw = raws[i];
                if (raw == null) continue;
                string n = raw.gameObject.name ?? string.Empty;
                if (n == "grid" || n.ToLowerInvariant().Contains("grid"))
                    return raw;
            }

            RawImage best = null;
            float bestArea = 0f;
            for (int i = 0; i < raws.Length; i++)
            {
                var raw = raws[i];
                if (raw == null) continue;
                var rt = raw.transform as RectTransform;
                if (rt == null) continue;
                float area = Mathf.Abs(rt.sizeDelta.x * rt.sizeDelta.y);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = raw;
                }
            }
            return best;
        }

        private static Image FindGridImage(RectTransform grid)
        {
            var self = grid.GetComponent<Image>();
            if (self != null)
                return self;

            var images = grid.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                var img = images[i];
                if (img == null) continue;
                string n = img.gameObject.name ?? string.Empty;
                if (n == "grid" || n.ToLowerInvariant().Contains("grid"))
                    return img;
            }

            Image best = null;
            float bestArea = 0f;
            for (int i = 0; i < images.Length; i++)
            {
                var img = images[i];
                if (img == null) continue;
                var rt = img.transform as RectTransform;
                if (rt == null) continue;
                float area = Mathf.Abs(rt.sizeDelta.x * rt.sizeDelta.y);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = img;
                }
            }
            return best;
        }

        private static Texture2D BakeSpriteToRepeatableTexture(Sprite sprite)
        {
            Texture src = sprite.texture;
            if (src == null)
                return null;

            Rect tr = sprite.textureRect;
            Vector4 border = sprite.border;
            float x = tr.x + border.x;
            float y = tr.y + border.y;
            float w = tr.width - border.x - border.z;
            float h = tr.height - border.y - border.w;
            if (w < 2f || h < 2f)
            {
                x = tr.x; y = tr.y; w = tr.width; h = tr.height;
            }

            int ix = Mathf.Clamp(Mathf.FloorToInt(x), 0, src.width - 1);
            int iy = Mathf.Clamp(Mathf.FloorToInt(y), 0, src.height - 1);
            int iw = Mathf.Clamp(Mathf.FloorToInt(w), 1, src.width - ix);
            int ih = Mathf.Clamp(Mathf.FloorToInt(h), 1, src.height - iy);

            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;

            var tex = new Texture2D(iw, ih, TextureFormat.ARGB32, false);
            tex.name = "EC_NodeSidePanel_GridTile";
            tex.ReadPixels(new Rect(ix, iy, iw, ih), 0, 0);
            tex.Apply(false, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        private static Texture2D CreateProceduralGridTexture(int size, int step)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.name = "EC_NodeSidePanel_ProcGrid";
            var bg = new Color(0.12f, 0.12f, 0.14f, 0.85f);
            var line = new Color(0.35f, 0.35f, 0.4f, 0.9f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, (x % step == 0 || y % step == 0) ? line : bg);
            tex.Apply(false, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Point;
            return tex;
        }
    }
}
