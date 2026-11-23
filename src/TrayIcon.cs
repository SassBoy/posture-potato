using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PosturePotato;

/// <summary>Lives in the taskbar's hidden icons. Left click: settings. Right click: menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _image;

    public TrayIcon(App app)
    {
        _app = app;
        _image = IconFactory.Create(Forms.SystemInformation.SmallIconSize);
        _icon = new Forms.NotifyIcon { Icon = _image, Text = "Posture Potato", Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right) ShowMenu();
            else if (e.Button == Forms.MouseButtons.Left) _app.OpenSettings();
        };
        _icon.BalloonTipClicked += (_, _) => _app.OpenSettings();
    }

    public void Refresh()
    {
        var text = "Posture Potato\n" + _app.Scheduler.Status;
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void ShowHello() =>
        _icon.ShowBalloonTip(6000, "Posture Potato is running",
            $"I'll check your posture every {Settings.FormatMinutes(_app.Settings.IntervalMinutes)}. " +
            "Find me in the hidden icons on your taskbar.", Forms.ToolTipIcon.None);

    public void ShowMenu()
    {
        var s = _app.Scheduler;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem { Header = s.Status, IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Check now", _app.ShowReminder));

        if (s.IsPaused)
        {
            menu.Items.Add(Item("Resume reminders", s.Resume));
        }
        else
        {
            var pause = new MenuItem { Header = "Pause reminders" };
            pause.Items.Add(Item("For 30 min", () => s.PauseFor(TimeSpan.FromMinutes(30))));
            pause.Items.Add(Item("For 1 hour", () => s.PauseFor(TimeSpan.FromHours(1))));
            pause.Items.Add(Item("Until tomorrow", s.PauseUntilTomorrow));
            menu.Items.Add(pause);
        }

        menu.Items.Add(Item("Settings", _app.OpenSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Posture Potato", _app.Quit));

        // Without this the menu won't close when you click elsewhere.
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource src) Native.SetForegroundWindow(src.Handle);
        };
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}

/// <summary>Renders the potato into PNG-in-ICO bytes, so no image files ship with the app.</summary>
internal static class IconFactory
{
    public static readonly int[] TraySizes = { 16, 20, 24, 32, 40, 48, 64 };
    public static readonly int[] AppSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static Drawing.Icon Create(Drawing.Size size)
    {
        using var ms = new MemoryStream(BuildIco(TraySizes));
        return new Drawing.Icon(ms, size);
    }

    public static byte[] BuildIco(int[] sizes)
    {
        var pngs = sizes.Select(s => s >= 256 ? RenderPng(s) : RenderDib(s)).ToArray();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        return ms.ToArray();
    }

    /// <summary>Classic 32-bit icon entry (BITMAPINFOHEADER + BGRA bottom-up + empty AND mask);
    /// System.Drawing and NotifyIcon don't read PNG entries below 256 px.</summary>
    private static byte[] RenderDib(int size)
    {
        var bmp = RenderBitmap(size);
        var px = new byte[size * size * 4];
        bmp.CopyPixels(px, size * 4, 0);
        for (int i = 0; i < px.Length; i += 4) // premultiplied -> straight alpha
        {
            byte a = px[i + 3];
            if (a is > 0 and < 255)
                for (int k = 0; k < 3; k++) px[i + k] = (byte)Math.Min(255, px[i + k] * 255 / a);
        }

        int maskStride = ((size + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40); w.Write(size); w.Write(size * 2); w.Write((short)1); w.Write((short)32);
        w.Write(0); w.Write(size * size * 4 + maskStride * size); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = size - 1; y >= 0; y--) w.Write(px, y * size * 4, size * 4);
        w.Write(new byte[maskStride * size]);
        return ms.ToArray();
    }

    private static byte[] RenderPng(int size)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(RenderBitmap(size)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static RenderTargetBitmap RenderBitmap(int size)
    {
        var pose = new PotatoPose { Eyes = EyeState.Open, Mouth = MouthState.Smile };
        var sprite = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null,
            PotatoArt.RenderIconSprite(pose), 32 * 4);

        var dv = new DrawingVisual();
        // Whole multiples stay crisp; small sizes are averaged down so the outline survives.
        RenderOptions.SetBitmapScalingMode(dv, size >= 32 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
            dc.DrawImage(sprite, new Rect(0, 0, size, size));
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }
}
