using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Airglow.App;

public sealed class TrayIcon : IDisposable
{
    private static readonly Color Accent = Color.FromArgb(0x4F, 0xB2, 0xBD);

    private readonly LiveState _live;
    private readonly NotifyIcon _icon;
    private readonly Icon _glyph;

    public TrayIcon(LiveState live, Action open, Action quit)
    {
        _live = live;
        _glyph = DrawGlyph();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => open());
        menu.Items.Add("Quit", null, (_, _) => quit());
        _icon = new NotifyIcon { Icon = _glyph, ContextMenuStrip = menu, Visible = true, Text = Tooltip(live) };
        _icon.DoubleClick += (_, _) => open();
        _live.Changed += OnChanged;
    }

    internal static string Tooltip(LiveState live)
    {
        if (!live.Connected || live.State is not { } state)
            return "airglow — engine not running";
        return $"CPU {Format(state, "cpu.package")} °C · GPU {Format(state, "gpu.core")} °C";
    }

    private static string Format(Core.Engine.EngineState state, string id) =>
        state.Temperatures.FirstOrDefault(t => t.Id == id)?.Celsius is { } c ? c.ToString("0") : "—";

    private void OnChanged()
    {
        var text = Tooltip(_live);
        if (text != _icon.Text)
            _icon.Text = text;
    }

    private static Icon DrawGlyph()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var glow = new SolidBrush(Color.FromArgb(56, Accent));
            g.FillEllipse(glow, 1, 1, 30, 30);
            using var blade = new SolidBrush(Accent);
            for (var i = 0; i < 4; i++)
            {
                var state = g.Save();
                g.TranslateTransform(16, 16);
                g.RotateTransform(i * 90);
                g.FillEllipse(blade, -3.5f, -13f, 7f, 11f);
                g.Restore(state);
            }

            g.FillEllipse(blade, 12.5f, 12.5f, 7f, 7f);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _live.Changed -= OnChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _glyph.Dispose();
    }
}
