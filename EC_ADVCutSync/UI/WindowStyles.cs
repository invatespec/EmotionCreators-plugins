using System;
using System.Collections.Generic;
using UnityEngine;

namespace EC_ADVCutSync
{
    // 本窗口私有样式：只克隆 GUI.skin，禁止写入共享 skin（会污染同帧其它 IMGUI 插件窗口）
    internal sealed class WindowStyles : IDisposable
    {
        // 底带配色（EC_LogFilter 同族深蓝灰，避免纯黑；多层绘制用 Band）
        internal static readonly Color BaseBg = new Color(0.10f, 0.12f, 0.16f, 1f);
        internal static readonly Color HeaderBg = new Color(0.15f, 0.19f, 0.25f, 1f);

        private static readonly Color32 ControlFill = new Color32(0xD7, 0xDF, 0xE8, 0xFF);
        private static readonly Color32 ControlHover = new Color32(0xE9, 0xEF, 0xF5, 0xFF);
        private static readonly Color32 ControlActive = new Color32(0xBE, 0xC8, 0xD3, 0xFF);
        private static readonly Color32 ControlText = new Color32(0x12, 0x18, 0x1F, 0xFF);
        private static readonly Color32 BodyText = new Color32(0xD5, 0xDC, 0xE4, 0xFF);
        private static readonly Color32 StrongText = new Color32(0xF2, 0xF6, 0xFA, 0xFF);
        private static readonly Color32 WarnText = new Color32(0xF0, 0xC0, 0x70, 0xFF);

        private readonly List<Texture2D> textures = new List<Texture2D>();

        public GUIStyle Window;
        public GUIStyle Header;
        public GUIStyle Label;
        public GUIStyle Warning;
        public GUIStyle Button;
        public GUIStyle Row;
        public GUIStyle Detail;

        // 共享 1x1 白纹理由 Unity 内建提供；Band 每次绘制后恢复 GUI.color
        internal static void Band(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        public void Ensure()
        {
            if (Window != null)
                return;
            // 窗口本体不画背景，背景由 DrawWindow 回调内的多层 Band 负责
            Window = new GUIStyle { padding = new RectOffset(12, 12, 10, 12), margin = new RectOffset(0, 0, 0, 0) };

            // fontSize=0 表示默认字号，不能直接加减，显式指定
            Label = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 14 };
            SetText(Label, BodyText);
            Header = new GUIStyle(Label) { fontSize = 16, fontStyle = FontStyle.Bold };
            SetText(Header, StrongText);
            Warning = new GUIStyle(Label);
            SetText(Warning, WarnText);

            // 按钮用 1x1 纯色：3x3 九宫格贴图默认双线性过滤，拉伸后中心色与边框色混成渐变
            Button = new GUIStyle(GUI.skin.button) { fontSize = 14, padding = new RectOffset(8, 8, 4, 4) };
            Button.normal.background = Button.onNormal.background = Solid(ControlFill);
            Button.hover.background = Button.onHover.background = Solid(ControlHover);
            Button.active.background = Button.onActive.background = Solid(ControlActive);
            Button.focused.background = Button.onFocused.background = Button.hover.background;
            Button.border = new RectOffset(0, 0, 0, 0);
            SetText(Button, ControlText);

            Row = new GUIStyle(Button) { padding = new RectOffset(8, 8, 2, 2), alignment = TextAnchor.MiddleLeft };
            // 明细子行：左内边距收窄（缩进由布局偏移负责），视觉上从属于父行
            Detail = new GUIStyle(Row) { padding = new RectOffset(2, 8, 2, 2) };
        }

        public void Dispose()
        {
            foreach (var texture in textures)
            {
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
            }
            textures.Clear();
            Window = Header = Label = Warning = Button = Row = Detail = null;
        }

        private Texture2D Solid(Color32 color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            textures.Add(texture);
            return texture;
        }

        private static void SetText(GUIStyle style, Color32 color)
        {
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = color;
            style.onNormal.textColor = style.onHover.textColor = style.onActive.textColor = style.onFocused.textColor = color;
        }
    }
}
