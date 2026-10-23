using System.Runtime.InteropServices;
using System.Text;
using static Capturo.Native.NativeMethods;

namespace Capturo.Capture;

public sealed record WindowInfo(IntPtr Handle, PxRect Bounds, string Title);

public static class WindowFinder
{
    /// <summary>Janelas visíveis de nível superior, da mais à frente para a mais atrás.</summary>
    public static IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows((handle, _) =>
        {
            if (TryDescribe(handle) is { } info)
                windows.Add(info);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>A janela mais à frente que contém o ponto (a lista vem em ordem de profundidade).</summary>
    public static WindowInfo? FindAt(IReadOnlyList<WindowInfo> windows, PxPoint point) =>
        windows.FirstOrDefault(w => w.Bounds.Contains(point));

    private static WindowInfo? TryDescribe(IntPtr handle)
    {
        if (!IsWindowVisible(handle) || IsIconic(handle))
            return null;

        // Janelas que deixam o clique passar costumam ser overlays invisíveis.
        if ((GetWindowLong(handle, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0)
            return null;

        // Janelas "cloaked" (apps UWP suspensos, outras áreas de trabalho) não aparecem na tela.
        if (DwmGetWindowAttribute(handle, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return null;

        // A moldura estendida do DWM exclui a borda invisível de redimensionamento do Windows 10/11.
        RECT r;
        if (DwmGetWindowAttribute(handle, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(handle, out r))
            return null;

        var bounds = PxRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);
        if (bounds.Width < 8 || bounds.Height < 8)
            return null;

        var title = new StringBuilder(256);
        GetWindowText(handle, title, title.Capacity);
        return new WindowInfo(handle, bounds, title.ToString());
    }
}
