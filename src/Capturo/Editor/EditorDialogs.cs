using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Capturo.Editor;

/// <summary>Base das caixas de diálogo pequenas do editor: tema escuro, OK/Cancelar.</summary>
internal abstract class EditorDialog : Window
{
    private readonly StackPanel _body = new() { Margin = new Thickness(18, 16, 18, 8) };

    protected EditorDialog(Window owner, string title)
    {
        Owner = owner;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("WindowBackground");
        Foreground = (Brush)Application.Current.FindResource("PrimaryText");
        FontSize = 13;

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 76, Background = (Brush)Application.Current.FindResource("Accent") };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 76, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => { if (Validate()) DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 4, 18, 16) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel();
        root.Children.Add(_body);
        root.Children.Add(buttons);
        Content = root;
    }

    protected virtual bool Validate() => true;

    protected void Add(UIElement element) => _body.Children.Add(element);

    protected static TextBox NumberBox(int value) => new()
    {
        Text = value.ToString(CultureInfo.InvariantCulture),
        Width = 72,
        Padding = new Thickness(4, 3, 4, 3),
        HorizontalContentAlignment = HorizontalAlignment.Right,
    };

    protected static int Read(TextBox box, int fallback) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) ? value : fallback;

    protected static UIElement Row(string label, UIElement field, string? suffix = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(field);
        if (suffix != null)
            row.Children.Add(new TextBlock { Text = suffix, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray });
        return row;
    }
}

/// <summary>Redimensionar: largura e altura em pixels ou porcentagem, mantendo a proporção.</summary>
internal sealed class ResizeDialog : EditorDialog
{
    private readonly int _originalWidth;
    private readonly int _originalHeight;
    private readonly TextBox _width;
    private readonly TextBox _height;
    private readonly CheckBox _keepRatio = new() { Content = "Manter a proporção", IsChecked = true, Margin = new Thickness(0, 6, 0, 2) };
    private bool _updating;

    public ResizeDialog(Window owner, int width, int height) : base(owner, "Redimensionar")
    {
        _originalWidth = width;
        _originalHeight = height;
        _width = NumberBox(width);
        _height = NumberBox(height);
        _width.TextChanged += (_, _) => Sync(fromWidth: true);
        _height.TextChanged += (_, _) => Sync(fromWidth: false);

        Add(new TextBlock { Text = $"Tamanho atual: {width} × {height} px", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8) });
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (int percent in new[] { 25, 50, 75, 150, 200 })
        {
            var preset = new Button { Content = $"{percent}%", Margin = new Thickness(0, 0, 6, 0) };
            preset.Click += (_, _) => { _width.Text = (width * percent / 100).ToString(); _height.Text = (height * percent / 100).ToString(); };
            presets.Children.Add(preset);
        }
        Add(presets);
        Add(Row("Largura", _width, "px"));
        Add(Row("Altura", _height, "px"));
        Add(_keepRatio);
    }

    public int ResultWidth => Read(_width, _originalWidth);
    public int ResultHeight => Read(_height, _originalHeight);

    private void Sync(bool fromWidth)
    {
        if (_updating || _keepRatio.IsChecked != true)
            return;
        _updating = true;
        if (fromWidth)
            _height.Text = Math.Max(1, (int)Math.Round(Read(_width, _originalWidth) * (double)_originalHeight / _originalWidth)).ToString();
        else
            _width.Text = Math.Max(1, (int)Math.Round(Read(_height, _originalHeight) * (double)_originalWidth / _originalHeight)).ToString();
        _updating = false;
    }

    protected override bool Validate() => ResultWidth is > 0 and <= 30000 && ResultHeight is > 0 and <= 30000;
}

/// <summary>Tamanho da tela: margens em volta da imagem e a cor do fundo novo.</summary>
internal sealed class CanvasSizeDialog : EditorDialog
{
    private readonly TextBox _top = NumberBox(20), _right = NumberBox(20), _bottom = NumberBox(20), _left = NumberBox(20);
    private readonly ComboBox _color = new() { Width = 140 };

    public CanvasSizeDialog(Window owner) : base(owner, "Tamanho da tela")
    {
        Add(new TextBlock { Text = "Margens a acrescentar (negativo corta):", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8) });
        var all = NumberBox(20);
        all.TextChanged += (_, _) => { foreach (var box in new[] { _top, _right, _bottom, _left }) box.Text = all.Text; };
        Add(Row("Todas", all, "px"));
        Add(Row("Em cima", _top, "px"));
        Add(Row("À direita", _right, "px"));
        Add(Row("Embaixo", _bottom, "px"));
        Add(Row("À esquerda", _left, "px"));
        foreach (var name in new[] { "Branco", "Preto", "Transparente", "Cinza claro" })
            _color.Items.Add(name);
        _color.SelectedIndex = 0;
        Add(Row("Fundo", _color));
    }

    public Thickness Margins => new(Read(_left, 0), Read(_top, 0), Read(_right, 0), Read(_bottom, 0));

    public Color BackgroundColor => _color.SelectedIndex switch
    {
        1 => Colors.Black,
        2 => Colors.Transparent,
        3 => Color.FromRgb(0xE5, 0xE7, 0xEB),
        _ => Colors.White,
    };
}
