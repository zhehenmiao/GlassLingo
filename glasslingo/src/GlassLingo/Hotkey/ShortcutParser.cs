using System;
using System.Windows.Input;
using GlassLingo.Native;

namespace GlassLingo.Hotkey
{
    internal static class ShortcutParser
    {
        internal static bool TryFromKey(Key key, ModifierKeys modifiers, out string value)
        {
            value = "";
            if ((modifiers & ModifierKeys.Windows) != 0 ||
                (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 ||
                key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin || !Enum.IsDefined(key)) return false;
            string token = key >= Key.D0 && key <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString();
            value = ((modifiers & ModifierKeys.Control) != 0 ? "Ctrl+" : "") +
                ((modifiers & ModifierKeys.Alt) != 0 ? "Alt+" : "") +
                ((modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : "") + token;
            return TryParse(value, out _, out _);
        }
        public static bool TryParse(string value, out NativeMethods.HotkeyModifiers modifiers, out uint key)
        {
            modifiers = NativeMethods.HotkeyModifiers.NoRepeat; key = 0;
            foreach (string part in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                switch (part.ToUpperInvariant())
                {
                    case "CTRL": case "CONTROL": modifiers |= NativeMethods.HotkeyModifiers.Control; break;
                    case "ALT": modifiers |= NativeMethods.HotkeyModifiers.Alt; break;
                    case "SHIFT": modifiers |= NativeMethods.HotkeyModifiers.Shift; break;
                    default:
                        string token = part.Length == 1 && char.IsAsciiDigit(part[0]) ? "D" + part : part;
                        if (key != 0 || !Enum.TryParse<Key>(token, true, out var parsed) || !Enum.IsDefined(parsed)
                            || parsed is Key.None or Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return false;
                        key = (uint)KeyInterop.VirtualKeyFromKey(parsed); break;
                }
            }
            return key != 0 && (modifiers & (NativeMethods.HotkeyModifiers.Control | NativeMethods.HotkeyModifiers.Alt)) != 0;
        }
    }
}
