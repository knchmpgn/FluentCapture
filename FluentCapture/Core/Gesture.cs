using System.Windows.Input;

namespace FluentSnip.Core;

/// <summary>Parsing / formatting of hotkey strings such as "Ctrl+Shift+1".</summary>
public static class Gesture
{
    public static bool TryParse(string s, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None; key = Key.None;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default: return false;
            }
        }
        var k = parts[^1];
        if (k == "~") key = Key.Oem3;
        else if (k == "-") key = Key.OemMinus;
        else if (k == "=") key = Key.OemPlus;
        else if (k.Equals("PrintScreen", StringComparison.OrdinalIgnoreCase) || k.Equals("PrtScn", StringComparison.OrdinalIgnoreCase) || k.Equals("PrtSc", StringComparison.OrdinalIgnoreCase)) key = Key.Snapshot;
        else if (k.Length == 1 && char.IsDigit(k[0])) key = Key.D0 + (k[0] - '0');
        else if (!Enum.TryParse(k, true, out key)) return false;
        return key != Key.None;
    }

    public static string KeyName(Key k)
    {
        if (k >= Key.D0 && k <= Key.D9) return ((int)(k - Key.D0)).ToString();
        return k switch { Key.Snapshot => "PrintScreen", Key.Oem3 => "~", Key.OemMinus => "-", Key.OemPlus => "=", _ => k.ToString() };
    }

    public static string Format(ModifierKeys m, Key k)
    {
        var l = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) l.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) l.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) l.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) l.Add("Win");
        l.Add(KeyName(k));
        return string.Join("+", l);
    }

    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}
