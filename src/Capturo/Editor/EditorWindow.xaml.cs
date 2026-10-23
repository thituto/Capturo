using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Capturo.Capture;
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
    Callout,
    Step,
    Spotlight,
    Magnifier,
    Pixelate,
    Stamp,
    Image,
    Brace,
    Crop,
    CutOut,
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
        Color.FromRgb(0xEC, 0x48, 0x99), // rosa
        Color.FromRgb(0x11, 0x18, 0x27), // preto
        Color.FromRgb(0x9C, 0xA3, 0xAF), // cinza
        Color.FromRgb(0xFF, 0xFF, 0xFF), // branco
    };

    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));

    private enum Gesture { None, Draw, Move, Resize, Crop, CutOut }

    private readonly AppSettings _settings;
    private readonly EditorHistory _history;
    private string? _filePath;
    private bool _dirty;

    private EditorTool _tool = EditorTool.Arrow;
    private Color _color = Palette[0];
    private double _size = 4;
    private ShapeStyle _style = ShapeStyle.Default;
    private string _stampGlyph = StampAnnotation.Glyphs[0];
    private bool _cutHorizontal = true;
    private int? _selected;
    private double _zoom = 1;
    private bool _syncingControls;

    // Gesto em andamento
    private Gesture _gesture;
    private Point _downPoint;
    private int _gestureIndex;
    private int _handleIndex;
    private Annotation? _gestureOriginal;
    private Annotation? _draft;
    private List<Point>? _penPoints;
    private Int32Rect? _areaDraft;

    // Edição de texto (texto solto ou balão)
    private TextBox? _textEditor;
    private int? _editingIndex;
    private Annotation? _editingTemplate;

    public EditorWindow(BitmapSource source, string? filePath, AppSettings settings)
    {
        InitializeComponent();
        _filePath = filePath;
        _settings = settings;
        _history = new EditorHistory(EditorState.For(source, LoadEffects(settings)));

        BuildToolbar();
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += (_, _) => FitToWindow();
        Closing += OnClosing;
        Render();
    }

    private BitmapSource Image => _history.Current.Image;
    private Size ImageSize => new(Image.PixelWidth, Image.PixelHeight);

    // ───────────────────────── Ferramentas e propriedades ─────────────────────────

    private void BuildToolbar()
    {
        foreach (var button in ToolPanel.Children.OfType<RadioButton>())
            button.Click += (s, _) => SetTool(Enum.Parse<EditorTool>((string)((FrameworkElement)s).Tag));

        foreach (var color in Palette)
        {
            var swatch = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Background = new SolidColorBrush(color),
                Tag = color,
                ToolTip = BitmapUtil.ToHex(color),
                Margin = new Thickness(0, 0, 4, 4),
            };
            swatch.Click += (s, _) => SetColor((Color)((FrameworkElement)s).Tag);
            ColorPanel.Children.Add(swatch);
        }

        foreach (var glyph in StampAnnotation.Glyphs)
        {
            var button = new Button { Content = glyph, FontFamily = new FontFamily("Segoe UI Symbol"), Width = 30, Margin = new Thickness(0, 0, 4, 4), Tag = glyph };
            button.Click += (s, _) =>
            {
                _stampGlyph = (string)((FrameworkElement)s).Tag;
                ModifySelected(a => a is StampAnnotation st ? st with { Glyph = _stampGlyph } : a);
                UpdateStampButtons();
            };
            StampGlyphs.Children.Add(button);
        }

        SizeValue.Text = _size.ToString("0");
        OpacityValue.Text = "100%";
        SetTool(_tool);
        SetColor(_color);
        UpdateStampButtons();
    }

    private void UpdateStampButtons()
    {
        foreach (var button in StampGlyphs.Children.OfType<Button>())
            button.Background = (string)button.Tag == _stampGlyph ? AccentBrush : Brushes.Transparent;
    }

    private void SetTool(EditorTool tool)
    {
        CommitText();
        _tool = tool;
        foreach (var button in ToolPanel.Children.OfType<RadioButton>())
            button.IsChecked = (string)button.Tag == tool.ToString();
        InputLayer.Cursor = DefaultCursor();
        if (tool == EditorTool.Spotlight)
            SetOpacityControl(0.6);
        StampPanel.Visibility = tool == EditorTool.Stamp ? Visibility.Visible : Visibility.Collapsed;
        PanelTitle.Text = ToolName(tool);
        ToolHelp.Text = ToolHint(tool);
        if (tool == EditorTool.Image)
            InsertImageFromFile(null);
        Render();
    }

    private Cursor DefaultCursor() => _tool switch
    {
        EditorTool.Select => Cursors.Arrow,
        EditorTool.Text => Cursors.IBeam,
        EditorTool.Image => Cursors.Arrow,
        _ => Cursors.Cross,
    };

    private void SetColor(Color color)
    {
        _color = color;
        foreach (var swatch in ColorPanel.Children.OfType<Button>())
            swatch.BorderBrush = (Color)swatch.Tag == color ? Brushes.White : Brushes.Transparent;
        if (_textEditor != null)
            _textEditor.Foreground = new SolidColorBrush(_editingTemplate is CalloutAnnotation ? Geometry2D.ContrastText(color) : color);
        if (!_syncingControls)
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
        if (!_syncingControls)
            ModifySelected(a => a.WithSize(_size));
    }

    private void StyleToggle_Click(object sender, RoutedEventArgs e)
    {
        _style = _style with { Filled = FillToggle.IsChecked == true, Rounded = RoundToggle.IsChecked == true };
        ModifySelected(a => a.WithStyle(_style));
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue == null)
            return;
        OpacityValue.Text = $"{e.NewValue:0}%";
        _style = _style with { Opacity = e.NewValue / 100 };
        if (!_syncingControls)
            ModifySelected(a => a.WithStyle(_style));
    }

    private void SetOpacityControl(double opacity)
    {
        _syncingControls = true;
        OpacitySlider.Value = Math.Round(opacity * 100);
        _syncingControls = false;
        _style = _style with { Opacity = opacity };
    }

    /// <summary>Ao selecionar um item, o painel de propriedades passa a mostrar o estilo dele.</summary>
    private void SyncControlsTo(Annotation item)
    {
        _syncingControls = true;
        try
        {
            switch (item)
            {
                case RectangleAnnotation r: ApplyStyleToControls(r.Style); break;
                case EllipseAnnotation el: ApplyStyleToControls(el.Style); break;
                case SpotlightAnnotation s: ApplyStyleToControls(new ShapeStyle(false, s.Dim, s.Rounded)); break;
                case TextAnnotation t: FillToggle.IsChecked = t.Filled; _style = _style with { Filled = t.Filled }; break;
                case StampAnnotation st: _stampGlyph = st.Glyph; UpdateStampButtons(); break;
            }
        }
        finally
        {
            _syncingControls = false;
        }
    }

    private void ApplyStyleToControls(ShapeStyle style)
    {
        _style = style;
        FillToggle.IsChecked = style.Filled;
        RoundToggle.IsChecked = style.Rounded;
        OpacitySlider.Value = Math.Round(style.Opacity * 100);
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

    private void Properties_Click(object sender, RoutedEventArgs e) => ToggleProperties();

    private void ToggleProperties() =>
        PropertiesPanel.Visibility = PropertiesPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    // ───────────────────────── Bordas (efeitos da imagem) ─────────────────────────

    private static ImageEffects LoadEffects(AppSettings settings)
    {
        var e = settings.Effects;
        var color = (Color)ColorConverter.ConvertFromString(e.BorderColor);
        return new ImageEffects(e.Border, color, e.BorderWidth, e.Shadow, e.RoundedCorners,
            Enum.TryParse<TornEdges>(e.Torn, out var torn) ? torn : TornEdges.None);
    }

    private void ApplyEffects(ImageEffects effects)
    {
        Commit(_history.Current with { Effects = effects });
        _settings.Effects = new AppSettings.EffectsSettings
        {
            Border = effects.Border,
            BorderColor = BitmapUtil.ToHex(effects.BorderColor),
            BorderWidth = effects.BorderWidth,
            Shadow = effects.Shadow,
            RoundedCorners = effects.RoundedCorners,
            Torn = effects.Torn.ToString(),
        };
        _settings.Save();
    }

    /// <summary>Menu do botão Bordas: liga/desliga cada efeito e escolhe cor, espessura e tipo de rasgo.</summary>
    private void Borders_Click(object sender, RoutedEventArgs e)
    {
        var fx = _history.Current.Effects;
        var panel = new StackPanel { Width = 230 };
        Popup? popup = null;

        CheckBox Toggle(string text, bool value, Func<bool, ImageEffects> apply)
        {
            var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(6, 4, 6, 4) };
            box.Click += (_, _) => ApplyEffects(apply(box.IsChecked == true));
            panel.Children.Add(box);
            return box;
        }

        Toggle("Borda", fx.Border, v => _history.Current.Effects with { Border = v });
        var colors = new WrapPanel { Margin = new Thickness(24, 0, 6, 4) };
        foreach (var color in Palette)
        {
            var swatch = new Button { Style = (Style)FindResource("SwatchButton"), Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 3, 3),
                BorderBrush = color == fx.BorderColor ? Brushes.White : Brushes.Transparent };
            swatch.Click += (_, _) => { popup!.IsOpen = false; ApplyEffects(_history.Current.Effects with { Border = true, BorderColor = color }); };
            colors.Children.Add(swatch);
        }
        panel.Children.Add(colors);
        var widths = new WrapPanel { Margin = new Thickness(24, 0, 6, 6) };
        foreach (int w in new[] { 1, 2, 3, 4, 6, 8, 12 })
        {
            var button = new Button { Content = $"{w}", Width = 26, Margin = new Thickness(0, 0, 3, 0),
                Background = Math.Abs(fx.BorderWidth - w) < 0.1 ? AccentBrush : Brushes.Transparent };
            button.Click += (_, _) => { popup!.IsOpen = false; ApplyEffects(_history.Current.Effects with { Border = true, BorderWidth = w }); };
            widths.Children.Add(button);
        }
        panel.Children.Add(widths);
        Toggle("Sombra", fx.Shadow, v => _history.Current.Effects with { Shadow = v });
        Toggle("Cantos arredondados", fx.RoundedCorners, v => _history.Current.Effects with { RoundedCorners = v });
        panel.Children.Add(new TextBlock { Text = "Borda rasgada", Foreground = Brushes.Gray, Margin = new Thickness(6, 8, 6, 2) });
        foreach (var (label, torn) in new[] { ("Nenhuma", TornEdges.None), ("Embaixo", TornEdges.Bottom), ("Embaixo e à direita", TornEdges.BottomRight), ("Todos os lados", TornEdges.All) })
        {
            var option = new RadioButton { Content = label, IsChecked = fx.Torn == torn, GroupName = "torn", Margin = new Thickness(12, 2, 6, 2), Foreground = Brushes.White };
            option.Click += (_, _) => ApplyEffects(_history.Current.Effects with { Torn = torn });
            panel.Children.Add(option);
        }
        popup = ShowPopup(BordersButton, panel);
    }

    private Popup ShowPopup(FrameworkElement anchor, UIElement content)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = (Brush)FindResource("BarBackground"),
                BorderBrush = (Brush)Application.Current.FindResource("PanelBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4),
                Child = content,
            },
        };
        popup.IsOpen = true;
        return popup;
    }

    /// <summary>Menu suspenso simples: uma lista de ações abaixo do botão.</summary>
    private void ShowMenu(object sender, IEnumerable<(string Label, Action Action)> choices)
    {
        var anchor = (FrameworkElement)sender;
        var panel = new StackPanel { MinWidth = 200 };
        Popup? popup = null;
        foreach (var (label, action) in choices)
        {
            var button = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => { popup!.IsOpen = false; action(); };
            panel.Children.Add(button);
        }
        popup = ShowPopup(anchor, panel);
    }

    // ───────────────────────── Botões da barra de cima ─────────────────────────

    private void CopyClose_Click(object sender, RoutedEventArgs e) => CopyAndClose();

    private void CopyAndClose()
    {
        var result = RenderResult();
        OutputService.CopyToClipboard(result);
        if (_dirty && _filePath != null)
            SaveTo(_filePath, result);
        _dirty = false;
        Close();
    }

    private void CopyCloseMenu_Click(object sender, RoutedEventArgs e) => ShowMenu(sender, new (string, Action)[]
    {
        ("Copiar e sair", CopyAndClose),
        ("Salvar e sair", () => { if (Save()) Close(); }),
        ("Sair sem salvar", () => { _dirty = false; Close(); }),
    });

    private void Print_Click(object sender, RoutedEventArgs e) => PrintResult();

    private void PrintResult()
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
            return;
        var image = RenderResult();
        double scale = Math.Min(1, Math.Min(dialog.PrintableAreaWidth / image.PixelWidth, dialog.PrintableAreaHeight / image.PixelHeight));
        var page = new Image { Source = image, Width = image.PixelWidth * scale, Height = image.PixelHeight * scale };
        page.Measure(new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight));
        page.Arrange(new Rect(page.DesiredSize));
        dialog.PrintVisual(page, Title);
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyResult();

    private void CopyMenu_Click(object sender, RoutedEventArgs e) => ShowMenu(sender, new (string, Action)[]
    {
        ("Copiar a imagem", CopyResult),
        ("Copiar como arquivo (para colar no Explorer, e-mail...)", CopyAsFile),
        ("Copiar o caminho do arquivo", () => { if (EnsureSaved() is { } path) OutputService.CopyText(path); }),
    });

    private void CopyAsFile()
    {
        if (EnsureSaved() is not { } path)
            return;
        var files = new StringCollection { path };
        Clipboard.SetFileDropList(files);
        Flash("Arquivo copiado");
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void UndoMenu_Click(object sender, RoutedEventArgs e) => ShowMenu(sender, new (string, Action)[]
    {
        ("Refazer (Ctrl+Y)", Redo),
        ("Desfazer tudo", () => { CommitText(); _history.UndoAll(); _selected = null; _dirty = true; Render(); FitToWindow(); }),
    });

    private void Crop_Click(object sender, RoutedEventArgs e) => SetTool(EditorTool.Crop);

    private void CropMenu_Click(object sender, RoutedEventArgs e) => ShowMenu(sender, new (string, Action)[]
    {
        ("Cortar arrastando uma área", () => SetTool(EditorTool.Crop)),
        ("Remover bordas de cor uniforme", () => ApplyImageChange(ImageOps.TrimUniformBorders(_history.Current))),
    });

    private void CutOut_Click(object sender, RoutedEventArgs e) => ShowMenu(sender, new (string, Action)[]
    {
        ("Recortar faixa horizontal (arraste de cima para baixo)", () => { _cutHorizontal = true; SetTool(EditorTool.CutOut); }),
        ("Recortar faixa vertical (arraste da esquerda para a direita)", () => { _cutHorizontal = false; SetTool(EditorTool.CutOut); }),
    });

    private void Resize_Click(object sender, RoutedEventArgs e)
    {
        CommitText();
        var dialog = new ResizeDialog(this, Image.PixelWidth, Image.PixelHeight);
        if (dialog.ShowDialog() == true)
            ApplyImageChange(ImageOps.Resize(_history.Current, dialog.ResultWidth, dialog.ResultHeight));
    }

    private void CanvasSize_Click(object sender, RoutedEventArgs e)
    {
        CommitText();
        var dialog = new CanvasSizeDialog(this);
        if (dialog.ShowDialog() == true)
            ApplyImageChange(ImageOps.ExpandCanvas(_history.Current, dialog.Margins, dialog.BackgroundColor));
    }

    private void Publish_Click(object sender, RoutedEventArgs e) => ShowMenu(sender, new (string, Action)[]
    {
        ("Mostrar na pasta", () => { if (EnsureSaved() is { } path) TrayIcon.RevealFile(path); }),
        ("Abrir com outro programa…", () =>
        {
            if (EnsureSaved() is { } path)
                Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false });
        }),
        ("Abrir no visualizador padrão", () =>
        {
            if (EnsureSaved() is { } path)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }),
        ("Copiar o caminho do arquivo", () => { if (EnsureSaved() is { } path) OutputService.CopyText(path); }),
    });

    /// <summary>Troca a imagem (corte, recorte, tamanho) mantendo o desfazer, e reajusta o zoom.</summary>
    private void ApplyImageChange(EditorState next)
    {
        if (next == _history.Current)
        {
            Flash("Nada a alterar");
            return;
        }
        _selected = null;
        Commit(next);
        Dispatcher.BeginInvoke(FitToWindow, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ───────────────────────── Imagens inseridas ─────────────────────────

    private void InsertImageFromFile(Point? center)
    {
        var dialog = new OpenFileDialog { Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|Todos os arquivos|*.*" };
        if (dialog.ShowDialog(this) != true)
        {
            SetToolSilently(EditorTool.Select);
            return;
        }
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(dialog.FileName), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            InsertImage(BitmapUtil.Materialize(decoder.Frames[0]), center);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException)
        {
            MessageBox.Show(this, $"Não foi possível abrir a imagem:\n{ex.Message}", "Capturo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        SetToolSilently(EditorTool.Select);
    }

    private void InsertImage(BitmapSource picture, Point? center)
    {
        var at = center ?? new Point(Image.PixelWidth / 2.0, Image.PixelHeight / 2.0);
        var item = ImageAnnotation.Create(picture, at, ImageSize);
        Commit(_history.Current with { Items = _history.Current.Items.Add(item) });
        Select(_history.Current.Items.Count - 1);
    }

    /// <summary>Muda a ferramenta sem disparar as ações de quando o usuário a escolhe (evita reabrir o diálogo de imagem).</summary>
    private void SetToolSilently(EditorTool tool)
    {
        _tool = tool;
        foreach (var button in ToolPanel.Children.OfType<RadioButton>())
            button.IsChecked = (string)button.Tag == tool.ToString();
        InputLayer.Cursor = DefaultCursor();
        PanelTitle.Text = ToolName(tool);
        ToolHelp.Text = ToolHint(tool);
    }

    private void PasteImage()
    {
        if (!Clipboard.ContainsImage())
            return;
        var picture = Clipboard.GetImage();
        if (picture != null)
            InsertImage(BitmapUtil.Materialize(picture), null);
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

        var items = _history.Current.Items;

        // Duplo clique num texto ou balão reabre a edição do texto.
        if (e.ClickCount == 2 && TopmostAt(p) is { } textIndex && items[textIndex] is TextAnnotation or CalloutAnnotation)
        {
            EditText(textIndex);
            e.Handled = true;
            return;
        }

        // Alças do item selecionado têm prioridade em qualquer ferramenta.
        if (_selected is { } selected && selected < items.Count && HandleAt(items[selected], p) is { } handle)
        {
            BeginGesture(Gesture.Resize, p, selected, items[selected]);
            _handleIndex = handle;
            e.Handled = true;
            return;
        }

        switch (_tool)
        {
            case EditorTool.Crop:
                BeginGesture(Gesture.Crop, p);
                break;

            case EditorTool.CutOut:
                BeginGesture(Gesture.CutOut, p);
                break;

            case EditorTool.Text:
                if (TopmostAt(p) is { } existing && items[existing] is TextAnnotation)
                    EditText(existing);
                else
                    OpenTextEditor(new TextAnnotation(p, "", _color, SizeMapping.FontSize(_size), _style.Filled), null);
                break;

            case EditorTool.Step:
                Commit(_history.Current with { Items = items.Add(new StepAnnotation(p, _history.Current.NextStepNumber, _color, SizeMapping.StepDiameter(_size))) });
                Select(items.Count);
                break;

            case EditorTool.Stamp:
                Commit(_history.Current with { Items = items.Add(new StampAnnotation(p, _stampGlyph, _color, SizeMapping.StampSize(_size))) });
                Select(items.Count);
                break;

            case EditorTool.Select:
            case EditorTool.Image:
                Select(TopmostAt(p));
                if (_selected is { } hit)
                    BeginGesture(Gesture.Move, p, hit, items[hit]);
                break;

            default:
                // Com uma ferramenta de desenho, clicar dentro do item selecionado move-o; fora, começa um novo.
                if (_selected is { } current && current < items.Count && items[current].HitTest(p))
                {
                    BeginGesture(Gesture.Move, p, current, items[current]);
                }
                else
                {
                    Select(null);
                    if (_tool is EditorTool.Pen or EditorTool.Highlighter)
                        _penPoints = new List<Point> { p };
                    BeginGesture(Gesture.Draw, p);
                }
                break;
        }
        e.Handled = true;
    }

    private void BeginGesture(Gesture gesture, Point p, int index = -1, Annotation? original = null)
    {
        _gesture = gesture;
        _downPoint = p;
        _gestureIndex = index;
        _gestureOriginal = original;
        _draft = null;
        _areaDraft = null;
        InputLayer.CaptureMouse();
    }

    private void Input_MouseMove(object sender, MouseEventArgs e)
    {
        if (_gesture == Gesture.None)
        {
            UpdateHoverCursor(e.GetPosition(InputLayer));
            return;
        }

        var p = ClampToImage(e.GetPosition(InputLayer));
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_gesture)
        {
            case Gesture.Draw:
                _draft = CreateDraft(p, shift);
                break;
            case Gesture.Move:
                _draft = _gestureOriginal!.OffsetAt(_downPoint, p - _downPoint);
                break;
            case Gesture.Resize:
                _draft = _gestureOriginal!.MoveHandle(_handleIndex, p);
                break;
            case Gesture.Crop:
                _areaDraft = AnnotationRenderer.ToPixelArea(new Rect(_downPoint, shift ? SquareCorner(_downPoint, p) : p), Image);
                break;
            case Gesture.CutOut:
                _areaDraft = CutBand(_downPoint, p);
                break;
        }
        Render();
    }

    /// <summary>Faixa a recortar: atravessa a imagem inteira na direção escolhida.</summary>
    private Int32Rect CutBand(Point from, Point to)
    {
        if (_cutHorizontal)
        {
            int top = (int)Math.Floor(Math.Min(from.Y, to.Y)), bottom = (int)Math.Ceiling(Math.Max(from.Y, to.Y));
            return new Int32Rect(0, top, Image.PixelWidth, Math.Max(0, bottom - top));
        }
        int left = (int)Math.Floor(Math.Min(from.X, to.X)), right = (int)Math.Ceiling(Math.Max(from.X, to.X));
        return new Int32Rect(left, 0, Math.Max(0, right - left), Image.PixelHeight);
    }

    private Annotation? CreateDraft(Point p, bool shift)
    {
        var rect = new Rect(_downPoint, shift ? SquareCorner(_downPoint, p) : p);
        var end = shift ? SnapAngle(_downPoint, p) : p;
        switch (_tool)
        {
            case EditorTool.Arrow: return new ArrowAnnotation(_downPoint, end, _color, _size);
            case EditorTool.Line: return new LineAnnotation(_downPoint, end, _color, _size);
            case EditorTool.Brace: return new BraceAnnotation(_downPoint, end, _color, _size);
            case EditorTool.Rectangle: return new RectangleAnnotation(rect, _color, _size, _style);
            case EditorTool.Ellipse: return new EllipseAnnotation(rect, _color, _size, _style with { Rounded = false });
            case EditorTool.Pixelate: return new PixelateAnnotation(rect);
            case EditorTool.Spotlight: return new SpotlightAnnotation(rect, Math.Clamp(_style.Opacity, 0.1, 0.95), _style.Rounded);
            case EditorTool.Magnifier: return MagnifierAnnotation.Create(rect, ImageSize, _color, _size);
            case EditorTool.Callout: return CalloutAnnotation.Create(rect, ImageSize, _color, SizeMapping.FontSize(_size));
            case EditorTool.Pen:
            case EditorTool.Highlighter:
                if (_penPoints != null && (p - _penPoints[^1]).Length >= 1.5)
                    _penPoints.Add(p);
                return new PenAnnotation(_penPoints!.ToImmutableArray(), _color, _size, _tool == EditorTool.Highlighter);
            default:
                return null;
        }
    }

    private void Input_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_gesture == Gesture.None)
            return;
        var gesture = _gesture;
        var draft = _draft;
        var area = _areaDraft;
        int index = _gestureIndex;
        var original = _gestureOriginal;
        _gesture = Gesture.None;
        _draft = null;
        _areaDraft = null;
        _penPoints = null;
        InputLayer.ReleaseMouseCapture();

        var state = _history.Current;
        switch (gesture)
        {
            case Gesture.Draw when draft != null && IsMeaningful(draft):
                Commit(state with { Items = state.Items.Add(draft) });
                Select(state.Items.Count);
                if (draft is CalloutAnnotation)
                    EditText(state.Items.Count);
                break;
            case Gesture.Move or Gesture.Resize when draft != null && draft != original:
                Commit(state with { Items = state.Items.SetItem(index, draft) });
                break;
            case Gesture.Crop when area is { Width: >= 4, Height: >= 4 } crop:
                ApplyImageChange(ImageOps.Crop(state, crop));
                SetToolSilently(EditorTool.Select);
                break;
            case Gesture.CutOut when area is { } band && (_cutHorizontal ? band.Height : band.Width) >= 2:
                ApplyImageChange(_cutHorizontal
                    ? ImageOps.CutOut(state, band.Y, band.Height, horizontal: true)
                    : ImageOps.CutOut(state, band.X, band.Width, horizontal: false));
                break;
        }
        Render();
    }

    private void Select(int? index)
    {
        _selected = index;
        if (index is { } i && i < _history.Current.Items.Count)
            SyncControlsTo(_history.Current.Items[i]);
        Render();
    }

    private static bool IsMeaningful(Annotation a) => a switch
    {
        PenAnnotation p => p.Points.Length > 1,
        ArrowAnnotation arrow => (arrow.End - arrow.Start).Length >= 4,
        LineAnnotation line => (line.End - line.Start).Length >= 4,
        BraceAnnotation brace => (brace.End - brace.Start).Length >= 8,
        MagnifierAnnotation m => m.Source.Width >= 8 && m.Source.Height >= 8,
        CalloutAnnotation c => c.Box.Width >= 24 && c.Box.Height >= 16,
        _ => a.Bounds.Width >= 4 && a.Bounds.Height >= 4,
    };

    private int? TopmostAt(Point p)
    {
        var items = _history.Current.Items;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].HitTest(p))
                return i;
        }
        return null;
    }

    private int? HandleAt(Annotation item, Point p)
    {
        double radius = 8 / Math.Max(_zoom, 0.05);
        var handles = item.Handles;
        for (int i = handles.Count - 1; i >= 0; i--)
        {
            if (Math.Abs(handles[i].X - p.X) <= radius && Math.Abs(handles[i].Y - p.Y) <= radius)
                return i;
        }
        return null;
    }

    private void UpdateHoverCursor(Point p)
    {
        var items = _history.Current.Items;
        Cursor? cursor = null;
        if (_selected is { } index && index < items.Count)
        {
            if (HandleAt(items[index], p) is { } handle)
                cursor = HandleCursor(items[index], handle);
            else if (items[index].HitTest(p))
                cursor = Cursors.SizeAll;
        }
        if (cursor == null && _tool is EditorTool.Select or EditorTool.Image && TopmostAt(p) != null)
            cursor = Cursors.SizeAll;
        InputLayer.Cursor = cursor ?? DefaultCursor();
    }

    private static Cursor HandleCursor(Annotation item, int handle)
    {
        if (item is ArrowAnnotation or LineAnnotation or BraceAnnotation || (item is CalloutAnnotation && handle == 8))
            return Cursors.Hand;
        return (handle % 8) switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            _ => Cursors.SizeWE,
        };
    }

    private Point ClampToImage(Point p) =>
        new(Math.Clamp(p.X, 0, Image.PixelWidth), Math.Clamp(p.Y, 0, Image.PixelHeight));

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

    // ───────────────────────── Texto e balão ─────────────────────────

    private void EditText(int index)
    {
        var item = _history.Current.Items[index];
        _selected = index;
        OpenTextEditor(item, index);
    }

    /// <summary>Abre a caixa de digitação sobre a imagem. <paramref name="index"/> = item existente que será substituído.</summary>
    private void OpenTextEditor(Annotation template, int? index)
    {
        _editingTemplate = template;
        _editingIndex = index;

        var editor = new TextBox
        {
            AcceptsReturn = false,
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(0),
        };

        Point origin;
        switch (template)
        {
            case CalloutAnnotation c:
                double padding = Math.Max(8, c.FontSize * 0.45);
                origin = new Point(c.Box.X + padding, c.Box.Y + padding * 0.7);
                editor.Width = Math.Max(40, c.Box.Width - padding * 2);
                editor.TextWrapping = TextWrapping.Wrap;
                editor.Text = c.Text;
                editor.FontSize = c.FontSize;
                editor.Foreground = new SolidColorBrush(Geometry2D.ContrastText(c.Color));
                editor.Background = Brushes.Transparent;
                break;
            case TextAnnotation t:
                origin = t.Origin;
                editor.MinWidth = 80;
                editor.Text = t.Text;
                editor.FontSize = t.FontSize;
                editor.Foreground = new SolidColorBrush(t.Color);
                editor.Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0));
                break;
            default:
                return;
        }
        editor.CaretBrush = editor.Foreground;
        editor.PreviewKeyDown += TextEditor_PreviewKeyDown;
        Canvas.SetLeft(editor, origin.X);
        Canvas.SetTop(editor, origin.Y);
        ContentCanvas.Children.Add(editor);
        _textEditor = editor;
        Render();
        editor.Focus();
        editor.CaretIndex = editor.Text.Length;
    }

    private void TextEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var editor = (TextBox)sender;
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int caret = editor.CaretIndex;
            editor.Text = editor.Text.Insert(caret, "\n");
            editor.CaretIndex = caret + 1;
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
            Render();
            e.Handled = true;
        }
    }

    private void CommitText()
    {
        if (_textEditor == null)
            return;
        string text = _textEditor.Text.TrimEnd();
        double fontSize = _textEditor.FontSize;
        var template = _editingTemplate;
        int? index = _editingIndex;
        CloseTextEditor();

        var items = _history.Current.Items;
        Annotation? updated = template switch
        {
            CalloutAnnotation c => c with { Text = text },
            TextAnnotation t when text.Length > 0 => t with { Text = text, FontSize = fontSize },
            _ => null,
        };

        if (index is { } i && i < items.Count)
        {
            Commit(_history.Current with { Items = updated == null ? items.RemoveAt(i) : items.SetItem(i, updated) });
            _selected = updated == null ? null : i;
        }
        else if (updated != null)
        {
            Commit(_history.Current with { Items = items.Add(updated) });
            _selected = items.Count;
        }
        Render();
    }

    private void CloseTextEditor()
    {
        if (_textEditor == null)
            return;
        ContentCanvas.Children.Remove(_textEditor);
        _textEditor = null;
        _editingIndex = null;
        _editingTemplate = null;
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

        if (key == Key.F4 && mods == ModifierKeys.None)
        {
            ToggleProperties();
            e.Handled = true;
            return;
        }

        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when shift: Redo(); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.C: CopyResult(); break;
                case Key.V: PasteImage(); break;
                case Key.P: PrintResult(); break;
                case Key.Enter: CopyAndClose(); break;
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
            Key.K => EditorTool.Callout,
            Key.N => EditorTool.Step,
            Key.S => EditorTool.Spotlight,
            Key.M => EditorTool.Magnifier,
            Key.B => EditorTool.Pixelate,
            Key.J => EditorTool.Stamp,
            Key.G => EditorTool.Brace,
            Key.I => EditorTool.Image,
            Key.C => EditorTool.Crop,
            _ => null,
        };
        if (tool is { } t)
        {
            SetTool(t);
            e.Handled = true;
            return;
        }

        var items = _history.Current.Items;
        if (_selected is { } index && index < items.Count)
        {
            Vector? nudge = key switch
            {
                Key.Left => new Vector(-1, 0),
                Key.Right => new Vector(1, 0),
                Key.Up => new Vector(0, -1),
                Key.Down => new Vector(0, 1),
                _ => null,
            };
            if (nudge is { } n)
            {
                Commit(_history.Current with { Items = items.SetItem(index, items[index].Offset(n * (shift ? 10 : 1))) });
                e.Handled = true;
                return;
            }
            if (key is Key.Delete or Key.Back)
            {
                _selected = null;
                Commit(_history.Current with { Items = items.RemoveAt(index) });
                e.Handled = true;
                return;
            }
            if (key == Key.Enter && items[index] is TextAnnotation or CalloutAnnotation)
            {
                EditText(index);
                e.Handled = true;
                return;
            }
        }

        if (key == Key.Escape)
        {
            if (_selected != null)
                Select(null);
            else
                Close();
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
        var size = ImageSize;
        if (_history.Undo())
        {
            _selected = null;
            _dirty = true;
            Render();
            if (ImageSize != size)
                FitToWindow();
        }
    }

    private void Redo()
    {
        var size = ImageSize;
        if (_history.Redo())
        {
            _selected = null;
            _dirty = true;
            Render();
            if (ImageSize != size)
                FitToWindow();
        }
    }

    /// <summary>A lista que aparece agora: o histórico mais o gesto em andamento, sem o item que está sendo digitado.</summary>
    private IReadOnlyList<Annotation> DisplayItems()
    {
        var items = _history.Current.Items;
        if (_draft != null)
        {
            items = _gesture == Gesture.Draw ? items.Add(_draft)
                : _gestureIndex >= 0 && _gestureIndex < items.Count ? items.SetItem(_gestureIndex, _draft)
                : items;
        }
        if (_editingIndex is { } editing && editing < items.Count)
        {
            // O balão continua visível (o texto é digitado por cima dele); o texto solto some enquanto é editado.
            if (items[editing] is CalloutAnnotation callout)
                items = items.SetItem(editing, callout with { Text = "" });
            else
                items = items.RemoveAt(editing);
        }
        return items;
    }

    private void Render()
    {
        var state = _history.Current;
        var image = state.Image;
        var display = DisplayItems();

        if (!ReferenceEquals(BaseImage.Source, image))
        {
            BaseImage.Source = image;
            BaseImage.Width = image.PixelWidth;
            BaseImage.Height = image.PixelHeight;
            foreach (var layer in new[] { AnnotationLayer, OverlayLayer, InputLayer })
            {
                layer.Width = image.PixelWidth;
                layer.Height = image.PixelHeight;
            }
            ContentCanvas.Width = Surface.Width = image.PixelWidth;
            ContentCanvas.Height = Surface.Height = image.PixelHeight;
        }

        AnnotationLayer.Children.Clear();
        foreach (var element in AnnotationRenderer.Scene(display, image))
            AnnotationLayer.Children.Add(element);

        RenderEffects(state.Effects, ImageSize);
        RenderOverlay(display);

        UndoButton.IsEnabled = _history.CanUndo;
        string name = _filePath != null ? System.IO.Path.GetFileName(_filePath) : "nova captura";
        Title = $"Editor de imagens do Capturo · {(_dirty ? "• " : "")}{name}";
        SizeText.Text = $"{image.PixelWidth} x {image.PixelHeight}";
    }

    private void RenderEffects(ImageEffects effects, Size size)
    {
        var outline = effects.Outline(size);
        Surface.Clip = effects.RoundedCorners || effects.Torn != TornEdges.None ? outline : null;
        BorderHost.Children.Clear();
        if (effects.Border)
            BorderHost.Children.Add(effects.BorderShape(outline));
        ShadowHost.Children.Clear();
        if (effects.Shadow)
            ShadowHost.Children.Add(ImageEffects.ShadowShape(outline, 0));
    }

    private void RenderOverlay(IReadOnlyList<Annotation> display)
    {
        OverlayLayer.Children.Clear();
        double unit = 1 / Math.Max(_zoom, 0.05);

        if (_areaDraft is { } area)
        {
            var rect = new Rect(area.X, area.Y, area.Width, area.Height);
            if (_gesture == Gesture.CutOut)
            {
                // A faixa que vai sumir fica vermelha.
                OverlayLayer.Children.Add(Positioned(new Rectangle
                {
                    Width = Math.Max(1, rect.Width), Height = Math.Max(1, rect.Height),
                    Fill = new SolidColorBrush(Color.FromArgb(0x66, 0xEF, 0x44, 0x44)),
                    Stroke = Brushes.Red, StrokeThickness = unit, StrokeDashArray = new DoubleCollection { 4, 3 },
                }, rect.X, rect.Y));
            }
            else
            {
                OverlayLayer.Children.Add(new Path
                {
                    Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                    Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(ImageSize)), new RectangleGeometry(rect)),
                });
                OverlayLayer.Children.Add(DashedBox(rect, unit));
            }
        }

        if (_selected is not { } index || index >= display.Count || _textEditor != null && _editingTemplate is TextAnnotation)
            return;
        var item = display[index];
        OverlayLayer.Children.Add(DashedBox(item.Bounds, unit));

        var handles = item.Handles;
        double size = 9 * unit;
        for (int i = 0; i < handles.Count; i++)
        {
            bool round = item is ArrowAnnotation or LineAnnotation or BraceAnnotation || (item is CalloutAnnotation && i == 8);
            Shape handle = round ? new Ellipse() : new Rectangle();
            handle.Width = handle.Height = size;
            handle.Fill = Brushes.White;
            handle.Stroke = AccentBrush;
            handle.StrokeThickness = 1.5 * unit;
            OverlayLayer.Children.Add(Positioned(handle, handles[i].X - size / 2, handles[i].Y - size / 2));
        }
    }

    private static T Positioned<T>(T element, double x, double y) where T : UIElement
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        return element;
    }

    private static Rectangle DashedBox(Rect rect, double unit) => Positioned(new Rectangle
    {
        Width = Math.Max(1, rect.Width),
        Height = Math.Max(1, rect.Height),
        Stroke = AccentBrush,
        StrokeThickness = 1.25 * unit,
        StrokeDashArray = new DoubleCollection { 4, 3 },
    }, rect.X, rect.Y);

    private static string ToolName(EditorTool tool) => tool switch
    {
        EditorTool.Select => "Selecionar",
        EditorTool.Arrow => "Seta",
        EditorTool.Rectangle => "Retângulo",
        EditorTool.Ellipse => "Elipse",
        EditorTool.Line => "Linha",
        EditorTool.Pen => "Lápis",
        EditorTool.Highlighter => "Marca-texto",
        EditorTool.Text => "Texto",
        EditorTool.Callout => "Balão",
        EditorTool.Step => "Numeração",
        EditorTool.Spotlight => "Destaque",
        EditorTool.Magnifier => "Lupa",
        EditorTool.Pixelate => "Desfocar",
        EditorTool.Stamp => "Carimbo",
        EditorTool.Image => "Imagem",
        EditorTool.Brace => "Chave",
        EditorTool.Crop => "Cortar",
        EditorTool.CutOut => "Recortar",
        _ => tool.ToString(),
    };

    private string ToolHint(EditorTool tool) => tool switch
    {
        EditorTool.Select => "Clique para selecionar, arraste para mover; as alças redimensionam. Setas do teclado ajustam; Delete apaga.",
        EditorTool.Arrow => "Arraste do início até a ponta; depois ajuste pelas alças.",
        EditorTool.Text => "Clique onde o texto começa. Duplo clique num texto edita.",
        EditorTool.Callout => "Arraste a caixa do balão e digite. A alça redonda move a ponta.",
        EditorTool.Step => "Cada clique adiciona o próximo número.",
        EditorTool.Spotlight => "Arraste a área que fica clara; a opacidade controla o escuro em volta.",
        EditorTool.Magnifier => "Arraste a área a ampliar; a lente aparece ao lado e pode ser movida.",
        EditorTool.Pixelate => "Arraste sobre o que deve ficar ilegível.",
        EditorTool.Stamp => "Escolha o símbolo abaixo e clique para carimbar.",
        EditorTool.Image => "Escolha um arquivo, ou cole uma imagem com Ctrl+V.",
        EditorTool.Brace => "Arraste de uma ponta à outra da chave.",
        EditorTool.Crop => "Arraste a área que deve ficar. Ctrl+Z desfaz.",
        EditorTool.CutOut => _cutHorizontal ? "Arraste de cima para baixo a faixa que deve sumir." : "Arraste da esquerda para a direita a faixa que deve sumir.",
        _ => "Arraste para desenhar; Shift restringe a forma. O item fica editável pelas alças.",
    };

    // ───────────────────────── Zoom ─────────────────────────

    /// <summary>Zoom 1 = um pixel da captura num pixel físico da tela, em qualquer escala do Windows.</summary>
    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.05, 8);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = _zoom / dpi;
        ZoomText.Text = $"{_zoom * 100:0}%";
        _syncingControls = true;
        ZoomSlider.Value = Math.Clamp(_zoom * 100, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _syncingControls = false;
        RenderOptions.SetBitmapScalingMode(BaseImage, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        RenderOverlay(DisplayItems());
    }

    private void FitToWindow()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double availableWidth = Math.Max(100, Scroller.ActualWidth - 100) * dpi;
        double availableHeight = Math.Max(100, Scroller.ActualHeight - 100) * dpi;
        SetZoom(Math.Min(1, Math.Min(availableWidth / Image.PixelWidth, availableHeight / Image.PixelHeight)));
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        SetZoom(e.Delta > 0 ? _zoom * 1.15 : _zoom / 1.15);
        e.Handled = true;
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncingControls && IsLoaded)
            SetZoom(e.NewValue / 100);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);

    // ───────────────────────── Exportação ─────────────────────────

    /// <summary>Imagem final: captura + anotações e os efeitos (borda, sombra, cantos, rasgo).</summary>
    public BitmapSource RenderResult()
    {
        CommitText();
        var state = _history.Current;
        int width = state.Image.PixelWidth, height = state.Image.PixelHeight;
        var canvas = new Canvas { Width = width, Height = height };
        canvas.Children.Add(new Image { Source = state.Image, Width = width, Height = height, Stretch = Stretch.Fill });
        foreach (var element in AnnotationRenderer.Scene(state.Items, state.Image))
            canvas.Children.Add(element);
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        bitmap.Freeze();
        return state.Effects.Apply(bitmap);
    }

    private void CopyResult()
    {
        if (OutputService.CopyToClipboard(RenderResult()))
            Flash("Copiado para a área de transferência");
    }

    /// <summary>Caminho do arquivo com as alterações gravadas (salva antes, se preciso).</summary>
    private string? EnsureSaved() => (_filePath == null || _dirty) && !Save() ? null : _filePath;

    private bool Save()
    {
        if (_filePath == null)
            return SaveAs();
        return SaveTo(_filePath, RenderResult());
    }

    private bool SaveTo(string path, BitmapSource image)
    {
        try
        {
            OutputService.Save(image, path);
            _dirty = false;
            Render();
            Flash($"Salvo em {path}");
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

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>Barra de título escura (Windows 10 2004+ / 11), como o resto do editor.</summary>
internal static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        int dark = 1;
        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int caption = 0x003F3C3C; // COLORREF 0x00BBGGRR: #3C3C3F
        DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
    }
}
