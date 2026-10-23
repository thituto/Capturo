using System.Windows.Controls;
using System.Windows.Input;

namespace Capturo.Services;

/// <summary>Campo que grava uma combinação de teclas: clique nele e pressione o atalho; Backspace ou Delete limpam.</summary>
public sealed class HotkeyBox : TextBox
{
    private Hotkey _hotkey = Hotkey.None;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = "Clique e pressione a combinação (ex.: Win+Shift+W). Backspace limpa.";
        GotKeyboardFocus += (_, _) => { if (_hotkey.IsNone) Text = "Pressione o atalho…"; };
        LostKeyboardFocus += (_, _) => Text = Display(_hotkey);
    }

    public event EventHandler? HotkeyChanged;

    public Hotkey Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            Text = Display(value);
            HotkeyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            Hotkey = Hotkey.None;
            return;
        }
        if (modifiers == ModifierKeys.None && key is Key.Escape or Key.Tab)
        {
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }
        if (Hotkey.IsModifierKey(key))
        {
            // Mostra os modificadores enquanto o usuário ainda não escolheu a tecla final.
            Text = string.Join("", Parts(modifiers)) + "…";
            return;
        }
        Hotkey = new Hotkey(modifiers, key);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // O Windows entrega a tecla Print Screen só no "soltar".
        if (e.Key == Key.Snapshot)
        {
            Hotkey = new Hotkey(Keyboard.Modifiers, Key.Snapshot);
            e.Handled = true;
            return;
        }
        if (Hotkey.IsModifierKey(e.Key == Key.System ? e.SystemKey : e.Key) && Keyboard.Modifiers == ModifierKeys.None)
            Text = IsKeyboardFocused && _hotkey.IsNone ? "Pressione o atalho…" : Display(_hotkey);
        base.OnPreviewKeyUp(e);
    }

    private static IEnumerable<string> Parts(ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Windows)) yield return "Win+";
        if (modifiers.HasFlag(ModifierKeys.Control)) yield return "Ctrl+";
        if (modifiers.HasFlag(ModifierKeys.Alt)) yield return "Alt+";
        if (modifiers.HasFlag(ModifierKeys.Shift)) yield return "Shift+";
    }

    private static string Display(Hotkey hotkey) => hotkey.IsNone ? "(nenhum)" : hotkey.ToString();
}
