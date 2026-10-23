using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capturo.Capture;

/// <summary>Para que serve a seleção: muda o texto de ajuda e o que um clique faz.</summary>
public enum SelectionPurpose
{
    Screenshot,
    TextRecognition,
    QrCode,
    ColorPicker,
    Scrolling,
    Video,
}

/// <summary>Resultado de uma seleção: a imagem e a área escolhida, uma cor (conta-gotas), ou nada (cancelada).</summary>
public sealed record CaptureResult(BitmapSource? Image, Color? PickedColor, PxRect? Area)
{
    public static readonly CaptureResult Cancelled = new(null, null, null);
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
    private readonly SelectionPurpose _purpose;
    private readonly List<OverlayWindow> _overlays = new();
    private readonly TaskCompletionSource<CaptureResult> _completion = new();

    private PxPoint _cursor;
    private PxPoint? _dragStart;
    private bool _dragging;
    private PxRect _selection;
    private bool _finished;

    private CaptureSession(BitmapSource desktop, PxRect virtualScreen, IReadOnlyList<PxRect> monitors,
        IReadOnlyList<WindowInfo> windows, SelectionPurpose purpose)
    {
        _desktop = desktop;
        _virtual = virtualScreen;
        _monitors = monitors;
        _windows = windows;
        _purpose = purpose;
    }

    /// <summary>Congela a tela e deixa o usuário escolher uma região, janela ou monitor.</summary>
    public static Task<CaptureResult> SelectAsync(SelectionPurpose purpose = SelectionPurpose.Screenshot)
    {
        // A lista de janelas e a imagem são tiradas antes de qualquer overlay aparecer.
        var windows = WindowFinder.GetVisibleWindows();
        var virtualScreen = ScreenGrabber.VirtualScreen;
        var desktop = ScreenGrabber.Grab(virtualScreen);
        var session = new CaptureSession(desktop, virtualScreen, ScreenGrabber.Monitors, windows, purpose);
        session.Open();
        return session._completion.Task;
    }

    internal BitmapSource Desktop => _desktop;
    internal PxRect Virtual => _virtual;

    internal string Hint => _purpose switch
    {
        SelectionPurpose.TextRecognition => "Selecione o texto a reconhecer · clique numa janela · Esc: cancelar",
        SelectionPurpose.QrCode => "Selecione o QR Code ou código de barras · Esc: cancelar",
        SelectionPurpose.ColorPicker => "Clique na cor que quer copiar · Esc: cancelar",
        SelectionPurpose.Scrolling => "Selecione a área que rola (ou clique na janela) · Esc durante a rolagem encerra",
        SelectionPurpose.Video => "Selecione a área a gravar · clique numa janela · Enter: monitor inteiro · Esc: cancelar",
        _ => "Arraste para selecionar · clique numa janela · Enter: monitor · A: todas as telas · C: copiar cor · Esc: cancelar",
    };

    internal bool PicksColorOnly => _purpose == SelectionPurpose.ColorPicker;

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
        if (PicksColorOnly)
        {
            Finish(new CaptureResult(null, ColorUnderCursor(), null));
            return;
        }
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
                if (!PicksColorOnly)
                    FinishWithArea(MonitorUnderCursor());
                break;
            case Key.A when _purpose == SelectionPurpose.Screenshot:
                FinishWithArea(_virtual);
                break;
            case Key.C when _purpose is SelectionPurpose.Screenshot or SelectionPurpose.ColorPicker:
                Finish(new CaptureResult(null, ColorUnderCursor(), null));
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
            Highlight: PicksColorOnly ? default : _dragging ? _selection : HoverArea(),
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
        Finish(new CaptureResult(image, null, area));
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
