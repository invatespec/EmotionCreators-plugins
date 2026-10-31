using System.Collections.Generic;
using System.Reflection;
using ADVCreate;
using ADVPart.List;
using ADVPart.Manipulate;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EC_ADVBatchTransform
{
    using Cut = HEdit.ADVPart.Cut;
    using SortOrder = HEdit.ADVPart.SortOrder;
    using SpeechBubbles = HEdit.ADVPart.SpeechBubbles;
    using UICtrl = TextAndEffectListUICtrl.UICtrl;

    // ADV Part 编辑器的取值入口，其他模块不直接碰单例和反射
    internal static class EditorContext
    {
        private static FieldInfo listField;
        private static bool listFieldResolved;
        private static bool tabWarned;
        private static ADVPartUICtrl cachedUI;
        private static TextAndEffectListUICtrl cachedList;
        private static CanvasGroup cachedTabGroup;

        internal static ADVPartUICtrl UI
        {
            get { return Singleton<ADVPartUICtrl>.IsInstance() ? Singleton<ADVPartUICtrl>.Instance : null; }
        }

        // SelectPart 在节点页点编辑时就已赋值，进入编辑器的信号由 PartHooks 补充
        internal static bool InPart
        {
            get
            {
                if (UI == null || !Singleton<HEdit.HEditGlobal>.IsInstance()) return false;
                var part = Singleton<HEdit.HEditGlobal>.Instance.SelectPart;
                return part != null && part.kind == 1 && UI.cut != null;
            }
        }

        // 预览、角色姿势编辑、隐藏 UI 时 pause 为 true，红框不可见，不响应批量操作；
        // 只在文本/特效页工作，其他页上的按键交还给原生
        internal static bool CanOperate
        {
            get
            {
                if (!InPart) return false;
                var ui = UI;
                return !ui.preview && !ui.pause && ui.dicObject.Count > 0 && OnTextEffectTab;
            }
        }

        // 切页时原生 Tab.Enable 改这一页 CanvasGroup 的 alpha 和 blocksRaycasts。取不到时不限制页面
        internal static bool OnTextEffectTab
        {
            get
            {
                if (UI == null) return false;
                Resolve(UI);
                if (cachedTabGroup == null) return true;
                return cachedTabGroup.alpha > 0f && cachedTabGroup.blocksRaycasts;
            }
        }

        // 撤销记录只看页面：预览、隐藏 UI、编辑角色时数值仍可改，也要能撤销
        internal static bool InTextEffectPage => InPart && OnTextEffectTab;

        internal static Cut Cut => UI?.cut;

        internal static SortOrder Primary => UI?.sortOrder;

        internal static Dictionary<SortOrder, UICtrl> Objects => UI?.dicObject;

        internal static UICtrl GetCtrl(SortOrder so)
        {
            var objects = Objects;
            if (so == null || objects == null) return null;
            objects.TryGetValue(so, out var ctrl);
            return ctrl;
        }

        internal static bool HasCtrl(SortOrder so) => GetCtrl(so) != null;

        internal static TextAndEffectListUICtrl List
        {
            get
            {
                var ui = UI;
                if (ui == null) return null;
                Resolve(ui);
                return cachedList;
            }
        }

        internal static TextUICtrl TextPanel => List?.listUICtrl != null ? List.textUICtrl : null;

        internal static EffectUICtrl EffectPanel => List?.listUICtrl != null ? List.effectUICtrl : null;

        internal static TextLayoutUICtrl LayoutPanel
        {
            get
            {
                var panel = TextPanel;
                return panel != null ? panel.textLayoutUICtrl : null;
            }
        }

        // 主对象是气泡且点开了 Text1/Text2 时返回槽位序号，否则 -1
        internal static int ActiveSlot
        {
            get
            {
                if (!(Primary is SpeechBubbles)) return -1;
                var layout = LayoutPanel;
                if (layout == null) return -1;
                if (layout.Check(0)) return 0;
                return layout.Check(1) ? 1 : -1;
            }
        }

        internal static bool IsTyping
        {
            get
            {
                var ui = UI;
                if (ui != null && ui.isInputNow) return true;
                if (Singleton<HEdit.HEditGlobal>.IsInstance() && Singleton<HEdit.HEditGlobal>.Instance.isInputNow) return true;
                return FocusedInputField();
            }
        }

        internal static Transform CanvasTransform
        {
            get
            {
                if (!Singleton<ADV.ADV>.IsInstance()) return null;
                var canvas = Singleton<ADV.ADV>.Instance.canvas;
                return canvas != null ? canvas.transform : null;
            }
        }

        private static bool FocusedInputField()
        {
            var es = EventSystem.current;
            if (es == null) return false;
            var go = es.currentSelectedGameObject;
            if (go == null) return false;
            var input = go.GetComponent<InputField>();
            if (input != null && input.isFocused) return true;
            var tmp = go.GetComponent<TMP_InputField>();
            return tmp != null && tmp.isFocused;
        }

        private static void Resolve(ADVPartUICtrl ui)
        {
            if (ui == cachedUI) return;
            cachedUI = ui;
            var list = ResolveListUICtrl(ui);
            cachedList = list != null ? list.textAndEffectListUICtrl : null;
            cachedTabGroup = list != null ? ResolveTabGroup(list) : null;
        }

        private static ListUICtrl ResolveListUICtrl(ADVPartUICtrl ui)
        {
            if (!listFieldResolved)
            {
                listFieldResolved = true;
                listField = AccessTools.Field(typeof(ADVPartUICtrl), "listUICtrl");
                if (listField == null)
                    ADVBatchTransformPlugin.Log.LogWarning("未找到 ADVPartUICtrl.listUICtrl，列表高亮与面板刷新停用");
            }
            return listField?.GetValue(ui) as ListUICtrl;
        }

        // ListUICtrl.tabs 依次是 cut、文本/特效、部件信息；Tab 是私有嵌套类，按字段名取 canvasGroup
        private static CanvasGroup ResolveTabGroup(ListUICtrl list)
        {
            var tabs = AccessTools.Field(typeof(ListUICtrl), "tabs")?.GetValue(list) as System.Array;
            object tab = tabs != null && tabs.Length > 1 ? tabs.GetValue(1) : null;
            var group = tab != null ? AccessTools.Field(tab.GetType(), "canvasGroup")?.GetValue(tab) as CanvasGroup : null;
            if (group == null && !tabWarned)
            {
                tabWarned = true;
                ADVBatchTransformPlugin.Log.LogWarning("未找到文本/特效页的 CanvasGroup，不按页面限制插件功能");
            }
            return group;
        }
    }
}
