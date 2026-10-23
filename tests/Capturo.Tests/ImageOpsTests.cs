using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capturo.Editor;
using Xunit;

namespace Capturo.Tests;

public class ImageOpsTests
{
    private static readonly Color Red = Colors.Red;

    private static EditorState StateWith(int width, int height, params Annotation[] items) =>
        EditorState.For(EditorModelTests.Image(width, height)) with { Items = items.ToImmutableList() };

    private static byte GreenAt(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[1];
    }

    [Fact]
    public void Crop_cuts_the_image_and_moves_annotations_with_it()
    {
        var state = StateWith(40, 30, new RectangleAnnotation(new Rect(20, 20, 5, 5), Red, 2, ShapeStyle.Default));

        var cropped = ImageOps.Crop(state, new Int32Rect(10, 15, 20, 10));

        Assert.Equal(20, cropped.Image.PixelWidth);
        Assert.Equal(10, cropped.Image.PixelHeight);
        Assert.Equal(15, GreenAt(cropped.Image, 0, 0)); // a linha 15 virou a primeira
        Assert.Equal(new Rect(10, 5, 5, 5), ((RectangleAnnotation)cropped.Items[0]).Rect);
    }

    [Fact]
    public void CutOut_removes_a_band_and_pulls_up_what_was_below()
    {
        var above = new RectangleAnnotation(new Rect(2, 2, 4, 4), Red, 2, ShapeStyle.Default);
        var below = new RectangleAnnotation(new Rect(2, 30, 4, 4), Red, 2, ShapeStyle.Default);
        var state = StateWith(20, 40, above, below);

        var cut = ImageOps.CutOut(state, start: 10, length: 15, horizontal: true);

        Assert.Equal(25, cut.Image.PixelHeight);
        Assert.Equal(9, GreenAt(cut.Image, 0, 9));   // antes da faixa: igual
        Assert.Equal(25, GreenAt(cut.Image, 0, 10)); // logo depois: a antiga linha 25
        Assert.Equal(above, cut.Items[0]);
        Assert.Equal(new Rect(2, 15, 4, 4), ((RectangleAnnotation)cut.Items[1]).Rect);
    }

    [Fact]
    public void Resize_scales_the_image_and_every_annotation()
    {
        var state = StateWith(100, 50, new ArrowAnnotation(new Point(10, 10), new Point(90, 40), Red, 4));

        var resized = ImageOps.Resize(state, 50, 25);

        Assert.Equal(50, resized.Image.PixelWidth);
        Assert.Equal(25, resized.Image.PixelHeight);
        var arrow = (ArrowAnnotation)resized.Items[0];
        Assert.Equal(new Point(5, 5), arrow.Start);
        Assert.Equal(new Point(45, 20), arrow.End);
        Assert.Equal(2, arrow.Thickness, 6);
    }

    [Fact]
    public void ExpandCanvas_adds_margins_and_shifts_annotations()
    {
        var state = StateWith(10, 10, new StepAnnotation(new Point(5, 5), 1, Red, 20));

        var bigger = ImageOps.ExpandCanvas(state, new Thickness(4, 6, 2, 8), Colors.White);

        Assert.Equal(16, bigger.Image.PixelWidth);
        Assert.Equal(24, bigger.Image.PixelHeight);
        Assert.Equal(new Point(9, 11), ((StepAnnotation)bigger.Items[0]).Center);
    }

    [Fact]
    public void Trim_removes_uniform_borders_only()
    {
        // Imagem branca de 30x20 com um quadrado colorido no meio.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 30, 20));
            dc.DrawRectangle(Brushes.Blue, null, new Rect(8, 5, 10, 6));
        }
        var image = new RenderTargetBitmap(30, 20, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();

        var trimmed = ImageOps.TrimUniformBorders(EditorState.For(image));

        Assert.Equal(10, trimmed.Image.PixelWidth);
        Assert.Equal(6, trimmed.Image.PixelHeight);
    }
}
