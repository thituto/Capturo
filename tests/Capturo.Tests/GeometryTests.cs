using Capturo.Capture;
using Xunit;

namespace Capturo.Tests;

public class GeometryTests
{
    [Fact]
    public void FromCorners_includes_both_end_pixels_in_any_drag_direction()
    {
        var forward = PxRect.FromCorners(new PxPoint(10, 20), new PxPoint(19, 29));
        var backward = PxRect.FromCorners(new PxPoint(19, 29), new PxPoint(10, 20));

        Assert.Equal(new PxRect(10, 20, 10, 10), forward);
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Intersect_returns_overlap_or_empty()
    {
        var a = new PxRect(0, 0, 100, 100);

        Assert.Equal(new PxRect(50, 60, 50, 40), a.Intersect(new PxRect(50, 60, 200, 200)));
        Assert.True(a.Intersect(new PxRect(100, 0, 10, 10)).IsEmpty);
    }

    [Fact]
    public void Contains_is_exclusive_on_the_right_and_bottom_edges()
    {
        var r = new PxRect(-1920, 0, 1920, 1080); // monitor à esquerda do principal

        Assert.True(r.Contains(new PxPoint(-1920, 0)));
        Assert.True(r.Contains(new PxPoint(-1, 1079)));
        Assert.False(r.Contains(new PxPoint(0, 0)));
    }

    [Fact]
    public void FindAt_picks_the_frontmost_window_containing_the_point()
    {
        var front = new WindowInfo(new IntPtr(1), new PxRect(100, 100, 200, 200), "Diálogo");
        var back = new WindowInfo(new IntPtr(2), new PxRect(0, 0, 800, 600), "Editor");
        var windows = new[] { front, back };

        Assert.Same(front, WindowFinder.FindAt(windows, new PxPoint(150, 150)));
        Assert.Same(back, WindowFinder.FindAt(windows, new PxPoint(10, 10)));
        Assert.Null(WindowFinder.FindAt(windows, new PxPoint(900, 900)));
    }
}
