using HEdit;
using Manager;
using UnityEngine;

namespace EC_SceneAudit.Repair
{
    internal static class RepairPreflightContext
    {
        private const float ProbeInterval = 0.25f;

        private static NodeSettingCanvas cachedCanvas;
        private static float nextProbeAt;

        public static bool IsNodeOverviewActive()
        {
            if (IsSceneBusy())
                return false;

            // NodeSettingCanvas 查找开销大，按间隔缓存，与节点页判定保持一致。
            var now = Time.unscaledTime;
            if (now >= nextProbeAt || cachedCanvas == null)
            {
                nextProbeAt = now + ProbeInterval;
                try
                {
                    cachedCanvas = UnityEngine.Object.FindObjectOfType<NodeSettingCanvas>();
                }
                catch
                {
                    cachedCanvas = null;
                }
            }
            if (!IsCanvasNodeActive(cachedCanvas))
                return false;

            try
            {
                if (!Singleton<HEditGlobal>.IsInstance())
                    return false;
                var global = Singleton<HEditGlobal>.Instance;
                if (global == null || global.nodeControl == null)
                    return false;
                return true;
            }
            catch
            {
                cachedCanvas = null;
                return false;
            }
        }

        private static bool IsCanvasNodeActive(NodeSettingCanvas canvas)
        {
            if (canvas == null)
                return false;
            try
            {
                var group = canvas.CgNode;
                return canvas.gameObject.activeInHierarchy && group != null
                    && group.interactable && group.blocksRaycasts && group.alpha > 0.01f
                    && group.gameObject.activeInHierarchy;
            }
            catch
            {
                cachedCanvas = null;
                return false;
            }
        }

        private static bool IsSceneBusy()
        {
            try
            {
                if (!Singleton<Scene>.IsInstance())
                    return true;
                var scene = Singleton<Scene>.Instance;
                if (scene == null || scene.IsNowLoadingFade || scene.IsFadeNow)
                    return true;
                return scene.NowSceneNames == null || scene.NowSceneNames.Count == 0
                    || scene.NowSceneNames[0] != "HEditScene";
            }
            catch
            {
                return true;
            }
        }

    }
}
