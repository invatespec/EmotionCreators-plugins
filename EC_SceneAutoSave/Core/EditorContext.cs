using ADVCreate;
using HEdit;
using Manager;
using UnityEngine;

namespace EC_SceneAutoSave.Core
{
    // HEdit 当前状态的只读判定。返回 null 表示允许，否则返回给面板显示的原因。
    internal static class EditorContext
    {
        private const float ProbeInterval = 0.25f;

        private static NodeSettingCanvas cachedCanvas;
        private static float nextProbeAt;

        public static HEditData Data => Singleton<HEditData>.IsInstance() ? Singleton<HEditData>.Instance : null;

        public static HEditGlobal Global => Singleton<HEditGlobal>.IsInstance() ? Singleton<HEditGlobal>.Instance : null;

        public static bool IsTutorial()
        {
            return Singleton<global::Manager.Game>.IsInstance() && Singleton<global::Manager.Game>.Instance.isPlayTutorial;
        }

        // 场景栈：下标 0 是最上层的附加场景，基础场景在末尾。
        public static bool IsInHEdit()
        {
            if (Global == null || !Singleton<Scene>.IsInstance())
                return false;
            var names = Singleton<Scene>.Instance.NowSceneNames;
            return names != null && names.Contains("HEditScene");
        }

        // 读档、存档、地图、配置等窗口都以附加场景压在 HEditScene 之上。
        public static bool IsHEditTopScene()
        {
            if (!Singleton<Scene>.IsInstance())
                return false;
            var scene = Singleton<Scene>.Instance;
            if (scene == null || scene.IsNowLoadingFade || scene.IsFadeNow)
                return false;
            var names = scene.NowSceneNames;
            return names != null && names.Count > 0 && names[0] == "HEditScene";
        }

        public static string BlockReasonForAutoSave()
        {
            if (!IsHEditTopScene())
                return "窗口或加载画面打开中";
            if (IsTutorial())
                return "教程中";
            if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2))
                return "鼠标按住中";
            var global = Global;
            if (global == null || global.nodeControl == null)
                return "HEdit 未就绪";
            if (global.isDrag)
                return "拖动中";
            // 不看 IMGUI keyboardControl：其它插件残留的焦点会让自动保存永远等不到。
            if (global.isInputNow)
                return "输入框正在输入";
            if (global.baseSettingCanvas != null && global.baseSettingCanvas.gameObject.activeSelf)
                return "基础设置面板打开中";
            if (Singleton<ADVPartUICtrl>.IsInstance() && Singleton<ADVPartUICtrl>.Instance.preview)
                return "ADV 预览中";
            return null;
        }

        public static string BlockReasonForApply()
        {
            if (!IsNodeOverviewActive())
                return "请回到节点总览";
            if (Singleton<ADVPartUICtrl>.IsInstance() && !Singleton<ADVPartUICtrl>.Instance.pause)
                return "ADV 编辑器正在使用";
            var global = Global;
            if (global.isDrag || global.isInputNow)
                return "拖动或输入中";
            if (global.baseSettingCanvas != null && global.baseSettingCanvas.gameObject.activeSelf)
                return "基础设置面板打开中";
            return null;
        }

        public static NodeSettingCanvas NodeCanvas()
        {
            var now = Time.unscaledTime;
            if (now >= nextProbeAt || cachedCanvas == null)
            {
                nextProbeAt = now + ProbeInterval;
                cachedCanvas = Object.FindObjectOfType<NodeSettingCanvas>();
            }
            return cachedCanvas;
        }

        // 节点总览用 LoadSceneName 判断,不能用 SelectPart == null。
        public static bool IsNodeOverviewActive()
        {
            if (!IsHEditTopScene())
                return false;
            var canvas = NodeCanvas();
            if (canvas == null || !canvas.gameObject.activeInHierarchy)
                return false;
            var group = canvas.CgNode;
            if (group == null || !group.gameObject.activeInHierarchy || group.alpha <= 0.01f
                || !group.interactable || !group.blocksRaycasts)
                return false;
            var global = Global;
            return global != null && global.nodeControl != null;
        }
    }
}
