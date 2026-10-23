using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using Capturo.Editor;
using Capturo.Services;
using Xunit;

namespace Capturo.Tests;

public class EditorModelTests
{
    private static readonly Color Red = Colors.Red;

    [Fact]
    public void History_undo_and_redo_walk_the_snapshots()
    {
        var history = new EditorHistory();
        var one = EditorState.Empty with { Items = ImmutableList.Create<Annotation>(new PixelateAnnotation(new Rect(0, 0, 10, 10))) };
        var two = one with { Crop = new Int32Rect(0, 0, 5, 5) };

        history.Push(one);
        history.Push(two);
        Assert.True(history.Undo());
        Assert.Same(one, history.Current);
        Assert.True(history.Redo());
        Assert.Same(two, history.Current);
    }

    [Fact]
    public void Pushing_after_undo_discards_the_redo_branch()
    {
        var history = new EditorHistory();
        history.Push(EditorState.Empty with { Crop = new Int32Rect(0, 0, 1, 1) });
        history.Undo();
        history.Push(EditorState.Empty with { Crop = new Int32Rect(0, 0, 2, 2) });

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Next_step_number_follows_the_highest_existing_step()
    {
        var state = EditorState.Empty with
        {
            Items = ImmutableList.Create<Annotation>(
                new StepAnnotation(new Point(0, 0), 1, Red, 30),
                new StepAnnotation(new Point(0, 0), 4, Red, 30)),
        };

        Assert.Equal(5, state.NextStepNumber);
        Assert.Equal(1, EditorState.Empty.NextStepNumber);
    }

    [Fact]
    public void Offset_moves_every_kind_of_annotation()
    {
        var delta = new Vector(10, 5);
        Annotation[] items =
        {
            new ArrowAnnotation(new Point(0, 0), new Point(20, 0), Red, 4),
            new RectangleAnnotation(new Rect(0, 0, 20, 20), Red, 4),
            new PenAnnotation(ImmutableArray.Create(new Point(0, 0), new Point(5, 5)), Red, 4, false),
            new TextAnnotation(new Point(0, 0), "Oi", Red, 22),
            new StepAnnotation(new Point(0, 0), 1, Red, 30),
            new PixelateAnnotation(new Rect(0, 0, 20, 20)),
        };

        foreach (var item in items)
        {
            var before = item.Bounds;
            var after = item.Offset(delta).Bounds;
            Assert.Equal(before.X + 10, after.X, 6);
            Assert.Equal(before.Y + 5, after.Y, 6);
        }
    }

    [Fact]
    public void WithColor_and_WithSize_restyle_without_moving()
    {
        var arrow = new ArrowAnnotation(new Point(1, 2), new Point(30, 40), Red, 4);
        var restyled = (ArrowAnnotation)arrow.WithColor(Colors.Blue).WithSize(8);

        Assert.Equal(Colors.Blue, restyled.Color);
        Assert.Equal(8, restyled.Thickness);
        Assert.Equal(arrow.Start, restyled.Start);

        // A pixelização não tem cor nem espessura: continua a mesma.
        var pixelate = new PixelateAnnotation(new Rect(0, 0, 1, 1));
        Assert.Same(pixelate, pixelate.WithColor(Colors.Blue).WithSize(8));
    }

    [Fact]
    public void Capture_paths_are_timestamped_and_never_overwrite()
    {
        var when = new DateTime(2026, 10, 22, 14, 5, 33);
        var taken = new HashSet<string> { @"C:\caps\Capturo_2026-10-22_14-05-33.png" };

        string path = OutputService.MakeCapturePath(@"C:\caps", when, taken.Contains);

        Assert.Equal(@"C:\caps\Capturo_2026-10-22_14-05-33_2.png", path);
    }
}
