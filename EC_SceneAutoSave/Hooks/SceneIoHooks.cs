using System;
using HarmonyLib;
using HEdit;
using YS_Node;

namespace EC_SceneAutoSave.Hooks
{
    // 原生读档、存档、场景重置的统一入口；钩子内异常不能影响原生流程。
    internal static class SceneIoHooks
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HEditData), nameof(HEditData.Load), typeof(string), typeof(NodeControl), typeof(bool))]
        private static void LoadPrefix(bool _isEdit)
        {
            if (_isEdit)
                Guard(() => SceneAutoSavePlugin.Controller.OnBeforeLoad());
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HEditData), nameof(HEditData.Load), typeof(string), typeof(NodeControl), typeof(bool))]
        private static void LoadPostfix(string _path, bool _isEdit, bool __result)
        {
            if (_isEdit)
                Guard(() => SceneAutoSavePlugin.Controller.OnDataLoaded(_path, __result));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HEditData), nameof(HEditData.Save), typeof(string), typeof(NodeControl), typeof(bool))]
        private static void SavePostfix(string _path, bool __result)
        {
            if (__result)
                Guard(() => SceneAutoSavePlugin.Controller.OnDataSaved(_path));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HEditData), nameof(HEditData.Init))]
        private static void InitPostfix()
        {
            Guard(() => SceneAutoSavePlugin.Controller.OnDataReset());
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NodeControl), nameof(NodeControl.RebuildNodeUI))]
        private static void RebuildNodeUIPostfix(bool __result)
        {
            Guard(() => SceneAutoSavePlugin.Controller.OnNodeUiRebuilt(__result));
        }

        internal static void Guard(Action action)
        {
            try
            {
                if (SceneAutoSavePlugin.Controller != null)
                    action();
            }
            catch (Exception ex)
            {
                SceneAutoSavePlugin.Log.LogError("Autosave hook failed: " + ex);
            }
        }
    }
}
