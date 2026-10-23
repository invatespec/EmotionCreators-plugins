using System;
using System.Collections.Generic;
using UnityEngine;

namespace EC_SceneAudit.Repair
{
    // 本窗口私有样式：只克隆 GUI.skin，禁止写入共享 skin（会污染同帧其它 IMGUI 插件窗口）。
    // 边界用 3x3 九宫格贴图 + border=1；输入框刻意浅底深字，让默认深色光标与选区可见。
    // 自建贴图随窗口释放；Texture2D.whiteTexture 是内建共享纹理，不释放。
    internal sealed class RepairWindowStyles : IDisposable
    {
        internal static readonly Color32 WindowFill = new Color32(0x20, 0x26, 0x2E, 0xFF);
        internal static readonly Color32 ContentFill = new Color32(0x19, 0x1E, 0x25, 0xFF);
        internal static readonly Color32 TitleFill = new Color32(0x2B, 0x33, 0x3D, 0xFF);

        private static readonly Color32 BoxFill = new Color32(0x15, 0x1A, 0x20, 0xFF);
        private static readonly Color32 BoxBorder = new Color32(0x3A, 0x44, 0x4F, 0xFF);
        private static readonly Color32 ControlFill = new Color32(0xD7, 0xDF, 0xE8, 0xFF);
        private static readonly Color32 ControlHover = new Color32(0xE9, 0xEF, 0xF5, 0xFF);
        private static readonly Color32 ControlActive = new Color32(0xBE, 0xC8, 0xD3, 0xFF);
        private static readonly Color32 ControlBorder = new Color32(0x93, 0xA0, 0xAF, 0xFF);
        private static readonly Color32 ControlText = new Color32(0x12, 0x18, 0x1F, 0xFF);
        private static readonly Color32 InputFill = new Color32(0xED, 0xF1, 0xF6, 0xFF);
        private static readonly Color32 InputFocused = new Color32(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly Color32 InputBorder = new Color32(0xA6, 0xB2, 0xC0, 0xFF);
        private static readonly Color32 InputText = new Color32(0x10, 0x14, 0x18, 0xFF);
        private static readonly Color32 BodyText = new Color32(0xD5, 0xDC, 0xE4, 0xFF);
        private static readonly Color32 StrongText = new Color32(0xF2, 0xF6, 0xFA, 0xFF);

        private readonly List<Texture2D> textures = new List<Texture2D>();

        public GUIStyle WindowFrame;
        public GUIStyle Box;
        public GUIStyle Label;
        public GUIStyle Header;
        public GUIStyle Status;
        public GUIStyle TextField;
        public GUIStyle TextArea;
        public GUIStyle Button;

        public void Ensure()
        {
            if (WindowFrame != null)
                return;

            // 窗口自身零边距、零 padding，标题栏与内容区坐标由本窗口自己画，位置可控。
            WindowFrame = new GUIStyle
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0)
            };
            WindowFrame.normal.background = Solid(WindowFill);
            WindowFrame.onNormal.background = WindowFrame.normal.background;

            Box = new GUIStyle(GUI.skin.box);
            SetBackground(Box, Panel(BoxFill, BoxBorder), new RectOffset(1, 1, 1, 1));
            Box.padding = new RectOffset(6, 6, 4, 4);
            SetText(Box, BodyText);

            // fontSize=0 表示使用默认字号，不能直接加减；与 EC_LogFilter 一样显式指定字号。
            Label = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 14 };
            SetText(Label, BodyText);

            Header = new GUIStyle(Label) { fontSize = 16 };
            SetText(Header, StrongText);

            Status = new GUIStyle(Label) { fontSize = 16 };
            SetText(Status, StrongText);

            TextField = new GUIStyle(GUI.skin.textField) { fontSize = 14 };
            SetControls(TextField, Panel(InputFill, InputBorder), Panel(InputFill, InputBorder),
                Panel(InputFocused, InputBorder), Panel(InputFocused, InputBorder), InputText);
            TextField.padding = new RectOffset(6, 6, 3, 3);

            // 路径使用多行输入区：长路径按宽度换行，整段仍可直接编辑。
            TextArea = new GUIStyle(GUI.skin.textArea) { fontSize = 14, wordWrap = true, stretchHeight = true };
            SetControls(TextArea, Panel(InputFill, InputBorder), Panel(InputFill, InputBorder),
                Panel(InputFocused, InputBorder), Panel(InputFocused, InputBorder), InputText);
            TextArea.padding = new RectOffset(6, 6, 3, 3);

            Button = new GUIStyle(GUI.skin.button) { fontSize = 14 };
            SetControls(Button, Panel(ControlFill, ControlBorder), Panel(ControlHover, ControlBorder),
                Panel(ControlActive, ControlBorder), Panel(ControlHover, ControlBorder), ControlText);
            Button.padding = new RectOffset(6, 6, 3, 3);
        }

        public void Dispose()
        {
            foreach (var texture in textures)
            {
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
            }
            textures.Clear();
            WindowFrame = null;
            Box = null;
            Label = null;
            Header = null;
            Status = null;
            TextField = null;
            TextArea = null;
            Button = null;
        }

        // 纯色带：只临时改 GUI.color，画完立即恢复，不依赖 OnGUI 自动复原。
        internal static void Band(Rect rect, Color32 color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private Texture2D Solid(Color32 color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            textures.Add(texture);
            return texture;
        }

        // 3x3 九宫格：中心填充、外圈边框，配合 border=1 得到 1 像素可见描边。
        private Texture2D Panel(Color32 fill, Color32 border)
        {
            var texture = new Texture2D(3, 3);
            for (var y = 0; y < 3; y++)
            {
                for (var x = 0; x < 3; x++)
                    texture.SetPixel(x, y, x == 1 && y == 1 ? fill : border);
            }
            texture.Apply();
            textures.Add(texture);
            return texture;
        }

        private static void SetBackground(GUIStyle style, Texture2D texture, RectOffset border)
        {
            style.border = border;
            style.normal.background = texture;
            style.hover.background = texture;
            style.active.background = texture;
            style.focused.background = texture;
            style.onNormal.background = texture;
            style.onHover.background = texture;
            style.onActive.background = texture;
            style.onFocused.background = texture;
        }

        // 按钮/输入框的八种状态都要给底色和文字色，只改 normal 会在悬停/按下时露出原生皮肤。
        private static void SetControls(
            GUIStyle style,
            Texture2D normal,
            Texture2D hover,
            Texture2D active,
            Texture2D focused,
            Color32 text)
        {
            style.normal.background = normal;
            style.hover.background = hover;
            style.active.background = active;
            style.focused.background = focused;
            style.onNormal.background = normal;
            style.onHover.background = hover;
            style.onActive.background = active;
            style.onFocused.background = focused;
            style.border = new RectOffset(1, 1, 1, 1);
            SetText(style, text);
        }

        private static void SetText(GUIStyle style, Color32 color)
        {
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
            style.onNormal.textColor = color;
            style.onHover.textColor = color;
            style.onActive.textColor = color;
            style.onFocused.textColor = color;
        }
    }
}
