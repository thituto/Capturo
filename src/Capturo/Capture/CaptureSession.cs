using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capturo.Capture;

public enum CaptureMode
{
    Region,
    CurrentMonitor,
    AllMonitors,
}

/// <summary>Resultado de uma captura: uma imagem, uma cor copiada pelo conta-gotas, ou nada (cancelada).</summary>
public sealed record CaptureResult(BitmapSource? Image, Color? PickedColor)
{
    public static readonly CaptureResult Cancelled = new(null, null);
}

/// <summary>
/// Seleção de região sobre a tela congelada. Toda a geometria fica em pixels físicos da tela virtual;
/// cada monitor tem a sua janela de overlay, que só converte para as próprias unidades na hora de desenhar.
/// </summary>
public sealed class CaptureSession
{
    private const int DragThreshold = 4;

    private readonly BitmapSource _desktop;
    private readonly PxRect _virtual;
    private readonly IReadOnlyList<PxRect> _monitors;
    private readonly IReadOnlyList<WindowInfo> _windows;
    private readonly List<OverlayWindow> _overlays = new();
    private readonly TaskCompletionSource<CaptureResult> _completion = new();

    private PxPoint _cursor;
    private PxPoint? _dragStart;
    private bool _dragging;
    private PxRect _selection;
    private bool _finished;

    private CaptureSession(BitmapSource desktop, PxRect virtualScreen, IReadOnlyList<PxRect> monitors,
        IReadOnlyList<WindowInfo> windows)
    {
        _desktop = desktop;
        _virtual = virtualScreen;
        _monitors = monitors;
        _windows = windows;
    }

    public static Task<CaptureResult> CaptureAsync(CaptureMode mode)
    {
        switch (mode)
        {
            case CaptureMode.AllMonitors:
                return Task.FromResult(new CaptureResult(ScreenGrabber.Grab(ScreenGrabber.VirtualScreen), null));
            case CaptureMode.CurrentMonitor:
                var monitor = ScreenGrabber.MonitorAt(ScreenGrabber.CursorPosition);
                return Task.FromResult(new CaptureResult(ScreenGrabber.Grab(monitor), null));
        }

        // A lista de janelas e a imagem são tiradas antes de qualquer overlay aparecer.
        var windows = WindowFinder.GetVisibleWindows();
        var virtualScreen = ScreenGrabber.VirtualScreen;
        var desktop = ScreenGrabber.Grab(virtualScreen);
        var session = new CaptureSession(desktop, virtualScreen, ScreenGrabber.Monitors, windows);
        session.Open();
        return session._completion.Task;
    }

    internal BitmapSource Desktop => _desktop;
    internal PxRect Virtual => _virtual;

    private void Open()
    {
        foreach (var monitor in _monitors)
        {
            var overlay = new OverlayWindow(this, monitor);
            _overlays.Add(overlay);
            overlay.Show();
        }

        _cursor = ScreenGrabber.CursorPosition;
        Refresh();
        var active = _overlays.FirstOrDefault(o => o.Monitor.Contains(_cursor)) ?? _overlays[0];
        active.Activate();
        active.Focus();
    }

    internal void PointerDown()
    {
        _cursor = ScreenGrabber.CursorPosition;
        _dragStart = _cursor;
        _dragging = false;
        Refresh();
    }

    internal void PointerMove()
    {
        _cursor = ScreenGrabber.CursorPosition;
        if (_dragStart is { } start)
        {
            if (!_dragging && (Math.Abs(_cursor.X - start.X) > DragThreshold || Math.Abs(_cursor.Y - start.Y) > DragThreshold))
                _dragging = true;
            if (_dragging)
                _selection = PxRect.FromCorners(start, _cursor).Intersect(_virtual);
        }
        Refresh();
    }

    internal void PointerUp()
    {
        if (_dragStart is null)
            return;
        _cursor = ScreenGrabber.CursorPosition;
        bool dragged = _dragging;
        _dragStart = null;
        _dragging = false;

        if (dragged)
        {
            if (_selection.Width >= 2 && _selection.Height >= 2)
                FinishWithArea(_selection);
            else
                Refresh();
            return;
        }

        // Um clique sem arrastar captura a janela (ou o monitor) sob o cursor.
        FinishWithArea(HoverArea());
    }

    /// <summary>Botão direito: desfaz a seleção em andamento ou cancela a captura.</summary>
    internal void SecondaryClick()
    {
        if (_dragging)
        {
            _dragStart = null;
            _dragging = false;
            Refresh();
            return;
        }
        Finish(CaptureResult.Cancelled);
    }

    internal void KeyPressed(Key key)
    {
        switch (key)
        {
            case Key.Escape:
                Finish(CaptureResult.Cancelled);
                break;
            case Key.Enter:
            case Key.Space:
                FinishWithArea(MonitorUnderCursor());
                break;
            case Key.A:
                FinishWithArea(_virtual);
                break;
            case Key.C:
                Finish(new CaptureResult(null, ColorUnderCursor()));
                break;
        }
    }

    internal Color ColorUnderCursor() =>
        BitmapUtil.GetPixel(_desktop, _cursor.X - _virtual.X, _cursor.Y - _virtual.Y);

    private PxRect MonitorUnderCursor() =>
        _monitors.FirstOrDefault(m => m.Contains(_cursor), _monitors[0]);

    /// <summary>O que será capturado se o usuário clicar agora: a janela sob o cursor, ou o monitor.</summary>
    private PxRect HoverArea()
    {
        var window = WindowFinder.FindAt(_windows, _cursor);
        var area = window?.Bounds.Intersect(_virtual) ?? default;
        return area.IsEmpty ? MonitorUnderCursor() : area;
    }

    private void Refresh()
    {
        if (_finished)
            return;
        var state = new OverlayState(
            Highlight: _dragging ? _selection : HoverArea(),
            Cursor: _cursor,
            Dragging: _dragging,
            CursorColor: ColorUnderCursor());
        foreach (var overlay in _overlays)
            overlay.Render(state);
    }

    private void FinishWithArea(PxRect area)
    {
        area = area.Intersect(_virtual);
        if (area.IsEmpty)
        {
            Finish(CaptureResult.Cancelled);
            return;
        }
        var image = BitmapUtil.Crop(_desktop, new Int32Rect(area.X - _virtual.X, area.Y - _virtual.Y, area.Width, area.Height));
        Finish(new CaptureResult(image, null));
    }

    private void Finish(CaptureResult result)
    {
        if (_finished)
            return;
        _finished = true;
        foreach (var overlay in _overlays)
            overlay.Close();
        _overlays.Clear();
        _completion.TrySetResult(result);
    }
}

internal readonly record struct OverlayState(PxRect Highlight, PxPoint Cursor, bool Dragging, Color CursorColor);
