using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Capturo.Services;
using Microsoft.Win32;
using Path = System.Windows.Shapes.Path;

namespace Capturo.Editor;

public enum EditorTool
{
    Select,
    Arrow,
    Rectangle,
    Ellipse,
    Line,
    Pen,
    Highlighter,
    Text,
    Step,
    Pixelate,
    Crop,
}

public partial class EditorWindow : Window
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xEF, 0x44, 0x44), // vermelho
        Color.FromRgb(0xF5, 0x9E, 0x0B), // laranja
        Color.FromRgb(0xFA, 0xCC, 0x15), // amarelo
        Color.FromRgb(0x22, 0xC5, 0x5E), // verde
        Color.FromRgb(0x3B, 0x82, 0xF6), // azul
        Color.FromRgb(0xA8, 0x55, 0xF7), // roxo
        Color.FromRgb(0x11, 0x18, 0x27), // preto
        Color.FromRgb(0xFF, 0xFF, 0xFF), // branco
    };

    private readonly BitmapSource _source;
    private readonly AppSettings _settings;
    private readonly EditorHistory _history = new();
    private string? _filePath;
    private bool _dirty;

    private EditorTool _tool = EditorTool.Arrow;
    private Color _color = Palette[0];
    private double _size = 4;
    private int? _selected;
    private double _zoom = 1;

    // Gesto em andamento
    private bool _pointerDown;
    private Point _downPoint;
    private Annotation? _preview;
    private List<Point>? _penPoints;
    private Int32Rect? _cropPreview;
    private TextBox? _textEditor;

    public EditorWindow(BitmapSource source, string? filePath, AppSettings settings)
    {
        InitializeComponent();
        _source = source;
        _filePath = filePath;
        _settings = settings;

        BaseImage.Source = source;
        BaseImage.Width = source.PixelWidth;
        BaseImage.Height = source.PixelHeight;
        foreach (var layer in new[] { AnnotationLayer, PreviewLayer, SelectionLayer, InputLayer })
        {
            layer.Width = source.PixelWidth;
            layer.Height = source.PixelHeight;
        }

        BuildToolbar();
        SizeValue.Text = _size.ToString("0");
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => FitToWindow();
        Closing += OnClosing;
        Render();
    }

    // ───────────────────────── Barra de ferramentas ─────────────────────────

    private void BuildToolbar()
    {
        foreach (var button in ToolPanel.Children.OfType<ToggleButton>())
        {
            button.Click += (s, _) => SetTool(Enum.Parse<EditorTool>((string)((FrameworkElement)s).Tag));
        }

        foreach (var color in Palette)
        {
            var swatch = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Background = new SolidColorBrush(color),
                Tag = color,
                ToolTip = Capture.BitmapUtil.ToHex(color),
            };
            swatch.Click += (s, _) => SetColor((Color)((FrameworkElement)s).Tag);
            ColorPanel.Children.Add(swatch);
        }

        SetTool(_tool);
        SetColor(_color);
    }

    private void SetTool(EditorTool tool)
    {
        CommitText();
        _tool = tool;
        if (tool != EditorTool.Select)
            _selected = null;
        foreach (var button in ToolPanel.Children.OfType<ToggleButton>())
            button.IsChecked = (string)button.Tag == tool.ToString();
        InputLayer.Cursor = tool switch
        {
            EditorTool.Select => Cursors.Arrow,
            EditorTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
        Render();
    }

    private void SetColor(Color color)
    {
        _color = color;
        foreach (var swatch in ColorPanel.Children.OfType<Button>())
            swatch.BorderBrush = (Color)swatch.Tag == color ? Brushes.White : Brushes.Transparent;
        if (_textEditor != null)
            _textEditor.Foreground = new SolidColorBrush(color);
        ModifySelected(a => a.WithColor(color));
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _size = e.NewValue;
        if (SizeValue == null)
            return;
        SizeValue.Text = _size.ToString("0");
        if (_textEditor != null)
            _textEditor.FontSize = SizeMapping.FontSize(_size);
        ModifySelected(a => a.WithSize(_size));
    }

    private void ModifySelected(Func<Annotation, Annotation> change)
    {
        if (_selected is not { } index || index >= _history.Current.Items.Count)
            return;
        var items = _history.Current.Items;
        var updated = change(items[index]);
        if (updated != items[index])
            Commit(_history.Current with { Items = items.SetItem(index, updated) });
    }

    // ───────────────────────── Entrada do mouse ─────────────────────────

    private void Input_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(InputLayer);
        if (_textEditor != null)
        {
            CommitText();
            return;
        }

        switch (_tool)
        {
            case EditorTool.Text:
                OpenTextEditor(p);
                return;
            case EditorTool.Step:
                var step = new StepAnnotation(p, _history.Current.NextStepNumber, _color, SizeMapping.StepDiameter(_size));
                Commit(_history.Current with { Items = _history.Current.Items.Add(step) });
                return;
            case EditorTool.Select:
                _selected = HitTest(p);
                Render();
                if (_selected is null)
                    return;
                break;
            case EditorTool.Pen:
            case EditorTool.Highlighter:
                _penPoints = new List<Point> { p };
                break;
        }

        _pointerDown = true;
        _downPoint = p;
        InputLayer.CaptureMouse();
        e.Handled = true;
    }

    private void Input_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pointerDown)
            return;
        var p = ClampToImage(e.GetPosition(InputLayer));
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var rect = new Rect(_downPoint, shift ? SquareCorner(_downPoint, p) : p);
        var end = shift ? SnapAngle(_downPoint, p) : p;

        _preview = null;
        _cropPreview = null;
        switch (_tool)
        {
            case EditorTool.Arrow: _preview = new ArrowAnnotation(_downPoint, end, _color, _size); break;
            case EditorTool.Line: _preview = new LineAnnotation(_downPoint, end, _color, _size); break;
            case EditorTool.Rectangle: _preview = new RectangleAnnotation(rect, _color, _size); break;
            case EditorTool.Ellipse: _preview = new EllipseAnnotation(rect, _color, _size); break;
            case EditorTool.Pixelate: _preview = new PixelateAnnotation(rect); break;
            case EditorTool.Crop: _cropPreview = AnnotationRenderer.ToPixelArea(rect, _source); break;
            case EditorTool.Pen:
            case EditorTool.Highlighter:
                if (_penPoints != null && (p - _penPoints[^1]).Length >= 1.5)
                    _penPoints.Add(p);
                _preview = new PenAnnotation(_penPoints!.ToImmutableArray(), _color, _size, _tool == EditorTool.Highlighter);
                break;
            case EditorTool.Select when _selected is { } index:
                _preview = _history.Current.Items[index].Offset(p - _downPoint);
                break;
        }
        RenderPreview();
    }

    private void Input_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerDown)
            return;
        _pointerDown = false;
        InputLayer.ReleaseMouseCapture();

        var state = _history.Current;
        if (_tool == EditorTool.Crop)
        {
            if (_cropPreview is { Width: >= 4, Height: >= 4 } crop)
                Commit(state with { Crop = crop });
        }
        else if (_tool == EditorTool.Select)
        {
            if (_selected is { } index && _preview != null && _preview != state.Items[index])
                Commit(state with { Items = state.Items.SetItem(index, _preview) });
        }
        else if (_preview != null && IsMeaningful(_preview))
        {
            Commit(state with { Items = state.Items.Add(_preview) });
        }

        _preview = null;
        _cropPreview = null;
        _penPoints = null;
        Render();
    }

    private static bool IsMeaningful(Annotation a) => a switch
    {
        PenAnnotation p => p.Points.Length > 1,
        ArrowAnnotation arrow => (arrow.End - arrow.Start).Length >= 4,
        LineAnnotation line => (line.End - line.Start).Length >= 4,
        _ => a.Bounds.Width >= 4 && a.Bounds.Height >= 4,
    };

    private int? HitTest(Point p)
    {
        var items = _history.Current.Items;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Bounds.Contains(p))
                return i;
        }
        return null;
    }

    private Point ClampToImage(Point p) =>
        new(Math.Clamp(p.X, 0, _source.PixelWidth), Math.Clamp(p.Y, 0, _source.PixelHeight));

    private static Point SquareCorner(Point origin, Point p)
    {
        double side = Math.Max(Math.Abs(p.X - origin.X), Math.Abs(p.Y - origin.Y));
        return new Point(origin.X + Math.Sign(p.X - origin.X) * side, origin.Y + Math.Sign(p.Y - origin.Y) * side);
    }

    private static Point SnapAngle(Point origin, Point p)
    {
        var v = p - origin;
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return origin + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }

    // ───────────────────────── Texto ─────────────────────────

    private void OpenTextEditor(Point p)
    {
        _textEditor = new TextBox
        {
            MinWidth = 80,
            AcceptsReturn = false,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(_color),
            CaretBrush = new SolidColorBrush(_color),
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = SizeMapping.FontSize(_size),
            Padding = new Thickness(0),
        };
        _textEditor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                int caret = _textEditor.CaretIndex;
                _textEditor.Text = _textEditor.Text.Insert(caret, "\n");
                _textEditor.CaretIndex = caret + 1;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                CommitText();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseTextEditor();
                e.Handled = true;
            }
        };
        Canvas.SetLeft(_textEditor, p.X);
        Canvas.SetTop(_textEditor, p.Y);
        ContentCanvas.Children.Add(_textEditor);
        _textEditor.Focus();
    }

    private void CommitText()
    {
        if (_textEditor == null)
            return;
        string text = _textEditor.Text.TrimEnd();
        var origin = new Point(Canvas.GetLeft(_textEditor), Canvas.GetTop(_textEditor));
        double fontSize = _textEditor.FontSize;
        CloseTextEditor();
        if (text.Length > 0)
            Commit(_history.Current with { Items = _history.Current.Items.Add(new TextAnnotation(origin, text, _color, fontSize)) });
    }

    private void CloseTextEditor()
    {
        if (_textEditor == null)
            return;
        ContentCanvas.Children.Remove(_textEditor);
        _textEditor = null;
        Focus();
    }

    // ───────────────────────── Teclado ─────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_textEditor != null)
            return;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool shift = mods.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when shift: Redo(); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.C: CopyResult(); break;
                case Key.S when shift: SaveAs(); break;
                case Key.S: Save(); break;
                case Key.D0: SetZoom(1); break;
                case Key.D1: FitToWindow(); break;
                case Key.OemPlus or Key.Add: SetZoom(_zoom * 1.25); break;
                case Key.OemMinus or Key.Subtract: SetZoom(_zoom / 1.25); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        EditorTool? tool = key switch
        {
            Key.V => EditorTool.Select,
            Key.A => EditorTool.Arrow,
            Key.R => EditorTool.Rectangle,
            Key.E => EditorTool.Ellipse,
            Key.L => EditorTool.Line,
            Key.P => EditorTool.Pen,
            Key.H => EditorTool.Highlighter,
            Key.T => EditorTool.Text,
            Key.N => EditorTool.Step,
            Key.B => EditorTool.Pixelate,
            Key.C => EditorTool.Crop,
            _ => null,
        };
        if (tool is { } t)
        {
            SetTool(t);
            e.Handled = true;
            return;
        }

        if ((key == Key.Delete || key == Key.Back) && _selected is { } index)
        {
            _selected = null;
            Commit(_history.Current with { Items = _history.Current.Items.RemoveAt(index) });
            e.Handled = true;
        }
        else if (key == Key.Escape)
        {
            if (_selected != null)
            {
                _selected = null;
                Render();
            }
            else
            {
                Close();
            }
            e.Handled = true;
        }
    }

    // ───────────────────────── Estado e desenho ─────────────────────────

    private void Commit(EditorState next)
    {
        _history.Push(next);
        _dirty = true;
        Render();
    }

    private void Undo()
    {
        CommitText();
        if (_history.Undo())
        {
            _selected = null;
            _dirty = true;
            Render();
        }
    }

    private void Redo()
    {
        if (_history.Redo())
        {
            _selected = null;
            _dirty = true;
            Render();
        }
    }

    private void Render()
    {
        var state = _history.Current;
        AnnotationLayer.Children.Clear();
        for (int i = 0; i < state.Items.Count; i++)
        {
            // Enquanto um item é arrastado, a prévia o substitui.
            if (_pointerDown && _tool == EditorTool.Select && _selected == i && _preview != null)
                continue;
            AnnotationLayer.Children.Add(AnnotationRenderer.Create(state.Items[i], _source));
        }

        var crop = state.Crop ?? new Int32Rect(0, 0, _source.PixelWidth, _source.PixelHeight);
        Surface.Width = crop.Width;
        Surface.Height = crop.Height;
        ContentCanvas.Width = _source.PixelWidth;
        ContentCanvas.Height = _source.PixelHeight;
        CropShift.X = -crop.X;
        CropShift.Y = -crop.Y;

        RenderPreview();
        RenderSelection();

        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        string name = _filePath != null ? System.IO.Path.GetFileName(_filePath) : "nova captura";
        Title = $"{(_dirty ? "• " : "")}{name} — Capturo";
        StatusText.Text = $"{crop.Width} × {crop.Height} px   ·   {ToolHint(_tool)}";
    }

    private void RenderPreview()
    {
        PreviewLayer.Children.Clear();
        if (_preview != null)
        {
            PreviewLayer.Children.Add(AnnotationRenderer.Create(_preview, _source));
            if (_tool == EditorTool.Select)
                RenderAnnotationsExceptSelected();
        }
        if (_cropPreview is { } crop)
        {
            var dim = new Path
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(0, 0, _source.PixelWidth, _source.PixelHeight)),
                    new RectangleGeometry(new Rect(crop.X, crop.Y, crop.Width, crop.Height))),
            };
            PreviewLayer.Children.Add(dim);
            PreviewLayer.Children.Add(DashedBox(new Rect(crop.X, crop.Y, crop.Width, crop.Height)));
        }
    }

    private void RenderAnnotationsExceptSelected()
    {
        // Mantém a camada principal sem o item que está sendo movido.
        if (_selected is not { } index || AnnotationLayer.Children.Count != _history.Current.Items.Count)
            return;
        AnnotationLayer.Children.RemoveAt(index);
    }

    private void RenderSelection()
    {
        SelectionLayer.Children.Clear();
        if (_tool != EditorTool.Select || _selected is not { } index || index >= _history.Current.Items.Count)
            return;
        var bounds = (_pointerDown && _preview != null ? _preview : _history.Current.Items[index]).Bounds;
        SelectionLayer.Children.Add(DashedBox(bounds));
    }

    private Rectangle DashedBox(Rect rect)
    {
        var box = new Rectangle
        {
            Width = Math.Max(1, rect.Width),
            Height = Math.Max(1, rect.Height),
            Stroke = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            StrokeThickness = 1.5 / Math.Max(_zoom, 0.1),
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        Canvas.SetLeft(box, rect.X);
        Canvas.SetTop(box, rect.Y);
        return box;
    }

    private static string ToolHint(EditorTool tool) => tool switch
    {
        EditorTool.Select => "Clique num item para selecionar, arraste para mover, Delete apaga",
        EditorTool.Arrow => "Arraste do início até a ponta da seta",
        EditorTool.Text => "Clique onde o texto deve começar",
        EditorTool.Step => "Cada clique adiciona o próximo número",
        EditorTool.Pixelate => "Arraste sobre o que deve ficar ilegível",
        EditorTool.Crop => "Arraste a área que deve ficar; Ctrl+Z desfaz o recorte",
        _ => "Arraste para desenhar · Shift restringe a forma",
    };

    // ───────────────────────── Zoom ─────────────────────────

    /// <summary>Zoom 1 = um pixel da captura num pixel físico da tela, em qualquer escala do Windows.</summary>
    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.05, 16);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = _zoom / dpi;
        ZoomText.Text = $"{_zoom * 100:0}%";
        RenderOptions.SetBitmapScalingMode(BaseImage, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        RenderSelection();
    }

    private void FitToWindow()
    {
        var crop = _history.Current.Crop ?? new Int32Rect(0, 0, _source.PixelWidth, _source.PixelHeight);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double availableWidth = Math.Max(100, Scroller.ActualWidth - 56) * dpi;
        double availableHeight = Math.Max(100, Scroller.ActualHeight - 56) * dpi;
        SetZoom(Math.Min(1, Math.Min(availableWidth / crop.Width, availableHeight / crop.Height)));
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        SetZoom(e.Delta > 0 ? _zoom * 1.15 : _zoom / 1.15);
        e.Handled = true;
    }

    // ───────────────────────── Exportação ─────────────────────────

    /// <summary>Imagem final: captura + anotações, com o recorte aplicado.</summary>
    public BitmapSource RenderResult()
    {
        CommitText();
        int width = _source.PixelWidth, height = _source.PixelHeight;
        var canvas = new Canvas { Width = width, Height = height };
        canvas.Children.Add(new Image { Source = _source, Width = width, Height = height, Stretch = Stretch.Fill });
        foreach (var item in _history.Current.Items)
            canvas.Children.Add(AnnotationRenderer.Create(item, _source));
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        bitmap.Freeze();
        return _history.Current.Crop is { } crop ? Capture.BitmapUtil.Crop(bitmap, crop) : bitmap;
    }

    private void CopyResult()
    {
        if (OutputService.CopyToClipboard(RenderResult()))
            Flash("Copiado para a área de transferência");
    }

    private bool Save()
    {
        if (_filePath == null)
            return SaveAs();
        try
        {
            OutputService.Save(RenderResult(), _filePath);
            _dirty = false;
            Render();
            Flash($"Salvo em {_filePath}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Falha ao salvar.", ex);
            MessageBox.Show(this, $"Não foi possível salvar:\n{ex.Message}", "Capturo", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool SaveAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg",
            FileName = _filePath != null ? System.IO.Path.GetFileName(_filePath) : OutputService.MakeCapturePath("", DateTime.Now),
            InitialDirectory = _filePath != null ? System.IO.Path.GetDirectoryName(_filePath) : _settings.SaveFolder,
        };
        if (dialog.ShowDialog(this) != true)
            return false;
        _filePath = dialog.FileName;
        return Save();
    }

    private void Flash(string message) => StatusText.Text = message;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CommitText();
        if (!_dirty)
            return;
        var answer = MessageBox.Show(this, "Salvar as alterações nesta captura?", "Capturo",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !Save()))
            e.Cancel = true;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyResult();
    private void Save_Click(object sender, RoutedEventArgs e) => Save();
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();
}
