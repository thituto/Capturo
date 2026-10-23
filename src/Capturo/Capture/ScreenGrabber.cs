using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using static Capturo.Native.NativeMethods;

namespace Capturo.Capture;

public static class ScreenGrabber
{
    public static PxRect VirtualScreen => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>Monitores em pixels físicos (o processo é per-monitor v2).</summary>
    public static IReadOnlyList<PxRect> Monitors =>
        System.Windows.Forms.Screen.AllScreens
            .Select(s => new PxRect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height))
            .ToList();

    public static PxPoint CursorPosition =>
        GetCursorPos(out var p) ? new PxPoint(p.X, p.Y) : new PxPoint(0, 0);

    public static PxRect MonitorAt(PxPoint point)
    {
        var monitors = Monitors;
        return monitors.FirstOrDefault(m => m.Contains(point), monitors[0]);
    }

    /// <summary>Copia uma área da tela com BitBlt (CAPTUREBLT inclui janelas em camadas).</summary>
    public static BitmapSource Grab(PxRect area)
    {
        if (area.IsEmpty)
            throw new ArgumentException("Área de captura vazia.", nameof(area));

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr bitmap = CreateCompatibleBitmap(screenDc, area.Width, area.Height);
        IntPtr previous = SelectObject(memoryDc, bitmap);
        try
        {
            if (!BitBlt(memoryDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, SRCCOPY | CAPTUREBLT))
                throw new Win32Exception();
            SelectObject(memoryDc, previous);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            return BitmapUtil.Materialize(source);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
