using BepInEx.Configuration;
using UnityEngine;

namespace EC_SceneAutoSave
{
    internal sealed class AutoSaveSettings
    {
        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<int> IdleSeconds;
        public readonly ConfigEntry<int> MinIntervalSeconds;
        public readonly ConfigEntry<int> SessionsPerScene;
        public readonly ConfigEntry<KeyCode> ToggleKey;
        public readonly ConfigEntry<float> WindowX;
        public readonly ConfigEntry<float> WindowY;

        public AutoSaveSettings(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true,
                "在场景编辑中空闲时自动记录改动。关闭后仍可在面板中手动立即保存。");
            IdleSeconds = config.Bind("General", "Idle Seconds", 10,
                new ConfigDescription("没有鼠标、键盘操作多少秒后触发自动保存。", new AcceptableValueRange<int>(3, 120)));
            MinIntervalSeconds = config.Bind("General", "Min Interval Seconds", 180,
                new ConfigDescription("两次自动保存之间的最短间隔（秒）。", new AcceptableValueRange<int>(30, 3600)));
            SessionsPerScene = config.Bind("General", "Sessions Per Scene", 3,
                new ConfigDescription("每个场景存档保留的自动保存会话数量。", new AcceptableValueRange<int>(1, 10)));
            ToggleKey = config.Bind("Window", "Toggle Key", KeyCode.F4, "打开或关闭自动保存面板。");
            WindowX = config.Bind("Window", "Position X", 0.98f,
                new ConfigDescription("面板水平位置，0 为最左，1 为最右。", new AcceptableValueRange<float>(0f, 1f)));
            WindowY = config.Bind("Window", "Position Y", 0.12f,
                new ConfigDescription("面板垂直位置，0 为最上，1 为最下。", new AcceptableValueRange<float>(0f, 1f)));
        }
    }
}
