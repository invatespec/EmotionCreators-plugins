using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace EC_ItemParentPreserve
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ItemParentPreservePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.ec.itemparentpreserve";
        public const string PluginName = "EC_ItemParentPreserve";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> EnableByDefault;

        // 运行时开关,由注入的 Toggle 改写;UI 注入失败时保持配置默认值
        internal static bool IsPreserveEnabled = true;

        private void Awake()
        {
            Log = Logger;
            EnableByDefault = Config.Bind("General", "EnableByDefault", true, "保持位置开关的默认状态");
            IsPreserveEnabled = EnableByDefault.Value;

            new Harmony(PluginGuid).PatchAll(typeof(Hooks));
            Log.LogInfo(PluginName + " " + PluginVersion + " 已启动");
        }
    }
}
