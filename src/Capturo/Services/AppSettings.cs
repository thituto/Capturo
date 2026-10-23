using System.IO;
using System.Text.Json;

namespace Capturo.Services;

public sealed class AppSettings
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Capturo");

    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    /// <summary>Copia a captura para a área de transferência assim que ela é feita.</summary>
    public bool CopyOnCapture { get; set; } = true;

    /// <summary>Salva a captura automaticamente na pasta de capturas.</summary>
    public bool AutoSave { get; set; } = true;

    /// <summary>Abre o editor depois de cada captura.</summary>
    public bool OpenEditor { get; set; } = true;

    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Capturo");

    public bool FirstRunTipShown { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Configurações ilegíveis; usando o padrão.", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Não foi possível salvar as configurações.", ex);
        }
    }
}
