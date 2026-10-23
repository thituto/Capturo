using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static Capturo.Native.NativeMethods;

namespace Capturo.Capture;

/// <summary>Janela sem bordas que cobre exatamente um monitor e mostra a tela congelada, escurecida fora da seleção.</summary>
internal sealed class OverlayWindow : Window
{
    private const double MagnifierSize = 144;
    private const int MagnifierPixels = 15; // 15 × 15 pixels ampliados, ~9,6× cada

    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)));
    private static readonly Brush PanelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x1F, 0x23)));

    private readonly CaptureSession _session;
    private readonly Canvas _canvas = new();
    private readonly Path _dim = new() { Fill = DimBrush, IsHitTestVisible = false };
    private readonly Rectangle _border = new() { Stroke = Accent, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Line _horizontal = CrossLine();
    private readonly Line _vertical = CrossLine();
    private readonly Border _sizeLabel;
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12 };
    private readonly Border _magnifier;
    private readonly ImageBrush _magnifierBrush;
    private readonly TextBlock _magnifierText = new() { Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(8, 4, 8, 6) };
    private readonly Border _hint;
    private double _scale = 1;

    public PxRect Monitor { get; }

    public OverlayWindow(CaptureSession session, PxRect monitor)
    {
        _session = session;
        Monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Posição provisória em DIPs; a definitiva é aplicada em pixels físicos no SourceInitialized.
        Left = monitor.X;
        Top = monitor.Y;
        Width = monitor.Width;
        Height = monitor.Height;
        UseLayoutRounding = true;

        var virtualScreen = session.Virtual;
        var slice = new CroppedBitmap(session.Desktop,
            new Int32Rect(monitor.X - virtualScreen.X, monitor.Y - virtualScreen.Y, monitor.Width, monitor.Height));
        var image = new Image { Source = slice, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        _sizeLabel = new Border
        {
            Background = PanelBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 3),
            Child = _sizeText,
            IsHitTestVisible = false,
        };

        _magnifierBrush = new ImageBrush(session.Desktop)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        var zoomed = new Rectangle { Width = MagnifierSize, Height = MagnifierSize, Fill = _magnifierBrush };
        RenderOptions.SetBitmapScalingMode(zoomed, BitmapScalingMode.NearestNeighbor);
        double cell = MagnifierSize / MagnifierPixels;
        var center = new Rectangle
        {
            Width = cell,
            Height = cell,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            Margin = new Thickness((MagnifierSize - cell) / 2, (MagnifierSize - cell) / 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var zoomArea = new Grid { Width = MagnifierSize, Height = MagnifierSize, ClipToBounds = true };
        zoomArea.Children.Add(zoomed);
        zoomArea.Children.Add(center);
        var magnifierStack = new StackPanel();
        magnifierStack.Children.Add(zoomArea);
        magnifierStack.Children.Add(_magnifierText);
        _magnifier = new Border
        {
            Background = PanelBrush,
            BorderBrush = Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = magnifierStack,
            IsHitTestVisible = false,
        };

        _hint = new Border
        {
            Background = PanelBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 7),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
                Text = "Arraste para selecionar · clique numa janela · Enter: monitor · A: todas as telas · C: copiar cor · Esc: cancelar",
            },
        };

        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_horizontal);
        _canvas.Children.Add(_vertical);
        _canvas.Children.Add(_border);
        _canvas.Children.Add(_sizeLabel);
        _canvas.Children.Add(_magnifier);
        _canvas.Children.Add(_hint);

        var root = new Grid();
        root.Children.Add(image);
        root.Children.Add(_canvas);
        Content = root;

        MouseLeftButtonDown += (_, e) => { CaptureMouse(); _session.PointerDown(); e.Handled = true; };
        MouseMove += (_, _) => _session.PointerMove();
        MouseLeftButtonUp += (_, e) => { ReleaseMouseCapture(); _session.PointerUp(); e.Handled = true; };
        MouseRightButtonUp += (_, e) => { _session.SecondaryClick(); e.Handled = true; };
        KeyDown += (_, e) => { _session.KeyPressed(e.Key); e.Handled = true; };
        Loaded += (_, _) => ApplyPlacement();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyPlacement();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _scale = newDpi.DpiScaleX;
        // O WPF redimensiona a janela pelo retângulo sugerido; recoloca nos limites físicos do monitor.
        Dispatcher.BeginInvoke(ApplyPlacement, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ApplyPlacement()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        SetWindowPos(handle, HWND_TOPMOST, Monitor.X, Monitor.Y, Monitor.Width, Monitor.Height, SWP_NOACTIVATE);
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    public void Render(OverlayState state)
    {
        var full = new Rect(0, 0, Monitor.Width / _scale, Monitor.Height / _scale);
        var local = ToLocal(state.Highlight);
        bool hasHighlight = !state.Highlight.IsEmpty && state.Highlight.IntersectsWith(Monitor);

        _dim.Data = hasHighlight
            ? new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(local))
            : new RectangleGeometry(full);

        SetVisible(_border, hasHighlight);
        if (hasHighlight)
            Place(_border, local.X - 1, local.Y - 1, local.Width + 2, local.Height + 2);

        // O tamanho aparece só no monitor onde está o canto superior esquerdo da seleção.
        bool showSize = hasHighlight && Monitor.Contains(new PxPoint(state.Highlight.X, state.Highlight.Y));
        SetVisible(_sizeLabel, showSize);
        if (showSize)
        {
            _sizeText.Text = $"{state.Highlight.Width} × {state.Highlight.Height}";
            double labelTop = local.Y >= 30 ? local.Y - 28 : local.Y + 6;
            Place(_sizeLabel, local.X, labelTop);
        }

        bool cursorHere = Monitor.Contains(state.Cursor);
        var cursor = new Point((state.Cursor.X - Monitor.X) / _scale, (state.Cursor.Y - Monitor.Y) / _scale);

        bool showCross = cursorHere && !state.Dragging;
        SetVisible(_horizontal, showCross);
        SetVisible(_vertical, showCross);
        if (showCross)
        {
            _horizontal.X1 = 0; _horizontal.X2 = full.Width; _horizontal.Y1 = _horizontal.Y2 = cursor.Y;
            _vertical.Y1 = 0; _vertical.Y2 = full.Height; _vertical.X1 = _vertical.X2 = cursor.X;
        }

        SetVisible(_magnifier, cursorHere);
        if (cursorHere)
        {
            var virtualScreen = _session.Virtual;
            int half = MagnifierPixels / 2;
            _magnifierBrush.Viewbox = new Rect(state.Cursor.X - virtualScreen.X - half,
                state.Cursor.Y - virtualScreen.Y - half, MagnifierPixels, MagnifierPixels);
            _magnifierText.Text = $"{BitmapUtil.ToHex(state.CursorColor)}   {state.Cursor.X}, {state.Cursor.Y}";
            _magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = _magnifier.DesiredSize;
            double x = cursor.X + 24 + size.Width > full.Width ? cursor.X - 24 - size.Width : cursor.X + 24;
            double y = cursor.Y + 24 + size.Height > full.Height ? cursor.Y - 24 - size.Height : cursor.Y + 24;
            Place(_magnifier, x, y);
        }

        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool hintCovered = hasHighlight && local.Top < 64 && local.Left < full.Width / 2 + _hint.DesiredSize.Width / 2
            && local.Right > full.Width / 2 - _hint.DesiredSize.Width / 2;
        SetVisible(_hint, !state.Dragging && !hintCovered);
        Place(_hint, (full.Width - _hint.DesiredSize.Width) / 2, 16);
    }

    private Rect ToLocal(PxRect r) =>
        new((r.X - Monitor.X) / _scale, (r.Y - Monitor.Y) / _scale, r.Width / _scale, r.Height / _scale);

    private static void Place(FrameworkElement element, double x, double y, double? width = null, double? height = null)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        if (width is { } w) element.Width = Math.Max(0, w);
        if (height is { } h) element.Height = Math.Max(0, h);
    }

    private static void SetVisible(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static Line CrossLine() => new()
    {
        Stroke = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))),
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 4, 4 },
        IsHitTestVisible = false,
    };

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
