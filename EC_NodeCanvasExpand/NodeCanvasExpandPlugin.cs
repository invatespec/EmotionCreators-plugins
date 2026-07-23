using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using YS_Node;

namespace EC_NodeCanvasExpand
{
    // 动机：原版 rtfGrid 固定尺寸；扩后需 pan 边界、中心缩放、网格不拉伸、能缩到看全图。
    // 网格原版就是 Image(Tiled)+"grid"；size 放大后 tile 数爆掉，改 bake 贴图 + RawImage.uvRect。
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    public sealed class NodeCanvasExpandPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_NodeCanvasExpand";
        public const string PluginName = "EC Node Canvas Expand";
        public const string Version = "1.2.1";

        // 原版 gridScale [0.5,1] → localScale=1.5-gridScale ∈ [0.5,1]
        private const float GridScaleMin = 0.5f; // 最大放大：localScale=1
        private const float DefaultZoomInScale = 1f;

        internal static ManualLogSource Log;
        internal static ConfigEntry<float> SizeMultiplier;

        private static readonly HashSet<int> ExpandedIds = new HashSet<int>();
        private static readonly AccessTools.FieldRef<NodeControl, float> GridScaleRef =
            AccessTools.FieldRefAccess<NodeControl, float>("gridScale");
        private static readonly AccessTools.FieldRef<NodeControl, Vector2> VGridPosRef =
            AccessTools.FieldRefAccess<NodeControl, Vector2>("vGridPos");
        private static readonly AccessTools.FieldRef<NodeControl, float> ZoomSpeedRef =
            AccessTools.FieldRefAccess<NodeControl, float>("zoomSpeed");

        // 每个 NodeControl 允许的最小 localScale（缩到能装下整张画布）
        private static readonly Dictionary<int, float> MinLocalScale = new Dictionary<int, float>();

