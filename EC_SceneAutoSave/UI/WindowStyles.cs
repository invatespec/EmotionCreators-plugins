using System;
using System.Collections.Generic;
using UnityEngine;

namespace EC_SceneAutoSave.UI
{
    // 本窗口私有样式：只克隆 GUI.skin，禁止写入共享 skin（会污染同帧其它 IMGUI 插件窗口）。
    internal sealed class WindowStyles : IDisposable
    {
        private static readonly Color32 WindowFill = new Color32(0x20, 0x26, 0x2E, 0xF2);
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

        public void Ensure()
        {
            if (Window != null)
                return;
            Window = new GUIStyle { padding = new RectOffset(12, 12, 10, 12), margin = new RectOffset(0, 0, 0, 0) };
            Window.normal.background = Solid(WindowFill);
            Window.onNormal.background = Window.normal.background;

            // fontSize=0 表示默认字号，不能直接加减，显式指定。
            Label = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 14 };
            SetText(Label, BodyText);
            Header = new GUIStyle(Label) { fontSize = 16, fontStyle = FontStyle.Bold };
            SetText(Header, StrongText);
            Warning = new GUIStyle(Label);
            SetText(Warning, WarnText);

            // 按钮用 1x1 纯色：3x3 九宫格贴图默认双线性过滤，拉伸后中心色与边框色混成渐变。
            Button = new GUIStyle(GUI.skin.button) { fontSize = 14, padding = new RectOffset(8, 8, 4, 4) };
            Button.normal.background = Button.onNormal.background = Solid(ControlFill);
            Button.hover.background = Button.onHover.background = Solid(ControlHover);
            Button.active.background = Button.onActive.background = Solid(ControlActive);
            Button.focused.background = Button.onFocused.background = Button.hover.background;
            Button.border = new RectOffset(0, 0, 0, 0);
            SetText(Button, ControlText);
        }

        public void Dispose()
        {
            foreach (var texture in textures)
            {
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
            }
            textures.Clear();
            Window = Header = Label = Warning = Button = null;
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
