using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Capturo.Capture;
using Capturo.Services;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Drawing = System.Drawing;

namespace Capturo.Video;

public sealed record VideoOptions(int FramesPerSecond, bool CaptureCursor, bool HighlightClicks);

/// <summary>
/// Grava uma área da tela em MP4 (H.264). Uma thread copia a tela no ritmo do fps, desenha cursor e cliques e
/// entrega os quadros a um MediaStreamSource; o MediaTranscoder do Windows codifica (com aceleração de hardware
/// quando disponível) direto no arquivo.
/// </summary>
public sealed class VideoRecorder
{
    private readonly PxRect _area;
    private readonly VideoOptions _options;
    private readonly string _path;
    private readonly Channel<Frame> _frames = Channel.CreateBounded<Frame>(new BoundedChannelOptions(8)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly Stopwatch _clock = new();
    private readonly List<ClickMark> _clicks = new();
    private Thread? _captureThread;
    private Task? _encoding;
    private volatile bool _stopping;
    private volatile bool _paused;
    private TimeSpan _pausedTotal;
    private TimeSpan _pausedAt;
    private bool _leftWasDown;
    private bool _rightWasDown;

    private readonly record struct Frame(byte[] Pixels, TimeSpan Timestamp);
    private readonly record struct ClickMark(int X, int Y, long At, bool Right);

    /// <summary>Maior vídeo que os codificadores H.264 aceitam com folga (nível 5.1); áreas maiores são reduzidas.</summary>
    public const int MaxWidth = 3840;
    public const int MaxHeight = 2160;

    private readonly int _outputWidth;
    private readonly int _outputHeight;

    public VideoRecorder(PxRect area, VideoOptions options, string path)
    {
        // H.264 exige largura e altura pares.
        _area = area with { Width = Math.Max(2, area.Width & ~1), Height = Math.Max(2, area.Height & ~1) };
        _options = options;
        _path = path;
        (_outputWidth, _outputHeight) = OutputSize(_area.Width, _area.Height);
    }

    /// <summary>Tamanho do vídeo: o da área, ou reduzido proporcionalmente para caber em 3840 × 2160 (sempre par).</summary>
    public static (int Width, int Height) OutputSize(int width, int height)
    {
        double scale = Math.Min(1, Math.Min(MaxWidth / (double)width, MaxHeight / (double)height));
        return (Math.Max(2, (int)(width * scale) & ~1), Math.Max(2, (int)(height * scale) & ~1));
    }

    public PxRect Area => _area;
    public string FilePath => _path;
    public bool IsPaused => _paused;

    /// <summary>Tempo gravado, sem contar as pausas.</summary>
    public TimeSpan Elapsed => (_paused ? _pausedAt : _clock.Elapsed) - _pausedTotal;

    /// <summary>
    /// Prepara o codificador numa thread do pool (MTA): os codificadores de hardware do Media Foundation
    /// recusam a montagem feita a partir da thread STA da interface.
    /// </summary>
    public Task StartAsync() => Task.Run(StartCoreAsync);

    private async Task StartCoreAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, Array.Empty<byte>());
        var file = await StorageFile.GetFileFromPathAsync(_path);
        var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        try
        {
            var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_outputWidth, (uint)_outputHeight);
            input.FrameRate.Numerator = (uint)_options.FramesPerSecond;
            input.FrameRate.Denominator = 1;
            input.PixelAspectRatio.Numerator = 1;
            input.PixelAspectRatio.Denominator = 1;
            var source = new MediaStreamSource(new VideoStreamDescriptor(input))
            {
                BufferTime = TimeSpan.Zero,
                CanSeek = false,
            };
            source.SampleRequested += OnSampleRequested;

