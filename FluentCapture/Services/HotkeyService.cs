using System.Windows.Input;
using System.Windows.Interop;
using FluentSnip.Core;
using FluentSnip.Native;

namespace FluentSnip.Services;

public sealed class HotkeyService : IDisposable
{
    readonly HwndSource src;
    readonly Dictionary<int, Action> actions = new();
    int nextId = 1;

    // PrintScreen cannot be reliably claimed with RegisterHotKey (Windows 11 can reserve it for Snipping Tool,
    // and the key often produces no key-down message), so those bindings use a low-level keyboard hook.
    readonly List<(ModifierKeys Mods, Action Action)> hookBindings = new();
    NativeMethods.LowLevelKeyboardProc hookProc;
    IntPtr hookHandle;
    bool prtDown;

    public HotkeyService()
    {
        var p = new HwndSourceParameters("FluentSnip.Hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 };
        src = new HwndSource(p);
        src.AddHook(Hook);
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && actions.TryGetValue(w.ToInt32(), out var a))
        {
            handled = true;
            Application.Current.Dispatcher.BeginInvoke(a);
        }
        return IntPtr.Zero;
    }

    public bool Register(string gesture, Action action)
    {
        if (!Gesture.TryParse(gesture, out var mods, out var key)) return false;
        if (key == Key.Snapshot)
        {
            // The low-level hook does not receive keys while an elevated window (e.g. an elevated FluentStart start menu) has focus,
            // because Windows blocks input hooks across integrity levels. RegisterHotKey is not subject to that block, so also try it
            // as a second path. It may fail (Windows can reserve PrintScreen for Snipping Tool) - that is fine, the hook still works.
            // Both paths go through Fire(), which drops a duplicate within 400 ms.
            bool hooked = RegisterHook(mods, action);
            TryRegisterNative(mods, key, () => Fire(action));
            return hooked;
        }
        uint m = 0x4000; // MOD_NOREPEAT
        if (mods.HasFlag(ModifierKeys.Alt)) m |= 1;
        if (mods.HasFlag(ModifierKeys.Control)) m |= 2;
        if (mods.HasFlag(ModifierKeys.Shift)) m |= 4;
        if (mods.HasFlag(ModifierKeys.Windows)) m |= 8;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        int id = nextId++;
        if (!NativeMethods.RegisterHotKey(src.Handle, id, m, vk)) return false;
        actions[id] = action;
        return true;
    }

    DateTime lastFire = DateTime.MinValue;

    void Fire(Action action)
    {
        var now = DateTime.UtcNow;
        if ((now - lastFire).TotalMilliseconds < 400) return;
        lastFire = now;
        action();
    }

    void TryRegisterNative(ModifierKeys mods, Key key, Action action)
    {
        uint m = 0x4000; // MOD_NOREPEAT
        if (mods.HasFlag(ModifierKeys.Alt)) m |= 1;
        if (mods.HasFlag(ModifierKeys.Control)) m |= 2;
        if (mods.HasFlag(ModifierKeys.Shift)) m |= 4;
        if (mods.HasFlag(ModifierKeys.Windows)) m |= 8;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        int id = nextId++;
        if (NativeMethods.RegisterHotKey(src.Handle, id, m, vk)) actions[id] = action;
    }

    public void UnregisterAll()
    {
        foreach (var id in actions.Keys) NativeMethods.UnregisterHotKey(src.Handle, id);
        actions.Clear();
        hookBindings.Clear();
        if (hookHandle != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(hookHandle); hookHandle = IntPtr.Zero; }
    }

    bool RegisterHook(ModifierKeys mods, Action action)
    {
        hookBindings.Add((mods, action));
        if (hookHandle != IntPtr.Zero) return true;
        hookProc = HookCallback;   // keep the delegate alive
        hookHandle = NativeMethods.SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, hookProc, NativeMethods.GetModuleHandleW(null), 0);
        if (hookHandle == IntPtr.Zero) { hookBindings.Clear(); return false; }
        return true;
    }

    static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    static ModifierKeys CurrentMods()
    {
        var m = ModifierKeys.None;
        if (Down(0x11)) m |= ModifierKeys.Control;
        if (Down(0x12)) m |= ModifierKeys.Alt;
        if (Down(0x10)) m |= ModifierKeys.Shift;
        if (Down(0x5B) || Down(0x5C)) m |= ModifierKeys.Windows;
        return m;
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && System.Runtime.InteropServices.Marshal.ReadInt32(lParam) == 0x2C /* VK_SNAPSHOT */)
        {
            int msg = wParam.ToInt32();
            bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
            var mods = CurrentMods();
            foreach (var b in hookBindings)
            {
                if (b.Mods != mods) continue;
                if (down) { if (!prtDown) { prtDown = true; var act = b.Action; Application.Current.Dispatcher.BeginInvoke(() => Fire(act)); } }
                else if (up) { if (!prtDown) { var act = b.Action; Application.Current.Dispatcher.BeginInvoke(() => Fire(act)); } prtDown = false; }
                return (IntPtr)1;   // swallow so Windows / Snipping Tool does not also react
            }
        }
        return NativeMethods.CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() { UnregisterAll(); src.Dispose(); }
}
