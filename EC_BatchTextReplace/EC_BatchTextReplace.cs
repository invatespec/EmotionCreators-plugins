using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EC_BatchTextReplace
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    public class BatchTextReplacePlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_BatchTextReplace";
        public const string PluginName = "Batch Text Replace";
        public const string Version = "1.1.1";

        internal static ManualLogSource Log;

        private const string SceneName_HEditScene = "HEditScene";

        private ConfigEntry<KeyCode> _toggleKey;

        private bool _showWindow;
        private Rect _windowRect = new Rect(200f, 200f, 360f, 160f);
        private string _findText = "";
        private string _replaceText = "";

        // 缓存反射字段：ADV.dicText (private)
        private static readonly FieldInfo DicTextField =
            typeof(ADV.ADV).GetField("dicText", BindingFlags.NonPublic | BindingFlags.Instance);

        internal void Awake()
        {
            Log = Logger;

            _toggleKey = Config.Bind("Hotkeys", "Toggle Window",
                KeyCode.F5,
                "打开/关闭批量文本替换窗口");

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            enabled = IsHeditSceneLoaded();

            Log.LogInfo("BatchTextReplace loaded.");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == SceneName_HEditScene)
                enabled = true;
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (scene.name != SceneName_HEditScene) return;

            _showWindow = false;
            enabled = false;
        }

        private static bool IsHeditSceneLoaded()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).name == SceneName_HEditScene)
                    return true;
            }

            return false;
        }

        private void Update()
        {
            if (!enabled) return;
            if (Input.GetKeyDown(_toggleKey.Value))
                _showWindow = !_showWindow;
        }

        private void OnGUI()
        {
            if (!_showWindow) return;

            _windowRect = GUILayout.Window(0x425452, _windowRect, DrawWindow, "Batch Text Replace");
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            GUILayout.Label("Find:");
            _findText = GUILayout.TextField(_findText, GUILayout.Width(320));

            GUILayout.Label("Replace:");
            _replaceText = GUILayout.TextField(_replaceText, GUILayout.Width(320));

            if (GUILayout.Button("Replace All", GUILayout.Height(28)))
                DoReplace();

            GUILayout.EndVertical();

            if (GUI.Button(new Rect(_windowRect.width - 22, 2, 20, 20), "X"))
                _showWindow = false;

            GUI.DragWindow();
        }

        private void DoReplace()
        {
            if (string.IsNullOrEmpty(_findText))
            {
                Log.LogWarning("Find text is empty.");
                return;
            }

            var heditData = HEdit.HEditData.Instance;
            if (heditData == null)
            {
                Log.LogWarning("HEditData not available.");
                return;
            }

            var nodes = heditData.nodes;
            if (nodes == null)
            {
                Log.LogWarning("HEdit node data not available.");
                return;
            }

            // nodeControl 用于取 NodeUI.nodeBase.name（Part 在节点画布上的显示名）
            var nodeControl = HEdit.HEditGlobal.Instance != null
                ? HEdit.HEditGlobal.Instance.nodeControl
                : null;
            var dictNode = nodeControl?.dictNode;

            // 获取 ADV 单例和 dicText 用于渲染层同步（可能为 null）
            var advInstance = ADV.ADV.Instance;
            var dicText = (advInstance != null && DicTextField != null)
                ? DicTextField.GetValue(advInstance) as Dictionary<HEdit.ADVPart.SpeechBubbles, GameObject>
                : null;

            int count = 0;
            foreach (var kvp in nodes)
            {
                var part = kvp.Value;
                if (part == null || part.kind != 1) continue;

                string partName = kvp.Key; // 回退到 uuId
                if (dictNode != null && dictNode.TryGetValue(part.uuId, out var nodeUI) && nodeUI?.nodeBase != null)
                    partName = nodeUI.nodeBase.name;

                count += ReplaceInPart(part as HEdit.ADVPart, partName, dicText);
            }

            Log.LogInfo($"Batch replace done: {count} text(s) modified.");
        }

        private int ReplaceInPart(
            HEdit.ADVPart advPart,
            string partName,
            Dictionary<HEdit.ADVPart.SpeechBubbles, GameObject> dicText)
        {
            if (advPart?.cuts == null) return 0;

            int count = 0;
            for (int cutIndex = 0; cutIndex < advPart.cuts.Count; cutIndex++)
                count += ReplaceInCut(advPart.cuts[cutIndex], partName, cutIndex, dicText);

            return count;
        }

        private int ReplaceInCut(
            HEdit.ADVPart.Cut cut,
            string partName,
            int cutIndex,
            Dictionary<HEdit.ADVPart.SpeechBubbles, GameObject> dicText)
        {
            if (cut?.speechBubbles == null) return 0;

            int count = 0;
            for (int bubbleIndex = 0; bubbleIndex < cut.speechBubbles.Count; bubbleIndex++)
            {
                count += ReplaceInBubble(
                    cut.speechBubbles[bubbleIndex], partName, cutIndex, bubbleIndex, dicText);
            }

            return count;
        }

        private int ReplaceInBubble(
            HEdit.ADVPart.SpeechBubbles bubble,
            string partName,
            int cutIndex,
            int bubbleIndex,
            Dictionary<HEdit.ADVPart.SpeechBubbles, GameObject> dicText)
        {
            if (bubble?.textLayouts == null) return 0;

            int count = 0;
            for (int textIndex = 0; textIndex < bubble.textLayouts.Length; textIndex++)
            {
                var textLayout = bubble.textLayouts[textIndex];
                if (textLayout == null || string.IsNullOrEmpty(textLayout.msg) ||
                    !textLayout.msg.Contains(_findText))
                {
                    continue;
                }

                string oldMessage = textLayout.msg;
                textLayout.msg = oldMessage.Replace(_findText, _replaceText);
                count++;

                Log.LogInfo(
                    $"[{partName}] Cut#{cutIndex} Bubble#{bubbleIndex} TL#{textIndex}: " +
                    $"\"{oldMessage}\" → \"{textLayout.msg}\"");
                SyncRenderedText(dicText, bubble, textIndex, textLayout.msg);
            }

            return count;
        }

        private static void SyncRenderedText(
            Dictionary<HEdit.ADVPart.SpeechBubbles, GameObject> dicText,
            HEdit.ADVPart.SpeechBubbles bubble,
            int textInfoIndex,
            string newMsg)
        {
            if (dicText == null) return;
            if (!dicText.TryGetValue(bubble, out var go) || go == null) return;

            var tc = go.GetComponent<ADV.TextComponent>();
            if (tc?.textInfos == null || textInfoIndex >= tc.textInfos.Length) return;

            var ti = tc.textInfos[textInfoIndex];
            if (ti != null)
                ti.msg = newMsg;
        }

    }
}
