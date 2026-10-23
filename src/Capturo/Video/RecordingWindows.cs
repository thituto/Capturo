using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Capturo.Capture;

namespace Capturo.Video;

/// <summary>Moldura vermelha em volta da área gravada. Deixa os cliques passarem e não aparece no vídeo.</summary>
internal sealed class RecordingFrame : Window
{
    private const int FrameMargin = 3;
    private readonly PxRect _area;

    public RecordingFrame(PxRect area)
    {
        _area = area;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Content = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        CaptureExclusion.Apply(this);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
            Capturo.Native.NativeMethods.SetWindowPos(handle, Capturo.Native.NativeMethods.HWND_TOPMOST,
                _area.X - FrameMargin, _area.Y - FrameMargin, _area.Width + FrameMargin * 2, _area.Height + FrameMargin * 2, Capturo.Native.NativeMethods.SWP_NOACTIVATE);
        };
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}

/// <summary>Barra flutuante da gravação: tempo, pausar/continuar, parar e descartar.</summary>
internal sealed class RecordingToolbar : Window
{
    private readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Width = 74 };
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), Margin = new Thickness(4, 0, 8, 0) };
    private readonly Button _pause = new() { Content = "❚❚ Pausar", ToolTip = "Pausar / continuar" };
    private readonly DispatcherTimer _timer;
    private readonly VideoRecorder _recorder;

    public event EventHandler? StopRequested;
    public event EventHandler? DiscardRequested;

    public RecordingToolbar(VideoRecorder recorder)
    {
        _recorder = recorder;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;

        var stop = new Button { Content = "■ Parar", ToolTip = "Parar e salvar (ou o atalho de gravação)" };
        var discard = new Button { Content = "✕", ToolTip = "Descartar a gravação" };
        _pause.Click += (_, _) => { _recorder.TogglePause(); UpdateState(); };
        stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        discard.Click += (_, _) => DiscardRequested?.Invoke(this, EventArgs.Empty);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 6, 6) };
        panel.Children.Add(_dot);
        panel.Children.Add(_time);
        panel.Children.Add(_pause);
        panel.Children.Add(stop);
        panel.Children.Add(discard);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1F, 0x1F, 0x23)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x41)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
        };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) DragMove(); };
        CaptureExclusion.Apply(this);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdateState();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Loaded += (_, _) => PlaceNearArea();
        UpdateState();
    }

    private void UpdateState()
    {
        var elapsed = _recorder.Elapsed;
        _time.Text = elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
        _pause.Content = _recorder.IsPaused ? "▶ Continuar" : "❚❚ Pausar";
        _dot.Opacity = _recorder.IsPaused ? 0.3 : (DateTime.Now.Millisecond < 500 ? 1 : 0.35);
    }

    /// <summary>Abaixo da área gravada; se não couber, acima; senão, dentro, no canto inferior.</summary>
    private void PlaceNearArea()
    {
        var area = _recorder.Area;
        var monitor = ScreenGrabber.MonitorAt(new PxPoint(area.X + area.Width / 2, area.Y + area.Height / 2));
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double width = ActualWidth * scale, height = ActualHeight * scale;
        double x = Math.Clamp(area.X + area.Width / 2.0 - width / 2, monitor.X, monitor.Right - width);
        double y = area.Bottom + 10 + height <= monitor.Bottom ? area.Bottom + 10
            : area.Y - 10 - height >= monitor.Y ? area.Y - 10 - height
            : area.Bottom - height - 12;
        Left = x / scale;
        Top = y / scale;
    }
}
