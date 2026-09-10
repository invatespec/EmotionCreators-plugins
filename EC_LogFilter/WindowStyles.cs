using UnityEngine;

namespace EC_LogFilter
{
    internal sealed class WindowStyles
    {
        internal readonly GUIStyle Window = new GUIStyle();
        internal readonly GUIStyle Label;
        internal readonly GUIStyle Muted;
        internal readonly GUIStyle Right;
        internal readonly GUIStyle Title;
        internal readonly GUIStyle Button;
        internal readonly GUIStyle Row;
        internal readonly GUIStyle Text;
        internal readonly GUIStyle Wrap;
        internal readonly GUIStyle Slider = new GUIStyle();
        internal readonly GUIStyle Thumb = new GUIStyle();

        internal WindowStyles()
        {
            Label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, alignment = TextAnchor.MiddleLeft, richText = false,
                wordWrap = false, clipping = TextClipping.Clip, padding = new RectOffset(4, 4, 0, 0)
            };
            Paint(Label, false);
            Muted = new GUIStyle(Label);
            Muted.normal.textColor = new Color(0.72f, 0.76f, 0.81f);
            Right = new GUIStyle(Label) { alignment = TextAnchor.MiddleRight };
            Title = new GUIStyle(Label) { fontSize = 16, fontStyle = FontStyle.Bold };
            Row = new GUIStyle(Label);
            Button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14, richText = false, alignment = TextAnchor.MiddleCenter,
                fixedHeight = 0, padding = new RectOffset(4, 4, 0, 0), border = new RectOffset()
            };
            Paint(Button, true);
            Text = new GUIStyle(GUI.skin.textField) { fontSize = 14, richText = false };
            Paint(Text, true);
            Wrap = new GUIStyle(Label) { wordWrap = true, alignment = TextAnchor.UpperLeft };
            Thumb.fixedHeight = 28;
            Paint(Thumb, true);
        }

        private static void Paint(GUIStyle style, bool background)
        {
            foreach (GUIStyleState state in new[] { style.normal, style.hover, style.active, style.focused,
                style.onNormal, style.onHover, style.onActive, style.onFocused })
            {
                state.background = background ? Texture2D.whiteTexture : null;
                state.textColor = background
                    ? new Color(0.10f, 0.13f, 0.18f)
                    : new Color(0.94f, 0.95f, 0.97f);
            }
        }

        internal static void Band(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
}
