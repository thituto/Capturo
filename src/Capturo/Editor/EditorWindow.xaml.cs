using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        Color.FromRgb(0x9C, 0xA3, 0xAF), // cinza
        Color.FromRgb(0xFF, 0xFF, 0xFF), // branco
    };

    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));

    private enum Gesture { None, Draw, Move, Resize, Crop }

    private readonly BitmapSource _source;
    private readonly AppSettings _settings;
    private readonly EditorHistory _history;
    private string? _filePath;
    private bool _dirty;

    private EditorTool _tool = EditorTool.Arrow;
    private Color _color = Palette[0];
    private double _size = 4;
    private ShapeStyle _style = ShapeStyle.Default;
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
    private Int32Rect? _cropDraft;

    // Edição de texto (texto solto ou balão)
    private TextBox? _textEditor;
    private int? _editingIndex;
    private Annotation? _editingTemplate;

    public EditorWindow(BitmapSource source, string? filePath, AppSettings settings)
    {
        InitializeComponent();
        _source = source;
        _filePath = filePath;
        _settings = settings;
        _history = new EditorHistory(EditorState.Empty with { Effects = LoadEffects(settings) });

        BaseImage.Source = source;
        BaseImage.Width = source.PixelWidth;
        BaseImage.Height = source.PixelHeight;
        foreach (var layer in new[] { AnnotationLayer, OverlayLayer, InputLayer })
        {
            layer.Width = source.PixelWidth;
            layer.Height = source.PixelHeight;
        }

        BuildToolbar();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => FitToWindow();
        Closing += OnClosing;
        Render();
    }

    private Size ImageSize => new(_source.PixelWidth, _source.PixelHeight);

    // ───────────────────────── Barra de ferramentas ─────────────────────────

    private void BuildToolbar()
    {
        foreach (var button in ToolPanel.Children.OfType<ToggleButton>())
            button.Click += (s, _) => SetTool(Enum.Parse<EditorTool>((string)((FrameworkElement)s).Tag));

        foreach (var color in Palette)
        {
            var swatch = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Background = new SolidColorBrush(color),
                Tag = color,
                ToolTip = BitmapUtil.ToHex(color),
            };
            swatch.Click += (s, _) => SetColor((Color)((FrameworkElement)s).Tag);
            ColorPanel.Children.Add(swatch);
        }

        SizeValue.Text = _size.ToString("0");
        OpacityValue.Text = "100%";
        SetTool(_tool);
        SetColor(_color);
    }

    private void SetTool(EditorTool tool)
    {
        CommitText();
        _tool = tool;
        foreach (var button in ToolPanel.Children.OfType<ToggleButton>())
            button.IsChecked = (string)button.Tag == tool.ToString();
        InputLayer.Cursor = tool switch
        {
            EditorTool.Select => Cursors.Arrow,
            EditorTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
        if (tool == EditorTool.Spotlight)
            SetOpacityControl(0.6);
        Render();
    }

    private void SetColor(Color color)
    {
        _color = color;
        foreach (var swatch in ColorPanel.Children.OfType<Button>())
            swatch.BorderBrush = (Color)swatch.Tag == color ? Brushes.White : Brushes.Transparent;
        if (_textEditor != null)
        {
            _textEditor.Foreground = new SolidColorBrush(_editingTemplate is CalloutAnnotation ? Geometry2D.ContrastText(color) : color);
        }
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

    /// <summary>Ao selecionar um item, os controles passam a mostrar o estilo dele.</summary>
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

    // ───────────────────────── Efeitos da imagem ─────────────────────────

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

    private void Effects_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingControls)
            return;
        ApplyEffects(_history.Current.Effects with
        {
            Border = BorderToggle.IsChecked == true,
            Shadow = ShadowToggle.IsChecked == true,
            RoundedCorners = CornersToggle.IsChecked == true,
        });
    }

    private void BorderColorButton_Click(object sender, RoutedEventArgs e)
    {
        var panel = new WrapPanel { MaxWidth = 150 };
        Popup? popup = null;
        foreach (var color in Palette)
        {
            var swatch = new Button { Style = (Style)FindResource("SwatchButton"), Background = new SolidColorBrush(color), Margin = new Thickness(3) };
            swatch.Click += (_, _) =>
            {
                popup!.IsOpen = false;
                ApplyEffects(_history.Current.Effects with { Border = true, BorderColor = color });
            };
            panel.Children.Add(swatch);
        }
        popup = ShowPopup(BorderColorButton, panel);
    }

    private void BorderWidthButton_Click(object sender, RoutedEventArgs e)
    {
        ShowChoices(BorderWidthButton, new[] { 1, 2, 3, 4, 6, 8, 12 }.Select(w =>
            ($"{w} px", (Action)(() => ApplyEffects(_history.Current.Effects with { Border = true, BorderWidth = w })))));
    }

    private void TornButton_Click(object sender, RoutedEventArgs e)
    {
        ShowChoices(TornButton, new (string, Action)[]
        {
            ("Sem borda rasgada", () => ApplyEffects(_history.Current.Effects with { Torn = TornEdges.None })),
            ("Rasgada embaixo", () => ApplyEffects(_history.Current.Effects with { Torn = TornEdges.Bottom })),
            ("Rasgada embaixo e à direita", () => ApplyEffects(_history.Current.Effects with { Torn = TornEdges.BottomRight })),
            ("Rasgada em todos os lados", () => ApplyEffects(_history.Current.Effects with { Torn = TornEdges.All })),
        });
    }

    private void SyncEffectControls(ImageEffects effects)
    {
        _syncingControls = true;
        BorderToggle.IsChecked = effects.Border;
        ShadowToggle.IsChecked = effects.Shadow;
        CornersToggle.IsChecked = effects.RoundedCorners;
        BorderColorSwatch.Fill = new SolidColorBrush(effects.BorderColor);
        BorderWidthButton.Content = $"{effects.BorderWidth:0} px ▾";
        TornButton.Content = effects.Torn switch
        {
            TornEdges.None => "≈ Rasgado ▾",
            TornEdges.Bottom => "≈ Embaixo ▾",
            TornEdges.BottomRight => "≈ Embaixo+dir. ▾",
            _ => "≈ Todos os lados ▾",
        };
        _syncingControls = false;
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
                Background = (Brush)FindResource("PanelBackground"),
                BorderBrush = (Brush)FindResource("PanelBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = content,
            },
        };
        popup.IsOpen = true;
        return popup;
    }

    private void ShowChoices(FrameworkElement anchor, IEnumerable<(string Label, Action Action)> choices)
    {
        var panel = new StackPanel();
        Popup? popup = null;
        foreach (var (label, action) in choices)
        {
            var button = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => { popup!.IsOpen = false; action(); };
            panel.Children.Add(button);
        }
        popup = ShowPopup(anchor, panel);
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

            case EditorTool.Text:
                if (TopmostAt(p) is { } existing && items[existing] is TextAnnotation)
                    EditText(existing);
                else
                    OpenTextEditor(new TextAnnotation(p, "", _color, SizeMapping.FontSize(_size), _style.Filled), null);
                break;

            case EditorTool.Step:
                var step = new StepAnnotation(p, _history.Current.NextStepNumber, _color, SizeMapping.StepDiameter(_size));
                Commit(_history.Current with { Items = items.Add(step) });
                Select(items.Count);
                break;

            case EditorTool.Select:
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
        _cropDraft = null;
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
                _cropDraft = AnnotationRenderer.ToPixelArea(new Rect(_downPoint, shift ? SquareCorner(_downPoint, p) : p), _source);
                break;
        }
        Render();
    }

    private Annotation? CreateDraft(Point p, bool shift)
    {
        var rect = new Rect(_downPoint, shift ? SquareCorner(_downPoint, p) : p);
        var end = shift ? SnapAngle(_downPoint, p) : p;
        switch (_tool)
        {
            case EditorTool.Arrow: return new ArrowAnnotation(_downPoint, end, _color, _size);
            case EditorTool.Line: return new LineAnnotation(_downPoint, end, _color, _size);
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
        var crop = _cropDraft;
        int index = _gestureIndex;
        var original = _gestureOriginal;
        _gesture = Gesture.None;
        _draft = null;
        _cropDraft = null;
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
            case Gesture.Crop when crop is { Width: >= 4, Height: >= 4 } area:
                Commit(state with { Crop = area });
                Dispatcher.BeginInvoke(FitToWindow, System.Windows.Threading.DispatcherPriority.Loaded);
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
        if (cursor == null && _tool == EditorTool.Select && TopmostAt(p) != null)
            cursor = Cursors.SizeAll;
        InputLayer.Cursor = cursor ?? (_tool switch
        {
            EditorTool.Select => Cursors.Arrow,
            EditorTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        });
    }

    private static Cursor HandleCursor(Annotation item, int handle)
    {
        if (item is ArrowAnnotation or LineAnnotation || (item is CalloutAnnotation && handle == 8))
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
            Key.K => EditorTool.Callout,
            Key.N => EditorTool.Step,
            Key.S => EditorTool.Spotlight,
            Key.M => EditorTool.Magnifier,
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
        var display = DisplayItems();

        AnnotationLayer.Children.Clear();
        foreach (var element in AnnotationRenderer.Scene(display, _source))
            AnnotationLayer.Children.Add(element);

        var crop = state.Crop ?? new Int32Rect(0, 0, _source.PixelWidth, _source.PixelHeight);
        Surface.Width = crop.Width;
        Surface.Height = crop.Height;
        ContentCanvas.Width = _source.PixelWidth;
        ContentCanvas.Height = _source.PixelHeight;
        CropShift.X = -crop.X;
        CropShift.Y = -crop.Y;

        RenderEffects(state.Effects, new Size(crop.Width, crop.Height));
        RenderOverlay(display);

        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        string name = _filePath != null ? System.IO.Path.GetFileName(_filePath) : "nova captura";
        Title = $"{(_dirty ? "• " : "")}{name} — Capturo";
        StatusText.Text = $"{crop.Width} × {crop.Height} px   ·   {ToolHint(_tool)}";
    }

    private void RenderEffects(ImageEffects effects, Size size)
    {
        SyncEffectControls(effects);
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

        if (_cropDraft is { } crop)
        {
            OverlayLayer.Children.Add(new Path
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(ImageSize)),
                    new RectangleGeometry(new Rect(crop.X, crop.Y, crop.Width, crop.Height))),
            });
            OverlayLayer.Children.Add(DashedBox(new Rect(crop.X, crop.Y, crop.Width, crop.Height), unit));
        }

        if (_selected is not { } index || index >= display.Count || _textEditor != null && _editingTemplate is TextAnnotation)
            return;
        var item = display[index];
        OverlayLayer.Children.Add(DashedBox(item.Bounds, unit));

        var handles = item.Handles;
        double size = 9 * unit;
        for (int i = 0; i < handles.Count; i++)
        {
            bool round = item is ArrowAnnotation or LineAnnotation || (item is CalloutAnnotation && i == 8);
            Shape handle = round ? new Ellipse() : new Rectangle();
            handle.Width = handle.Height = size;
            handle.Fill = Brushes.White;
            handle.Stroke = AccentBrush;
            handle.StrokeThickness = 1.5 * unit;
            Canvas.SetLeft(handle, handles[i].X - size / 2);
            Canvas.SetTop(handle, handles[i].Y - size / 2);
            OverlayLayer.Children.Add(handle);
        }
    }

    private static Rectangle DashedBox(Rect rect, double unit)
    {
        var box = new Rectangle
        {
            Width = Math.Max(1, rect.Width),
            Height = Math.Max(1, rect.Height),
            Stroke = AccentBrush,
            StrokeThickness = 1.25 * unit,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        Canvas.SetLeft(box, rect.X);
        Canvas.SetTop(box, rect.Y);
        return box;
    }

    private static string ToolHint(EditorTool tool) => tool switch
    {
        EditorTool.Select => "Clique para selecionar · arraste para mover · alças redimensionam · setas do teclado ajustam · Delete apaga",
        EditorTool.Arrow => "Arraste do início até a ponta · depois ajuste pelas alças",
        EditorTool.Text => "Clique onde o texto começa · duplo clique num texto edita",
        EditorTool.Callout => "Arraste a caixa do balão e digite · a alça redonda move a ponta",
        EditorTool.Step => "Cada clique adiciona o próximo número",
        EditorTool.Spotlight => "Arraste a área que fica clara · a opacidade controla o escuro em volta",
        EditorTool.Magnifier => "Arraste a área a ampliar · a lente aparece ao lado e pode ser movida e redimensionada",
        EditorTool.Pixelate => "Arraste sobre o que deve ficar ilegível",
        EditorTool.Crop => "Arraste a área que deve ficar · Ctrl+Z desfaz o recorte",
        _ => "Arraste para desenhar · Shift restringe a forma · o item desenhado fica editável pelas alças",
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
        RenderOverlay(DisplayItems());
    }

    private void FitToWindow()
    {
        var crop = _history.Current.Crop ?? new Int32Rect(0, 0, _source.PixelWidth, _source.PixelHeight);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double availableWidth = Math.Max(100, Scroller.ActualWidth - 100) * dpi;
        double availableHeight = Math.Max(100, Scroller.ActualHeight - 100) * dpi;
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

    /// <summary>Imagem final: captura + anotações, recorte e efeitos (borda, sombra, cantos, rasgo).</summary>
    public BitmapSource RenderResult()
    {
        CommitText();
        var state = _history.Current;
        int width = _source.PixelWidth, height = _source.PixelHeight;
        var canvas = new Canvas { Width = width, Height = height };
        canvas.Children.Add(new Image { Source = _source, Width = width, Height = height, Stretch = Stretch.Fill });
        foreach (var element in AnnotationRenderer.Scene(state.Items, _source))
            canvas.Children.Add(element);
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        bitmap.Freeze();
        BitmapSource result = state.Crop is { } crop ? BitmapUtil.Crop(bitmap, crop) : bitmap;
        return state.Effects.Apply(result);
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

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
