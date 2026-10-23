using System.Diagnostics;
using System.IO;
using Capturo.Capture;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Capturo.Services;

/// <summary>Ícone e menu na área de notificação; é a "janela principal" do app.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _image;

    public TrayIcon(App app, AppSettings settings)
    {
        _image = CreateIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Capturar região\tPrint", null, (_, _) => app.StartCapture(CaptureMode.Region, fromMenu: true));
        menu.Items.Add("Capturar monitor atual\tShift+Print", null, (_, _) => app.StartCapture(CaptureMode.CurrentMonitor, fromMenu: true));
        menu.Items.Add("Capturar todas as telas\tCtrl+Print", null, (_, _) => app.StartCapture(CaptureMode.AllMonitors, fromMenu: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Toggle("Copiar ao capturar", settings.CopyOnCapture, v => { settings.CopyOnCapture = v; settings.Save(); }));
        menu.Items.Add(Toggle("Salvar automaticamente", settings.AutoSave, v => { settings.AutoSave = v; settings.Save(); }));
        menu.Items.Add(Toggle("Abrir o editor após capturar", settings.OpenEditor, v => { settings.OpenEditor = v; settings.Save(); }));
        menu.Items.Add("Abrir pasta de capturas", null, (_, _) => OpenFolder(settings.SaveFolder));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => app.Shutdown());

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "Capturo — Print para capturar",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                app.StartCapture(CaptureMode.Region, fromMenu: true);
        };
    }

    public void Notify(string message, bool error = false) =>
        _icon.ShowBalloonTip(3000, "Capturo", message, error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    private static Forms.ToolStripMenuItem Toggle(string text, bool initial, Action<bool> changed)
    {
        var item = new Forms.ToolStripMenuItem(text) { Checked = initial, CheckOnClick = true };
        item.CheckedChanged += (_, _) => changed(item.Checked);
        return item;
    }

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    /// <summary>Desenha o ícone em código: quadrado azul com cantos de seleção brancos.</summary>
    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x3B, 0x82, 0xF6));
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
        IntPtr handle = bitmap.GetHicon();
        return Drawing.Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
