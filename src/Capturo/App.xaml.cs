using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Capturo.Capture;
using Capturo.Editor;
using Capturo.Services;
using Capturo.Video;
using static Capturo.Native.NativeMethods;

namespace Capturo;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private bool _busy;
    private SettingsWindow? _settingsWindow;

    // Gravação em andamento
    private VideoRecorder? _recorder;
    private RecordingFrame? _recordingFrame;
    private RecordingToolbar? _recordingToolbar;

    protected override void OnStartup(StartupEventArgs e)
    {
        string? command = CommandChannel.Parse(e.Args);
        _singleInstance = new Mutex(true, "Capturo.SingleInstance.v1", out bool firstInstance);
        if (!firstInstance)
        {
            // Já existe um Capturo na bandeja: repassa o comando (se houver) para ele e sai.
            if (command != null)
                CommandChannel.Send(command);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Erro fatal fora da interface.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Erro em tarefa de fundo.", args.Exception); args.SetObserved(); };

        _settings = AppSettings.Load();
        _tray = new TrayIcon(this, _settings);
        _hotkeys = new HotkeyManager();

        var busy = ApplyHotkeys();
        if (busy.Count > 0)
        {
            _tray.Notify("Alguns atalhos já são usados por outro programa: " +
                         string.Join(", ", busy.Select(a => _settings.GetHotkey(a).ToString())) +
                         ". Troque-os em Configurações.", error: true);
        }
        else if (!_settings.FirstRunTipShown)
        {
            var main = _settings.GetHotkey(HotkeyAction.CaptureRegion);
            _tray.Notify($"Capturo está na bandeja. {main} captura uma região; o menu do ícone tem as outras opções.");
            _settings.FirstRunTipShown = true;
            _settings.Save();
        }

        CommandChannel.Listen(c => Dispatcher.BeginInvoke(() => Execute(c)), _shutdown.Token);
        if (command != null)
            Dispatcher.BeginInvoke(() => Execute(command), DispatcherPriority.ApplicationIdle);
    }

    private readonly CancellationTokenSource _shutdown = new();

    private void Execute(string command)
    {
        if (command == "Settings")
            OpenSettings();
        else if (Enum.TryParse<HotkeyAction>(command, out var action))
            Run(action);
    }

    /// <summary>Registra os atalhos das configurações; devolve os que não puderam ser registrados.</summary>
    public IReadOnlyList<HotkeyAction> ApplyHotkeys()
    {
        var failed = new List<HotkeyAction>();
        if (_hotkeys == null)
            return failed;
        _hotkeys.Clear();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var hotkey = _settings.GetHotkey(action);
            if (hotkey.IsNone)
                continue;
            if (!_hotkeys.Register(hotkey.NativeModifiers, hotkey.VirtualKey, () => Run(action)))
                failed.Add(action);
        }
        Log.Info(failed.Count == 0
            ? "Atalhos registrados."
            : "Atalhos em uso por outro programa: " + string.Join(", ", failed.Select(a => $"{a} ({_settings.GetHotkey(a)})")));
        _tray?.RebuildMenu();
        return failed;
    }

    public void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        // Sem atalhos ativos enquanto a janela está aberta, para que a combinação possa ser gravada nos campos.
        _hotkeys?.Clear();
        _settingsWindow = new SettingsWindow(_settings, ApplyHotkeys);
        _settingsWindow.ShowDialog();
        _settingsWindow = null;
        ApplyHotkeys();
    }

    /// <summary>Ponto único de entrada das ações (atalhos e menu da bandeja).</summary>
    public async void Run(HotkeyAction action, bool fromMenu = false)
    {
        // O atalho de gravação também para a gravação em andamento.
        if (_recorder != null)
        {
            if (action is HotkeyAction.RecordVideo or HotkeyAction.RecordVideoRegion)
                await StopRecordingAsync(keep: true);
            return;
        }
        if (_busy)
            return;
        _busy = true;
        try
        {
            // Dá tempo do menu da bandeja sumir antes de congelar a tela.
            if (fromMenu)
                await Task.Delay(250);
            await RunAction(action);
        }
        catch (Exception ex)
        {
            Log.Error($"Falha em {action}.", ex);
            _tray?.Notify($"Não deu certo: {ex.Message}", error: true);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RunAction(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.CaptureRegion:
            case HotkeyAction.CaptureRegionAlternate:
                await CaptureRegion();
                break;

            case HotkeyAction.CaptureLastRegion:
                if (LastRegion() is { } last)
                    OnCaptured(ScreenGrabber.Grab(last));
                else
                    await CaptureRegion();
                break;

            case HotkeyAction.CaptureFullScreen:
                OnCaptured(ScreenGrabber.Grab(ScreenGrabber.MonitorAt(ScreenGrabber.CursorPosition)));
                break;

            case HotkeyAction.CaptureAllScreens:
                OnCaptured(ScreenGrabber.Grab(ScreenGrabber.VirtualScreen));
                break;

            case HotkeyAction.CaptureActiveWindow:
                var window = ActiveWindowArea();
                if (window is { } area)
                    OnCaptured(ScreenGrabber.Grab(area));
                else
                    await CaptureRegion();
                break;

            case HotkeyAction.CaptureDelayed:
                await CountdownWindow.RunAsync(Math.Clamp(_settings.DelaySeconds, 1, 30));
                await CaptureRegion();
                break;

            case HotkeyAction.CaptureScrolling:
                await CaptureScrolling();
                break;

            case HotkeyAction.RecognizeText:
                await RecognizeText();
                break;

            case HotkeyAction.RecognizeQrCode:
                await ReadCode();
                break;

            case HotkeyAction.ColorPicker:
                var picked = await CaptureSession.SelectAsync(SelectionPurpose.ColorPicker);
                if (picked.PickedColor is { } color)
                    OnColorPicked(color);
                break;

            case HotkeyAction.RecordVideo:
                await StartRecordingAsync(ScreenGrabber.MonitorAt(ScreenGrabber.CursorPosition));
                break;

            case HotkeyAction.RecordVideoRegion:
                var selection = await CaptureSession.SelectAsync(SelectionPurpose.Video);
                if (selection.Area is { } videoArea)
                    await StartRecordingAsync(videoArea);
                break;
        }
    }

    // ───────────────────────── Capturas de imagem ─────────────────────────

    private async Task CaptureRegion()
    {
        var result = await CaptureSession.SelectAsync();
        if (result.PickedColor is { } color)
            OnColorPicked(color);
        else if (result.Image is { } image)
        {
            if (result.Area is { } area)
                RememberRegion(area);
            OnCaptured(image);
        }
    }

    private void RememberRegion(PxRect area)
    {
        _settings.LastRegion = new[] { area.X, area.Y, area.Width, area.Height };
        _settings.Save();
    }

    private PxRect? LastRegion()
    {
        if (_settings.LastRegion is not { Length: 4 } r)
            return null;
        var area = new PxRect(r[0], r[1], r[2], r[3]).Intersect(ScreenGrabber.VirtualScreen);
        return area.IsEmpty ? null : area;
    }

    private static PxRect? ActiveWindowArea()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
            return null;
        var info = WindowFinder.GetVisibleWindows().FirstOrDefault(w => w.Handle == handle);
        var area = info?.Bounds.Intersect(ScreenGrabber.VirtualScreen);
        return area is { IsEmpty: false } ? area : null;
    }

    private async Task CaptureScrolling()
    {
        var selection = await CaptureSession.SelectAsync(SelectionPurpose.Scrolling);
        if (selection.Area is not { } area)
            return;
        _tray?.Notify("Rolando e capturando… pressione Esc para parar.");
        await Task.Delay(150);
        OnCaptured(await ScrollingCapture.RunAsync(area));
    }

    private async Task RecognizeText()
    {
        var selection = await CaptureSession.SelectAsync(SelectionPurpose.TextRecognition);
        if (selection.Image is not { } image)
            return;
        string text = await TextRecognizer.RecognizeAsync(image);
        if (string.IsNullOrWhiteSpace(text))
        {
            _tray?.Notify("Nenhum texto foi reconhecido nessa área.", error: true);
            return;
        }
        OutputService.CopyText(text);
        BringToFront(new TextResultWindow("Texto reconhecido (já copiado)", text));
    }

    private async Task ReadCode()
    {
        var selection = await CaptureSession.SelectAsync(SelectionPurpose.QrCode);
        if (selection.Image is not { } image)
            return;
        string? text = await Task.Run(() => CodeReader.Read(image));
        if (string.IsNullOrEmpty(text))
        {
            _tray?.Notify("Nenhum QR Code ou código de barras foi encontrado nessa área.", error: true);
            return;
        }
        OutputService.CopyText(text);
        BringToFront(new TextResultWindow("Código lido (já copiado)", text));
    }

    private void OnCaptured(BitmapSource image)
    {
        string? path = null;
        if (_settings.AutoSave)
        {
            try
            {
                path = OutputService.SaveNew(image, _settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
            BringToFront(editor);
        }
        else
        {
            _tray?.Notify(path != null ? $"Captura salva: {Path.GetFileName(path)}" : "Captura copiada.");
        }
    }

    /// <summary>Mostra a janela na frente das outras mesmo quando o Windows nega o foco a um app em segundo plano.</summary>
    private static void BringToFront(Window window)
    {
        window.Topmost = true;
        window.Show();
        window.Activate();
        window.Dispatcher.BeginInvoke(() => window.Topmost = false, DispatcherPriority.ApplicationIdle);
    }

    private void OnColorPicked(Color color)
    {
        string hex = BitmapUtil.ToHex(color);
        if (OutputService.CopyText(hex))
            _tray?.Notify($"Cor {hex} copiada (rgb {color.R}, {color.G}, {color.B}).");
    }

    // ───────────────────────── Vídeo ─────────────────────────

    private async Task StartRecordingAsync(PxRect area)
    {
        string path = Path.ChangeExtension(OutputService.MakeCapturePath(_settings.SaveFolder, DateTime.Now), ".mp4");
        var options = new VideoOptions(Math.Clamp(_settings.VideoFramesPerSecond, 5, 60), _settings.VideoCaptureCursor, _settings.VideoHighlightClicks);
        var recorder = new VideoRecorder(area, options, path);

        await recorder.StartAsync();
        _recorder = recorder;
        _recordingFrame = new RecordingFrame(recorder.Area);
        _recordingFrame.Show();

        _recordingToolbar = new RecordingToolbar(recorder);
        _recordingToolbar.StopRequested += async (_, _) => await StopRecordingAsync(keep: true);
        _recordingToolbar.DiscardRequested += async (_, _) => await StopRecordingAsync(keep: false);
        _recordingToolbar.Show();
        _tray?.SetRecording(true);
    }

    private async Task StopRecordingAsync(bool keep)
    {
        var recorder = _recorder;
        if (recorder == null)
            return;
        _recorder = null;
        _recordingToolbar?.Close();
        _recordingFrame?.Close();
        _recordingToolbar = null;
        _recordingFrame = null;
        _tray?.SetRecording(false);

        try
        {
            string path = await recorder.StopAsync();
            if (!keep)
            {
                File.Delete(path);
                _tray?.Notify("Gravação descartada.");
                return;
            }
            _tray?.Notify($"Vídeo salvo: {Path.GetFileName(path)}");
            TrayIcon.RevealFile(path);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao finalizar o vídeo.", ex);
            _tray?.Notify($"O vídeo não pôde ser finalizado: {ex.Message}", error: true);
        }
    }

    // ───────────────────────── Ciclo de vida ─────────────────────────

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erro não tratado.", e.Exception);
        _tray?.Notify($"Erro inesperado: {e.Exception.Message}", error: true);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        // Saindo no meio de uma gravação: fecha o arquivo para ele não ficar corrompido.
        // (StopAsync não volta para a thread da interface, então esperar aqui não trava.)
        _recorder?.StopAsync().Wait(TimeSpan.FromSeconds(10));
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
