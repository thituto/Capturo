using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Capturo.Services;

namespace Capturo.Capture;

internal static class CaptureExclusion
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    /// <summary>Faz a janela não aparecer em capturas de tela (contagem, barra de gravação).</summary>
    public static void Apply(Window window) =>
        window.SourceInitialized += (_, _) => SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, WDA_EXCLUDEFROMCAPTURE);
}

/// <summary>Contagem regressiva grande no centro do monitor sob o cursor.</summary>
internal sealed class CountdownWindow : Window
{
    private readonly TextBlock _number = new()
    {
        FontSize = 72,
        FontWeight = FontWeights.Bold,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private CountdownWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x1F, 0x1F, 0x23)),
            CornerRadius = new CornerRadius(75),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            BorderThickness = new Thickness(3),
            Child = _number,
        };
        CaptureExclusion.Apply(this);
    }

    public static async Task RunAsync(int seconds)
    {
        var window = new CountdownWindow();
        var monitor = ScreenGrabber.MonitorAt(ScreenGrabber.CursorPosition);
        window.Show();
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        window.Left = (monitor.X + monitor.Width / 2.0) / scale - window.Width / 2;
        window.Top = (monitor.Y + monitor.Height / 2.0) / scale - window.Height / 2;
        for (int i = seconds; i > 0; i--)
        {
            window._number.Text = i.ToString();
            await Task.Delay(1000);
        }
        window.Close();
        await Task.Delay(120); // deixa a janela sumir antes de congelar a tela
    }
}

/// <summary>Mostra o texto reconhecido (OCR ou QR Code), já copiado, com opção de abrir links.</summary>
internal sealed class TextResultWindow : Window
{
    public TextResultWindow(string title, string text)
    {
        Title = $"{title} — Capturo";
        Width = 560;
        Height = 380;
        MinWidth = 360;
        MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("WindowBackground");
        Topmost = true;

        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 14,
            Padding = new Thickness(8),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        string trimmed = text.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var open = new Button { Content = "🔗 Abrir link", Margin = new Thickness(0, 0, 8, 0) };
            open.Click += (_, _) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            buttons.Children.Add(open);
        }
        var copy = new Button { Content = "⧉ Copiar", Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => OutputService.CopyText(box.Text);
        var close = new Button { Content = "Fechar", IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var layout = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(box);
        Content = layout;
    }
}
