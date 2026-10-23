using System.IO;

namespace Capturo.Services;

/// <summary>Log mínimo em %APPDATA%\Capturo\capturo.log, para diagnosticar falhas sem console.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Error(string message, Exception? ex = null) => Write("ERRO", message, ex);
    public static void Info(string message) => Write("INFO", message, null);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DataFolder);
                File.AppendAllText(Path.Combine(AppSettings.DataFolder, "capturo.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // O log nunca pode derrubar o app.
        }
    }
}
