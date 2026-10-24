using HarmonyLib;
using System;
using UnityEngine;

namespace EC_BackVarSync
{
    /// <summary>
    /// IMGUI 窗口鼠标输入拦截：面板区域内让 Input.GetMouseButton* 返回 false，
    /// 防止点面板按钮穿透成游戏点击（前进一个 CUT）。
    /// 做法 KKAPI.EatInputInRect 同源——直接 patch Input 读取口，
    /// 不引入 KKAPI 依赖（本插件需在无 KKAPI 环境独立运行）。
    /// 键盘穿透（输入框打字触发前进键）由 ChapterPanel 的焦点跟踪处理。
    /// </summary>
    internal static class IMGUIInputBlocker
    {
        private static Rect _area;
        private static bool _hasArea;

        /// <summary>面板 OnGUI 每帧登记窗口最终矩形（GUI 坐标系）。</summary>
        public static void SetArea(Rect area)
        {
            _area = area;
            _hasArea = true;
        }

        public static void ClearArea()
        {
            _hasArea = false;
        }

        private static bool IsBlocked()
        {
            if (!_hasArea)
                return false;

            // Input.mousePosition 是屏幕坐标（原点左下），GUI 坐标原点在左上
            Vector3 m = Input.mousePosition;
            return _area.Contains(new Vector2(m.x, Screen.height - m.y));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButton))]
        private static bool GetMouseButton_Prefix(ref bool __result)
        {
            if (!IsBlocked())
                return true;
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonDown))]
        private static bool GetMouseButtonDown_Prefix(ref bool __result)
        {
            if (!IsBlocked())
                return true;
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonUp))]
        private static bool GetMouseButtonUp_Prefix(ref bool __result)
        {
            if (!IsBlocked())
                return true;
            __result = false;
            return false;
        }
    }
}
