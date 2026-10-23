using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Capturo.Capture;
using Capturo.Editor;
using Capturo.Services;
using static Capturo.Native.NativeMethods;

namespace Capturo;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private bool _capturing;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "Capturo.SingleInstance.v1", out bool firstInstance);
        if (!firstInstance)
        {
            // Já existe um Capturo na bandeja; esta instância só sai.
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _settings = AppSettings.Load();
        _tray = new TrayIcon(this, _settings);
        _hotkeys = new HotkeyManager();

        var busy = new List<string>();
        void Bind(uint modifiers, uint key, string label, CaptureMode mode)
        {
            if (!_hotkeys.Register(modifiers, key, () => StartCapture(mode)))
                busy.Add(label);
        }
        Bind(0, VK_SNAPSHOT, "Print", CaptureMode.Region);
        Bind(MOD_SHIFT, VK_SNAPSHOT, "Shift+Print", CaptureMode.CurrentMonitor);
        Bind(MOD_CONTROL, VK_SNAPSHOT, "Ctrl+Print", CaptureMode.AllMonitors);
        Bind(MOD_CONTROL | MOD_ALT, 'R', "Ctrl+Alt+R", CaptureMode.Region);

        if (busy.Count > 0)
        {
            _tray.Notify($"Atalhos em uso por outro programa: {string.Join(", ", busy)}. " +
                         "Se o Print abrir a Ferramenta de Captura, desative isso em Configurações › Acessibilidade › Teclado.", error: true);
        }
        else if (!_settings.FirstRunTipShown)
        {
            _tray.Notify("Capturo está na bandeja. Print captura uma região; clique no ícone para o menu.");
            _settings.FirstRunTipShown = true;
            _settings.Save();
        }

        if (e.Args.Contains("--capture"))
            Dispatcher.BeginInvoke(() => StartCapture(CaptureMode.Region), DispatcherPriority.ApplicationIdle);
    }

    public async void StartCapture(CaptureMode mode, bool fromMenu = false)
    {
        if (_capturing)
            return;
        _capturing = true;
        try
        {
            // Dá tempo do menu da bandeja sumir antes de congelar a tela.
            if (fromMenu)
                await Task.Delay(250);

            var result = await CaptureSession.CaptureAsync(mode);
            if (result.PickedColor is { } color)
                OnColorPicked(color);
            else if (result.Image is { } image)
                OnCaptured(image);
        }
        catch (Exception ex)
        {
            Log.Error("Falha na captura.", ex);
            _tray?.Notify($"A captura falhou: {ex.Message}", error: true);
        }
        finally
        {
            _capturing = false;
        }
    }

    private void OnCaptured(System.Windows.Media.Imaging.BitmapSource image)
    {
        string? path = null;
        if (_settings.AutoSave)
        {
            try
            {
                path = OutputService.SaveNew(image, _settings);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.Error("Falha ao salvar a captura automaticamente.", ex);
                _tray?.Notify("Não foi possível salvar a captura automaticamente.", error: true);
            }
        }

        if (_settings.CopyOnCapture)
            OutputService.CopyToClipboard(image);

        if (_settings.OpenEditor)
        {
            var editor = new EditorWindow(image, path, _settings);
            editor.Show();
            editor.Activate();
        }
        else
        {
            _tray?.Notify(path != null ? $"Captura salva: {System.IO.Path.GetFileName(path)}" : "Captura copiada.");
        }
    }

    private void OnColorPicked(Color color)
    {
        string hex = BitmapUtil.ToHex(color);
        if (OutputService.CopyText(hex))
            _tray?.Notify($"Cor {hex} copiada (rgb {color.R}, {color.G}, {color.B}).");
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erro não tratado.", e.Exception);
        _tray?.Notify($"Erro inesperado: {e.Exception.Message}", error: true);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        if (_singleInstance != null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
