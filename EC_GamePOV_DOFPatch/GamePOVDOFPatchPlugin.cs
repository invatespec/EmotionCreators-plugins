using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Config;
using HarmonyLib;
using Manager;

namespace EC_GamePOV_DOFPatch
{
    // 动机：EC_GamePOV 把 CameraDir=0 + TargetPos 钉眼点 → DOF 焦点距≈0 全糊。
    // 全局开关 Config.EtcData.DepthOfField 在 CameraEffectorConfig 每帧 AND 合成里是总闸：
    //   effector.useDOF = EtcData.DepthOfField && config.useDOF
    // SetImageEffect 只改场景级 config.useDOF，不碰 EtcData → 边沿写一次即可，无需每帧强制。
    // 不调用 Config.Save()，避免把临时关闭写进 system.xml。
    [BepInProcess("EmotionCreators")]
    [BepInDependency("EC_GamePOV", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(GUID, PluginName, Version)]
    public sealed class GamePOVDOFPatchPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_GamePOV_DOFPatch";
        public const string PluginName = "EC GamePOV DOF Patch";
        public const string Version = "1.0.1";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> EnablePatch;

        private FieldInfo _povEnabledField;
        private bool _resolved;
        private bool _active;
        private bool _backupGlobalDOF;

        private void Awake()
        {
            Log = Logger;
            EnablePatch = Config.Bind(
                "General", "EnablePatch", true,
                "POV 开启时临时关闭全局景深（Config.EtcData.DepthOfField）；关闭 POV 后还原。不写配置文件。");

            TryResolveGamePOV();
            Log.LogInfo($"{PluginName} v{Version} loaded. EnablePatch={EnablePatch.Value} GamePOV={(_povEnabledField != null)}");
        }

        private void LateUpdate()
        {
            if (!EnablePatch.Value)
            {
                if (_active)
                    RestoreDOF();
                return;
            }

            if (!_resolved)
                TryResolveGamePOV();
            if (_povEnabledField == null)
                return;

            bool povOn;
            try
            {
                povOn = (bool)_povEnabledField.GetValue(null);
            }
            catch (Exception ex)
            {
                Log.LogWarning($"read povEnabled failed: {ex.Message}");
                _povEnabledField = null;
                return;
            }

            if (povOn)
            {
                if (_active)
                    return;

                var etc = GetEtcData();
                if (etc == null)
                    return;

                _backupGlobalDOF = etc.DepthOfField;
                etc.DepthOfField = false;
                _active = true;
                Log.LogDebug($"POV on → global DOF off (was {_backupGlobalDOF})");
            }
            else if (_active)
            {
                RestoreDOF();
            }
        }

        private void OnDestroy()
        {
            if (_active)
                RestoreDOF();
        }

        private void RestoreDOF()
        {
            var etc = GetEtcData();
            if (etc != null)
                etc.DepthOfField = _backupGlobalDOF;
            _active = false;
            Log.LogDebug($"POV off → global DOF restore {_backupGlobalDOF}");
        }

        private void TryResolveGamePOV()
        {
            _resolved = true;
            try
            {
                var type = AccessTools.TypeByName("EC_GamePOV.EC_GamePOV")
                           ?? Type.GetType("EC_GamePOV.EC_GamePOV, EC_GamePOV");
                if (type == null)
                {
                    Log.LogInfo("EC_GamePOV not found; patch idle.");
                    return;
                }

                _povEnabledField = AccessTools.Field(type, "povEnabled");
                if (_povEnabledField == null || !_povEnabledField.IsStatic)
                {
                    Log.LogWarning("EC_GamePOV.povEnabled not found; patch idle.");
                    _povEnabledField = null;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"resolve EC_GamePOV failed: {ex.Message}");
                _povEnabledField = null;
            }
        }

        private static EtceteraSystem GetEtcData()
        {
            // Manager.Config.Start 后才 initialized；POV 只在 HPlay 出现，此时必已就绪。
            if (!Manager.Config.initialized)
                return null;
            return Manager.Config.EtcData;
        }
    }
}
