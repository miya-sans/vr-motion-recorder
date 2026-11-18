using System.Collections.Generic;
using Avalonia.Input;

namespace VRMR.App.Views.Settings
{
    using Key = Avalonia.Input.Key;

    /// <summary>Turns a key press into the hotkey text the recorder reads ("F9", "Ctrl+Shift+R").</summary>
    public static class HotkeyKeys
    {
        /// <summary>The text for a key with its modifiers; null for a key that cannot be a hotkey (a modifier on its own, say).</summary>
        public static string Text(Key key, KeyModifiers modifiers)
        {
            string name = Name(key);
            if (name == null) return null;
            var parts = new List<string>(4);
            if ((modifiers & KeyModifiers.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & KeyModifiers.Alt) != 0) parts.Add("Alt");
            if ((modifiers & KeyModifiers.Shift) != 0) parts.Add("Shift");
            if ((modifiers & KeyModifiers.Meta) != 0) parts.Add("Win");
            parts.Add(name);
            return string.Join("+", parts);
        }

        static string Name(Key key)
        {
            if (key >= Key.A && key <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
            if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
            if (key >= Key.F1 && key <= Key.F24) return "F" + (key - Key.F1 + 1);
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return "NumPad" + (key - Key.NumPad0);
            switch (key)
            {
                case Key.Space: return "Space";
                case Key.Enter: return "Enter";
                case Key.Back: return "Back";
                case Key.PageUp: return "PageUp";
                case Key.PageDown: return "PageDown";
                case Key.Home: return "Home";
                case Key.End: return "End";
                case Key.Left: return "Left";
                case Key.Up: return "Up";
                case Key.Right: return "Right";
                case Key.Down: return "Down";
                case Key.Insert: return "Insert";
                case Key.Delete: return "Delete";
                case Key.Pause: return "Pause";
                case Key.Scroll: return "Scroll";
                case Key.PrintScreen: return "PrintScreen";
                case Key.Multiply: return "Multiply";
                case Key.Add: return "Add";
                case Key.Subtract: return "Subtract";
                case Key.Decimal: return "Decimal";
                case Key.Divide: return "Divide";
                default: return null;
            }
        }
    }
}
