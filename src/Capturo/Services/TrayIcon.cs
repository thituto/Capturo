using System.Diagnostics;
using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Capturo.Services;

/// <summary>Ícone e menu na área de notificação; é a "janela principal" do app.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly AppSettings _settings;
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _image;
    private readonly Drawing.Icon _recordingImage;

    public TrayIcon(App app, AppSettings settings)
    {
        _app = app;
        _settings = settings;
        _image = CreateIcon(Drawing.Color.FromArgb(0x3B, 0x82, 0xF6));
        _recordingImage = CreateIcon(Drawing.Color.FromArgb(0xEF, 0x44, 0x44));
        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "Capturo",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _app.Run(HotkeyAction.CaptureRegion, fromMenu: true);
        };
        RebuildMenu();
    }

    /// <summary>Recria o menu (os atalhos mostrados mudam quando o usuário os reconfigura).</summary>
    public void RebuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        void Action(HotkeyAction action, string? text = null)
        {
            var hotkey = _settings.GetHotkey(action);
            var item = new Forms.ToolStripMenuItem(text ?? Hotkey.Describe(action), null, (_, _) => _app.Run(action, fromMenu: true))
            {
                ShortcutKeyDisplayString = hotkey.IsNone ? null : hotkey.ToString(),
            };
            menu.Items.Add(item);
        }

        Action(HotkeyAction.CaptureRegion);
        Action(HotkeyAction.CaptureLastRegion);
        Action(HotkeyAction.CaptureFullScreen);
        Action(HotkeyAction.CaptureAllScreens);
        Action(HotkeyAction.CaptureActiveWindow);
        Action(HotkeyAction.CaptureDelayed, $"Captura com atraso ({_settings.DelaySeconds} s)");
        Action(HotkeyAction.CaptureScrolling);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Action(HotkeyAction.RecognizeText);
        Action(HotkeyAction.RecognizeQrCode);
        Action(HotkeyAction.ColorPicker);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Action(HotkeyAction.RecordVideo);
        Action(HotkeyAction.RecordVideoRegion);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Abrir pasta de capturas", null, (_, _) => OpenFolder(_settings.SaveFolder));
        menu.Items.Add("Configurações…", null, (_, _) => _app.OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => _app.Shutdown());

        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();

        var main = _settings.GetHotkey(HotkeyAction.CaptureRegion);
        _icon.Text = main.IsNone ? "Capturo" : $"Capturo — {main} para capturar";
    }

    public void SetRecording(bool recording)
    {
        _icon.Icon = recording ? _recordingImage : _image;
        _icon.Text = recording ? "Capturo — gravando (atalho de vídeo ou a barra para parar)" : "Capturo";
    }

    public void Notify(string message, bool error = false) =>
        _icon.ShowBalloonTip(3000, "Capturo", message, error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    public static void RevealFile(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    /// <summary>Desenha o ícone em código: quadrado colorido com cantos de seleção brancos.</summary>
    private static Drawing.Icon CreateIcon(Drawing.Color color)
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var background = new Drawing.SolidBrush(color);
            using var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(1, 1, 10, 10, 180, 90);
            path.AddArc(21, 1, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90);
            path.AddArc(1, 21, 10, 10, 90, 90);
            path.CloseFigure();
            g.FillPath(background, path);
            using var pen = new Drawing.Pen(Drawing.Color.White, 3) { StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round };
            g.DrawLines(pen, new[] { new Drawing.Point(8, 14), new Drawing.Point(8, 8), new Drawing.Point(14, 8) });
            g.DrawLines(pen, new[] { new Drawing.Point(18, 24), new Drawing.Point(24, 24), new Drawing.Point(24, 18) });
        }
        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _image.Dispose();
        _recordingImage.Dispose();
    }
}