            // Parte do perfil MP4 pronto do Windows: um perfil H.264 montado do zero é recusado pelo codificador de hardware.
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Audio = null;
            profile.Video.Width = (uint)_outputWidth;
            profile.Video.Height = (uint)_outputHeight;
            profile.Video.FrameRate.Numerator = (uint)_options.FramesPerSecond;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.Bitrate = (uint)Math.Clamp(_outputWidth * (long)_outputHeight * _options.FramesPerSecond / 6, 2_000_000, 40_000_000);

            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
            if (!prepared.CanTranscode)
                throw new InvalidOperationException($"O Windows não conseguiu preparar a gravação ({prepared.FailureReason}).");
            _encoding = EncodeAsync(prepared, stream);
        }
        catch
        {
            stream.Dispose();
            TryDelete(_path);
            throw;
        }

        Log.Info($"Gravação iniciada: área {_area.X},{_area.Y} {_area.Width}×{_area.Height}, vídeo {_outputWidth}×{_outputHeight} a {_options.FramesPerSecond} fps.");
        _clock.Start();
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Capturo video", Priority = ThreadPriority.AboveNormal };
        _captureThread.Start();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task EncodeAsync(PrepareTranscodeResult prepared, IDisposable stream)
    {
        try
        {
            await prepared.TranscodeAsync().AsTask().ConfigureAwait(false);
        }
        finally
        {
            stream.Dispose();
        }
    }

    public void TogglePause()
    {
        if (_paused)
        {
            _pausedTotal += _clock.Elapsed - _pausedAt;
            _paused = false;
        }
        else
        {
            _pausedAt = _clock.Elapsed;
            _paused = true;
        }
    }

    /// <summary>Para a captura, espera o codificador terminar o arquivo e devolve o caminho.</summary>
    public async Task<string> StopAsync()
    {
        _stopping = true;
        _captureThread?.Join(2000);
        _frames.Writer.TryComplete();
        Log.Info($"Parando gravação: {_captured} quadros capturados, {_delivered} entregues ao codificador.");
        if (_encoding != null)
            await _encoding.ConfigureAwait(false);
        Log.Info($"Vídeo finalizado: {_path} ({new FileInfo(_path).Length} bytes).");
        return _path;
    }

    private int _captured;
    private int _delivered;

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        var deferral = request.GetDeferral();
        _ = Task.Run(async () =>
        {
            try
            {
                if (await _frames.Reader.WaitToReadAsync() && _frames.Reader.TryRead(out var frame))
                {
                    var sample = MediaStreamSample.CreateFromBuffer(frame.Pixels.AsBuffer(), frame.Timestamp);
                    sample.Duration = TimeSpan.FromSeconds(1.0 / _options.FramesPerSecond);
                    request.Sample = sample;
                    Interlocked.Increment(ref _delivered);
                }
                else
                {
                    request.Sample = null; // fim da gravação
                }
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao entregar quadro de vídeo.", ex);
                request.Sample = null;
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void CaptureLoop()
    {
        var interval = TimeSpan.FromSeconds(1.0 / _options.FramesPerSecond);
        using var bitmap = new Drawing.Bitmap(_area.Width, _area.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var next = _clock.Elapsed;

        // Área maior que o limite do codificador: cada quadro é reduzido antes de ir para o vídeo.
        bool scaled = _outputWidth != _area.Width || _outputHeight != _area.Height;
        using var output = scaled ? new Drawing.Bitmap(_outputWidth, _outputHeight, Drawing.Imaging.PixelFormat.Format32bppArgb) : null;
        using var outputGraphics = output != null ? Drawing.Graphics.FromImage(output) : null;
        if (outputGraphics != null)
        {
            // Bilinear simples: a versão "alta qualidade" do GDI+ custa caro demais a cada quadro.
            outputGraphics.InterpolationMode = Drawing.Drawing2D.InterpolationMode.Bilinear;
            outputGraphics.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
            outputGraphics.CompositingMode = Drawing.Drawing2D.CompositingMode.SourceCopy;
            outputGraphics.CompositingQuality = Drawing.Drawing2D.CompositingQuality.HighSpeed;
        }

        while (!_stopping)
        {
            if (_paused)
            {
                Thread.Sleep(20);
                next = _clock.Elapsed;
                continue;
            }

            var timestamp = _clock.Elapsed - _pausedTotal;
            try
            {
                CopyScreen(graphics);
                if (_options.HighlightClicks)
                    DrawClicks(graphics);
                if (_options.CaptureCursor)
                    DrawCursor(graphics);
                if (output != null)
                    outputGraphics!.DrawImage(bitmap, 0, 0, _outputWidth, _outputHeight);
                _frames.Writer.TryWrite(new Frame(ToBytes(output ?? bitmap), timestamp));
                Interlocked.Increment(ref _captured);
            }
            catch (Exception ex)
            {
                // A tela pode ficar indisponível por instantes (UAC, bloqueio); o quadro é pulado.
                // Nada aqui pode derrubar o app: esta thread não tem quem trate exceções.
                Log.Error("Quadro de vídeo perdido.", ex);
                Thread.Sleep(50);
            }

            next += interval;
            var wait = next - _clock.Elapsed;
            if (wait > TimeSpan.Zero)
                Thread.Sleep(wait);
            else
                next = _clock.Elapsed; // atrasou: não tenta recuperar quadros perdidos
        }
    }

    /// <summary>BitBlt com CAPTUREBLT (inclui janelas em camadas); o CopyFromScreen do .NET não aceita essa combinação.</summary>
    private void CopyScreen(Drawing.Graphics graphics)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr target = graphics.GetHdc();
        try
        {
            if (!BitBlt(target, 0, 0, _area.Width, _area.Height, screen, _area.X, _area.Y, SRCCOPY | CAPTUREBLT))
                throw new System.ComponentModel.Win32Exception();
        }
        finally
        {
            graphics.ReleaseHdc(target);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static byte[] ToBytes(Drawing.Bitmap bitmap)
    {
        var rect = new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // O Media Foundation lê quadros RGB de baixo para cima: a última linha da tela vai primeiro.
            int stride = bitmap.Width * 4;
            int height = bitmap.Height;
            var pixels = new byte[stride * height];
            for (int y = 0; y < height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, (height - 1 - y) * stride, stride);
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void DrawCursor(Drawing.Graphics graphics)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero)
            return;
        int hotX = 0, hotY = 0;
        if (GetIconInfo(info.hCursor, out var icon))
        {
            hotX = icon.xHotspot;
            hotY = icon.yHotspot;
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
        IntPtr hdc = graphics.GetHdc();
        try
        {
            DrawIconEx(hdc, info.ptScreenPos.X - _area.X - hotX, info.ptScreenPos.Y - _area.Y - hotY, info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    /// <summary>Clique = círculo que cresce e some em meio segundo (amarelo: esquerdo, vermelho: direito).</summary>
    private void DrawClicks(Drawing.Graphics graphics)
    {
        long now = _clock.ElapsedMilliseconds;
        bool left = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        bool right = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        var cursor = ScreenGrabber.CursorPosition;
        if (left && !_leftWasDown)
            _clicks.Add(new ClickMark(cursor.X, cursor.Y, now, false));
        if (right && !_rightWasDown)
            _clicks.Add(new ClickMark(cursor.X, cursor.Y, now, true));
        _leftWasDown = left;
        _rightWasDown = right;

        _clicks.RemoveAll(c => now - c.At > 600);
        foreach (var click in _clicks)
        {
            double progress = (now - click.At) / 600.0;
            float radius = (float)(10 + 22 * progress);
            int alpha = (int)(200 * (1 - progress));
            var color = click.Right ? Drawing.Color.FromArgb(alpha, 0xEF, 0x44, 0x44) : Drawing.Color.FromArgb(alpha, 0xFA, 0xCC, 0x15);
            float x = click.X - _area.X, y = click.Y - _area.Y;
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(alpha / 3, color));
            using var pen = new Drawing.Pen(color, 3);
            graphics.FillEllipse(fill, x - radius, y - radius, radius * 2, radius * 2);
            graphics.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);
        }

        // Botão ainda pressionado: anel fixo em volta do cursor.
        if (left || right)
        {
            using var pen = new Drawing.Pen(left ? Drawing.Color.FromArgb(220, 0xFA, 0xCC, 0x15) : Drawing.Color.FromArgb(220, 0xEF, 0x44, 0x44), 3);
            graphics.DrawEllipse(pen, cursor.X - _area.X - 14, cursor.Y - _area.Y - 14, 28, 28);
        }
    }

    // ───────────── Win32 ─────────────

    private const int CURSOR_SHOWING = 0x1;
    private const int DI_NORMAL = 0x3;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int width, int height, int step, IntPtr brush, int flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
}
