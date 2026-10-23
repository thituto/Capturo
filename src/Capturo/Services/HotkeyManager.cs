using System.Windows.Interop;
using static Capturo.Native.NativeMethods;

namespace Capturo.Services;

/// <summary>Atalhos globais via RegisterHotKey, recebidos por uma janela só de mensagens.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("CapturoHotkeys")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <returns>false se outro programa já usa a combinação.</returns>
    public bool Register(uint modifiers, uint virtualKey, Action handler)
    {
        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, virtualKey))
            return false;
        _handlers[id] = handler;
        return true;
    }

    /// <summary>Remove todos os atalhos (antes de reaplicar as configurações ou enquanto o usuário grava um novo).</summary>
    public void Clear()
    {
        foreach (int id in _handlers.Keys)
            UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            handler();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Clear();
        _source.Dispose();
    }
}
