using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Capturo.Services;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<IReadOnlyList<HotkeyAction>> _applyHotkeys;
    private readonly Dictionary<HotkeyAction, HotkeyBox> _boxes = new();
    private readonly Dictionary<HotkeyAction, TextBlock> _warnings = new();

    /// <param name="applyHotkeys">Registra os atalhos salvos e devolve os que outro programa já usa.</param>
    public SettingsWindow(AppSettings settings, Func<IReadOnlyList<HotkeyAction>> applyHotkeys)
    {
        InitializeComponent();
        _settings = settings;
        _applyHotkeys = applyHotkeys;
        BuildHotkeyRows();
        LoadValues();
    }

    private void BuildHotkeyRows()
    {
        var textBoxStyle = (Style)FindResource(typeof(TextBox));
        int row = 0;
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            HotkeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = Hotkey.Describe(action), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            Grid.SetRow(label, row);
            HotkeyGrid.Children.Add(label);

            var box = new HotkeyBox { Style = textBoxStyle, Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(6, 4, 6, 4) };
            box.HotkeyChanged += (_, _) => _warnings[action].Text = "";
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            HotkeyGrid.Children.Add(box);
            _boxes[action] = box;

            var warning = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                FontSize = 12,
            };
            Grid.SetRow(warning, row);
            Grid.SetColumn(warning, 2);
            HotkeyGrid.Children.Add(warning);
            _warnings[action] = warning;
            row++;
        }
    }

    private void LoadValues()
    {
        foreach (var (action, box) in _boxes)
            box.Hotkey = _settings.GetHotkey(action);
        CopyBox.IsChecked = _settings.CopyOnCapture;
        AutoSaveBox.IsChecked = _settings.AutoSave;
        EditorBox.IsChecked = _settings.OpenEditor;
        FolderBox.Text = _settings.SaveFolder;
        DelaySlider.Value = _settings.DelaySeconds;
        StartupBox.IsChecked = StartupRegistration.IsEnabled;
        FpsSlider.Value = _settings.VideoFramesPerSecond;
        CursorBox.IsChecked = _settings.VideoCaptureCursor;
        ClicksBox.IsChecked = _settings.VideoHighlightClicks;
    }

    private void FpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FpsValue != null)
            FpsValue.Text = $"{e.NewValue:0} fps";
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (action, box) in _boxes)
            box.Hotkey = Hotkey.Defaults.GetValueOrDefault(action, Hotkey.None);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null };
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void DelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DelayValue != null)
            DelayValue.Text = $"{e.NewValue:0} s";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Duas ações com o mesmo atalho: só a primeira funcionaria.
        var duplicates = _boxes.Where(b => !b.Value.Hotkey.IsNone)
            .GroupBy(b => b.Value.Hotkey)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Skip(1))
            .ToList();
        foreach (var (action, _) in duplicates)
            _warnings[action].Text = "repetido";
        if (duplicates.Count > 0)
            return;

        foreach (var (action, box) in _boxes)
            _settings.SetHotkey(action, box.Hotkey);
        _settings.CopyOnCapture = CopyBox.IsChecked == true;
        _settings.AutoSave = AutoSaveBox.IsChecked == true;
        _settings.OpenEditor = EditorBox.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(FolderBox.Text))
            _settings.SaveFolder = FolderBox.Text.Trim();
        _settings.DelaySeconds = (int)DelaySlider.Value;
        _settings.VideoFramesPerSecond = (int)FpsSlider.Value;
        _settings.VideoCaptureCursor = CursorBox.IsChecked == true;
        _settings.VideoHighlightClicks = ClicksBox.IsChecked == true;
        _settings.Save();

        try
        {
            StartupRegistration.Set(StartupBox.IsChecked == true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("Não foi possível alterar o início automático.", ex);
        }

        var busy = _applyHotkeys();
        if (busy.Count == 0)
        {
            DialogResult = true;
            return;
        }
        foreach (var action in busy)
            _warnings[action].Text = "em uso por outro programa";
        MessageBox.Show(this,
            "Alguns atalhos já são usados pelo Windows ou por outro programa e não foram ativados. Escolha outra combinação para eles.",
            "Capturo", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
