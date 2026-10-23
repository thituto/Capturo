using System.Windows.Input;

namespace Capturo.Services;

/// <summary>Ações que podem ter atalho global, na ordem em que aparecem nas configurações.</summary>
public enum HotkeyAction
{
    CaptureRegion,
    CaptureRegionAlternate,
    CaptureLastRegion,
    CaptureFullScreen,
    CaptureAllScreens,
    CaptureActiveWindow,
    CaptureDelayed,
    CaptureScrolling,
    RecognizeText,
    RecognizeQrCode,
    ColorPicker,
    RecordVideo,
    RecordVideoRegion,
}

/// <summary>Combinação de teclas global. Os modificadores do WPF têm os mesmos valores do RegisterHotKey (Alt=1, Ctrl=2, Shift=4, Win=8).</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static readonly Hotkey None = new(ModifierKeys.None, Key.None);

    public bool IsNone => Key == Key.None;

    public uint NativeModifiers => (uint)Modifiers;
    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        if (IsNone)
            return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return None;
        var modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "win": modifiers |= ModifierKeys.Windows; break;
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "print": key = Key.Snapshot; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0]))
                        key = Key.D0 + (raw[0] - '0');
                    else if (Enum.TryParse(raw, true, out Key parsed))
                        key = parsed;
                    break;
            }
        }
        return new Hotkey(modifiers, key);
    }

    /// <summary>Teclas que não podem formar um atalho sozinhas.</summary>
    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private static string KeyName(Key key) => key switch
    {
        Key.Snapshot => "Print",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString(),
    };

    /// <summary>Atalhos de fábrica: Win+Shift+W é o principal; os demais seguem o Screenpresso.</summary>
    public static IReadOnlyDictionary<HotkeyAction, Hotkey> Defaults { get; } = new Dictionary<HotkeyAction, Hotkey>
    {
        [HotkeyAction.CaptureRegion] = new(ModifierKeys.Windows | ModifierKeys.Shift, Key.W),
        [HotkeyAction.CaptureRegionAlternate] = new(ModifierKeys.None, Key.Snapshot),
        [HotkeyAction.CaptureLastRegion] = new(ModifierKeys.Shift, Key.Snapshot),
        [HotkeyAction.CaptureFullScreen] = new(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.Snapshot),
        [HotkeyAction.RecordVideo] = new(ModifierKeys.Control, Key.Snapshot),
        [HotkeyAction.RecordVideoRegion] = new(ModifierKeys.Control | ModifierKeys.Shift, Key.Snapshot),
    };

    public static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.CaptureRegion => "Capturar região",
        HotkeyAction.CaptureRegionAlternate => "Capturar região (atalho alternativo)",
        HotkeyAction.CaptureLastRegion => "Capturar a última região",
        HotkeyAction.CaptureFullScreen => "Capturar tela cheia (monitor atual)",
        HotkeyAction.CaptureAllScreens => "Capturar todas as telas",
        HotkeyAction.CaptureActiveWindow => "Capturar a janela ativa",
        HotkeyAction.CaptureDelayed => "Captura com atraso",
        HotkeyAction.CaptureScrolling => "Captura com rolagem automática",
        HotkeyAction.RecognizeText => "Reconhecer texto (OCR)",
        HotkeyAction.RecognizeQrCode => "Ler QR Code",
        HotkeyAction.ColorPicker => "Seletor de cor",
        HotkeyAction.RecordVideo => "Gravar vídeo (tela cheia)",
        HotkeyAction.RecordVideoRegion => "Gravar vídeo de uma região",
        _ => action.ToString(),
    };
}
