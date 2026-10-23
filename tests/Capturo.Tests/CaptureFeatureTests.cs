using System.Windows.Input;
using System.Windows.Media.Imaging;
using Capturo.Capture;
using Capturo.Services;
using Xunit;

namespace Capturo.Tests;

public class CaptureFeatureTests
{
    [Theory]
    [InlineData("Win+Shift+W", ModifierKeys.Windows | ModifierKeys.Shift, Key.W)]
    [InlineData("Ctrl+Alt+Shift+Print", ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.Snapshot)]
    [InlineData("Print", ModifierKeys.None, Key.Snapshot)]
    [InlineData("Ctrl+5", ModifierKeys.Control, Key.D5)]
    public void Hotkeys_round_trip_through_text(string text, ModifierKeys modifiers, Key key)
    {
        var hotkey = Hotkey.Parse(text);

        Assert.Equal(new Hotkey(modifiers, key), hotkey);
        Assert.Equal(text, hotkey.ToString());
    }

    [Fact]
    public void Default_region_hotkey_is_win_shift_w_and_can_be_overridden_or_cleared()
    {
        var settings = new AppSettings();
        Assert.Equal("Win+Shift+W", settings.GetHotkey(HotkeyAction.CaptureRegion).ToString());

        settings.SetHotkey(HotkeyAction.CaptureRegion, Hotkey.Parse("Ctrl+Alt+R"));
        settings.SetHotkey(HotkeyAction.CaptureLastRegion, Hotkey.None);

        Assert.Equal("Ctrl+Alt+R", settings.GetHotkey(HotkeyAction.CaptureRegion).ToString());
        Assert.True(settings.GetHotkey(HotkeyAction.CaptureLastRegion).IsNone);
    }

    [Fact]
    public void Native_modifier_values_match_RegisterHotKey()
    {
        var hotkey = Hotkey.Parse("Win+Ctrl+Alt+Shift+A");
        Assert.Equal(0x1u | 0x2u | 0x4u | 0x8u, hotkey.NativeModifiers);
        Assert.Equal(0x41u, hotkey.VirtualKey);
    }

    /// <summary>Simula uma página de 3 telas de altura rolada em passos de 70 linhas, com cabeçalho fixo.</summary>
    [Fact]
    public void Stitcher_rebuilds_a_scrolled_page_with_a_fixed_header()
    {
        const int width = 40, height = 120, header = 10, pageHeight = 300, step = 70;
        byte[] page = MakePage(width, pageHeight);

        var stitcher = new ScrollStitcher(width, height);
        int offset = 0;
        Assert.True(stitcher.Add(Viewport(page, width, height, header, offset)));
        while (offset + (height - header) < pageHeight)
        {
            offset = Math.Min(offset + step, pageHeight - (height - header));
            Assert.True(stitcher.Add(Viewport(page, width, height, header, offset)));
        }
        Assert.False(stitcher.Add(Viewport(page, width, height, header, offset))); // fim: nada mudou

        BitmapSource result = stitcher.Build();
        Assert.Equal(header + pageHeight, result.PixelHeight);

        var pixels = new byte[width * 4 * result.PixelHeight];
        result.CopyPixels(pixels, width * 4, 0);
        for (int y = 0; y < pageHeight; y++)
            Assert.Equal(RowColor(y), pixels[(header + y) * width * 4 + 8]);
    }

    private static byte RowColor(int pageRow) => (byte)((pageRow * 37 + pageRow / 7 * 11) % 251);

    /// <summary>Cada linha da página tem um padrão próprio (não lisa), para o alinhamento ser inequívoco.</summary>
    private static byte[] MakePage(int width, int height)
    {
        var page = new byte[width * 4 * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                page[i] = (byte)(x % 3 == 2 ? RowColor(y) : x * 5);
                page[i + 1] = RowColor(y);
                page[i + 2] = (byte)(255 - RowColor(y));
                page[i + 3] = 255;
            }
        return page;
    }

    private static byte[] Viewport(byte[] page, int width, int height, int header, int offset)
    {
        int stride = width * 4;
        var frame = new byte[stride * height];
        for (int y = 0; y < header; y++)
            for (int x = 0; x < stride; x += 4)
            {
                frame[y * stride + x] = 200;
                frame[y * stride + x + 1] = (byte)(x / 4 % 2 == 0 ? 30 : 90); // cabeçalho com detalhe, igual em todo quadro
                frame[y * stride + x + 3] = 255;
            }
        Buffer.BlockCopy(page, offset * stride, frame, header * stride, (height - header) * stride);
        return frame;
    }
}