        private void Awake()
        {
            Log = Logger;
            SizeMultiplier = Config.Bind(
                "Canvas",
                "SizeMultiplier",
                5f,
                new ConfigDescription(
                    "节点画布 rtfGrid 尺寸乘数（相对原版 sizeDelta）",
                    new AcceptableValueRange<float>(1f, 10f)));

            var harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(Hooks));
            Log.LogInfo($"{PluginName} v{Version} loaded. multiplier={SizeMultiplier.Value}");
        }

        private static class Hooks
        {
            [HarmonyPostfix]
            [HarmonyPatch(typeof(NodeControl), "Awake")]
            private static void NodeControlAwakePostfix(NodeControl __instance)
            {
                // 背景处理失败绝不能打断 NodeControl.Awake，否则存档 RebuildNodeUI 会连环 NRE。
                try
                {
                    TryExpand(__instance);
                }
                catch (System.Exception ex)
                {
                    Log.LogError($"TryExpand failed (canvas may be partially expanded): {ex}");
                }
            }

            // vGridPos 现为真实 anchoredPosition，中键拖拽 1:1。
            [HarmonyPrefix]
            [HarmonyPatch(typeof(NodeControl), "GridDragFunc")]
            private static bool GridDragFuncPrefix(NodeControl __instance, Vector2 move)
            {
                Vector2 v = VGridPosRef(__instance);
                v += move;
                VGridPosRef(__instance) = v;
                return false;
            }

            // 替换 Update：中心缩放；pan 钳制；缩放下限动态到「整图可见」。
            [HarmonyPrefix]
            [HarmonyPatch(typeof(NodeControl), "Update")]
            private static bool NodeControlUpdatePrefix(NodeControl __instance)
            {
                var rtfGrid = __instance.rtfGrid;
                if (rtfGrid == null)
                    return false;

                var parent = rtfGrid.parent as RectTransform;
                if (parent == null)
                    return false;

                float zoomSpeed = ZoomSpeedRef(__instance);
                float gridScale = GridScaleRef(__instance);
                Vector2 vGridPos = VGridPosRef(__instance);

                float minLocal = GetMinLocalScale(__instance, rtfGrid, parent);
                float maxLocal = DefaultZoomInScale; // 1.0
                // gridScale = 1.5 - localScale → local 越小 gridScale 越大
                float scaleMin = minLocal;
                float scaleMax = maxLocal;

                float oldScale = ScaleFromGridScale(gridScale);
                float delta = Input.GetAxis("Mouse ScrollWheel") * -zoomSpeed;
                if (!Mathf.Approximately(delta, 0f))
                {
                    // 滚轮仍改 gridScale，再反算 clamp 到 [minLocal, maxLocal]
                    gridScale = Mathf.Clamp(gridScale + delta, GridScaleMin, 1.5f - scaleMin);
                    float newScale = Mathf.Clamp(ScaleFromGridScale(gridScale), scaleMin, scaleMax);
                    gridScale = 1.5f - newScale;

                    if (oldScale > 1e-4f)
                        vGridPos *= newScale / oldScale;
                }

                float scale = Mathf.Clamp(ScaleFromGridScale(gridScale), scaleMin, scaleMax);
                gridScale = 1.5f - scale;

                Vector2 maxPan = ComputeMaxPan(rtfGrid, parent, scale);
                vGridPos.x = Mathf.Clamp(vGridPos.x, -maxPan.x, maxPan.x);
                vGridPos.y = Mathf.Clamp(vGridPos.y, -maxPan.y, maxPan.y);

                rtfGrid.anchoredPosition = vGridPos;
                rtfGrid.localScale = new Vector3(scale, scale, 1f);

                GridScaleRef(__instance) = gridScale;
                VGridPosRef(__instance) = vGridPos;
                return false;
            }
        }

        private static float ScaleFromGridScale(float gridScale)
        {
            return 1.5f - gridScale;
        }

        private static float GetMinLocalScale(NodeControl control, RectTransform grid, RectTransform viewport)
        {
            int id = control.GetInstanceID();
            float cached;
            if (MinLocalScale.TryGetValue(id, out cached))
                return cached;

            float fit = ComputeFitScale(grid, viewport);
            // 略小于 fit，保证整图可见并留边；下限防止滚到看不见
            float minLocal = Mathf.Clamp(fit * 0.95f, 0.02f, 0.5f);
            MinLocalScale[id] = minLocal;
            return minLocal;
        }

        // 缩放到 localScale 后整张 grid 刚好落入视口。
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

        private static Vector2 ComputeMaxPan(RectTransform grid, RectTransform viewport, float scale)
        {
            Vector2 view = viewport.rect.size;
            if (view.x <= 0f || view.y <= 0f)
                view = viewport.sizeDelta;

            Vector2 scaled = grid.sizeDelta * scale;
            return new Vector2(
                Mathf.Max(0f, (scaled.x - view.x) * 0.5f),
                Mathf.Max(0f, (scaled.y - view.y) * 0.5f));
        }

        private static void TryExpand(NodeControl control)
        {
            if (control == null || control.rtfGrid == null)
                return;

            int id = control.GetInstanceID();
            if (!ExpandedIds.Add(id))
                return;

            float mul = SizeMultiplier != null ? SizeMultiplier.Value : 5f;
            if (mul <= 1.001f)
                return;

            var grid = control.rtfGrid;
            Vector2 before = grid.sizeDelta;
            if (before.x <= 0f || before.y <= 0f)
            {
                Log.LogWarning($"rtfGrid.sizeDelta invalid: {before}, skip expand.");
                ExpandedIds.Remove(id);
                return;
            }

            EnsureCenterPivot(grid);

            Vector2 after = before * mul;
            grid.sizeDelta = after;

            // 视口尺寸可能在 Awake 时已可用；预缓存最小缩放
            var parent = grid.parent as RectTransform;
            if (parent != null)
            {
                float fit = ComputeFitScale(grid, parent);
                float minLocal = Mathf.Clamp(fit * 0.95f, 0.02f, 0.5f);
                MinLocalScale[id] = minLocal;
                Log.LogInfo($"zoom-out floor localScale={minLocal:F3} (fit={fit:F3}) to show full canvas");
            }

            FixBackground(grid, after);

            Log.LogInfo($"node canvas expanded x{mul}: {before} -> {after}");
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

        // 原版 Image(Tiled) 在大 sizeDelta 下会 "Too many sprite tiles"。
        // Unity 同一 GO 只能有一个 Graphic：必须 Destroy Image 再挂 RawImage。
        private static void FixBackground(RectTransform grid, Vector2 targetSize)
        {
            var image = FindGridImage(grid);
            if (image == null)
            {
                Log.LogWarning("grid Image not found under rtfGrid; background unchanged.");
                return;
            }

            Sprite sprite = image.sprite;
            Color color = image.color;
            bool raycast = image.raycastTarget;
            float ppu = (sprite != null && sprite.pixelsPerUnit > 0f) ? sprite.pixelsPerUnit : 100f;
            var go = image.gameObject;
            string goName = go.name;

            Texture2D tileTex = null;
            if (sprite != null)
            {
                try
                {
                    tileTex = BakeSpriteToRepeatableTexture(sprite);
                }
                catch (System.Exception ex)
                {
                    Log.LogWarning($"bake sprite failed: {ex.Message}; fallback procedural grid.");
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

            // Image 与 RawImage 互斥；同帧要用 DestroyImmediate 才能立刻 AddComponent。
            Object.DestroyImmediate(image);

            var raw = go.GetComponent<RawImage>();
            if (raw == null)
                raw = go.AddComponent<RawImage>();
            if (raw == null)
            {
                Log.LogError($"AddComponent<RawImage> failed on '{goName}'.");
                return;
            }

            raw.texture = tileTex;
            raw.color = color;
            raw.raycastTarget = raycast;
            raw.uvRect = new Rect(0f, 0f, uvX, uvY);
            raw.enabled = true;

            Log.LogInfo($"background '{goName}' -> RawImage uv=({uvX:F1},{uvY:F1}), tile={tileTex.width}x{tileTex.height}");
        }

        private static Image FindGridImage(RectTransform grid)
        {
            // 优先自身（层级 UI/.../grid 即 rtfGrid）
            var self = grid.GetComponent<Image>();
            if (self != null)
                return self;

            var images = grid.GetComponentsInChildren<Image>(true);
            Image named = null;
            for (int i = 0; i < images.Length; i++)
            {
                var img = images[i];
                if (img == null)
                    continue;
                string n = img.gameObject.name ?? string.Empty;
                if (n == "grid" || n.ToLowerInvariant().Contains("grid"))
                {
                    named = img;
                    break;
                }
            }
            if (named != null)
                return named;

            // 回退：最大尺寸的 Image（背景通常最大）
            Image best = null;
            float bestArea = 0f;
            for (int i = 0; i < images.Length; i++)
            {
                var img = images[i];
                if (img == null)
                    continue;
                var rt = img.transform as RectTransform;
                if (rt == null)
                    continue;
                float area = Mathf.Abs(rt.sizeDelta.x * rt.sizeDelta.y);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = img;
                }
            }
            return best;
        }

        // Blit 整张 atlas 再 ReadPixels 裁切 sprite 区域 → 独立可 Repeat 贴图。
        private static Texture2D BakeSpriteToRepeatableTexture(Sprite sprite)
        {
            Texture src = sprite.texture;
            if (src == null)
                return null;

            Rect tr = sprite.textureRect;
            // 有 border 时取中心可平铺区，避免 9-slice 边框参与重复
            Vector4 border = sprite.border; // L,B,R,T in pixels
            float x = tr.x + border.x;
            float y = tr.y + border.y;
            float w = tr.width - border.x - border.z;
            float h = tr.height - border.y - border.w;
            if (w < 2f || h < 2f)
            {
                x = tr.x;
                y = tr.y;
                w = tr.width;
                h = tr.height;
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
            tex.name = "EC_NodeCanvasExpand_GridTile";
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
            tex.name = "EC_NodeCanvasExpand_ProcGrid";
            var bg = new Color(0.12f, 0.12f, 0.14f, 0.85f);
            var line = new Color(0.35f, 0.35f, 0.4f, 0.9f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool isLine = (x % step == 0) || (y % step == 0);
                    tex.SetPixel(x, y, isLine ? line : bg);
                }
            }
            tex.Apply(false, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Point;
            return tex;
        }
    }
}
