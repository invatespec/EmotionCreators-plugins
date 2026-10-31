using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace EC_MapLimitUnlock
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class MapLimitUnlockPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.rainbowing.ec.maplimitunlock";
        public const string PluginName = "EC_MapLimitUnlock";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            MapLimitHooks.Apply(new Harmony(PluginGuid));
            Log.LogInfo(PluginName + " " + PluginVersion + " 已启动，地图上限 " + MapLimitHooks.MapLimit);
        }
    }
}
