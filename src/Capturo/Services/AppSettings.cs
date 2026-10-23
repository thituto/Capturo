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

    /// <summary>Últimos efeitos de imagem usados no editor (borda, sombra...), aplicados às próximas capturas.</summary>
    public EffectsSettings Effects { get; set; } = new();

    /// <summary>Última região capturada, em pixels físicos da tela virtual (para "Capturar última região").</summary>
    public int[]? LastRegion { get; set; }

    /// <summary>Segundos de espera da captura com atraso.</summary>
    public int DelaySeconds { get; set; } = 3;

    public int VideoFramesPerSecond { get; set; } = 20;
    public bool VideoCaptureCursor { get; set; } = true;
    public bool VideoHighlightClicks { get; set; } = true;

    /// <summary>Atalhos personalizados (nome da ação → "Win+Shift+W"; texto vazio = sem atalho). Ação ausente usa o padrão.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    public Hotkey GetHotkey(HotkeyAction action) =>
        Hotkeys.TryGetValue(action.ToString(), out var text)
            ? Hotkey.Parse(text)
            : Hotkey.Defaults.GetValueOrDefault(action, Hotkey.None);

    public void SetHotkey(HotkeyAction action, Hotkey hotkey) => Hotkeys[action.ToString()] = hotkey.ToString();

    public sealed class EffectsSettings
    {
        public bool Border { get; set; }
        public string BorderColor { get; set; } = "#9CA3AF";
        public double BorderWidth { get; set; } = 2;
        public bool Shadow { get; set; }
        public bool RoundedCorners { get; set; }
        public string Torn { get; set; } = "None";
    }

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
