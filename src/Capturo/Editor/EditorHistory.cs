using System.Collections.Immutable;
using System.Windows;

namespace Capturo.Editor;

/// <summary>Estado completo do documento: as anotações e o recorte (null = imagem inteira).</summary>
public sealed record EditorState(ImmutableList<Annotation> Items, Int32Rect? Crop)
{
    public static readonly EditorState Empty = new(ImmutableList<Annotation>.Empty, null);

    public int NextStepNumber => Items.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;
}

/// <summary>Desfazer/refazer por instantâneos imutáveis.</summary>
public sealed class EditorHistory
{
    private readonly Stack<EditorState> _undo = new();
    private readonly Stack<EditorState> _redo = new();

    public EditorState Current { get; private set; } = EditorState.Empty;
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
}
