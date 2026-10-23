using System.Collections.Immutable;
using System.Windows.Media.Imaging;

namespace Capturo.Editor;

/// <summary>
/// Estado completo do documento: a imagem (cortar, recortar e redimensionar geram uma nova), as anotações
/// por cima dela e os efeitos de exportação. Imutável: desfazer é voltar ao instantâneo anterior.
/// </summary>
public sealed record EditorState(BitmapSource Image, ImmutableList<Annotation> Items, ImageEffects Effects)
{
    public static EditorState For(BitmapSource image, ImageEffects? effects = null) =>
        new(image, ImmutableList<Annotation>.Empty, effects ?? ImageEffects.None);

    public int NextStepNumber => Items.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;
}

/// <summary>Desfazer/refazer por instantâneos imutáveis.</summary>
public sealed class EditorHistory
{
    private readonly Stack<EditorState> _undo = new();
    private readonly Stack<EditorState> _redo = new();

    public EditorHistory(EditorState initial)
    {
        Current = initial;
    }

    public EditorState Current { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(EditorState next)
    {
        if (next == Current)
            return;
        _undo.Push(Current);
        _redo.Clear();
        Current = next;
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;
        _redo.Push(Current);
        Current = _undo.Pop();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;
        _undo.Push(Current);
        Current = _redo.Pop();
        return true;
    }

    /// <summary>Volta ao estado original (o desfazer continua podendo refazer tudo).</summary>
    public void UndoAll()
    {
        while (Undo()) { }
    }
}
