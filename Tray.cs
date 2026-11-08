using PyDisplay.Sensors;
using PyDisplay.UI;
using WinForms = System.Windows.Forms;

namespace PyDisplay;

/// <summary>Tray icon: double-click shows/hides the overlay; tooltip can show live GPU/CPU.</summary>
public sealed class Tray : IDisposable
{
    private readonly AppHost _h;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _showHide;

    public Tray(AppHost host)
    {
        _h = host;
        _icon = new WinForms.NotifyIcon { Text = "PyDisplay", Visible = true, Icon = LoadIcon() };
        var menu = new WinForms.ContextMenuStrip();
        _showHide = new WinForms.ToolStripMenuItem("Hide overlay", null, (_, _) => Toggle());
        menu.Items.Add(_showHide);
        menu.Items.Add(new WinForms.ToolStripMenuItem("Settings", null, (_, _) => _h.OpenSettings("look")));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Tools", null, (_, _) => _h.OpenTools("memory")));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Exit", null, (_, _) => _h.Exit()));
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => Toggle();
    }

    private void Toggle()
    {
        if (_h.Overlay is { IsVisible: true }) _h.CloseOverlay(); else _h.ShowOverlay();
        Refresh();
    }

    public void Refresh() => _showHide.Text = _h.Overlay is { IsVisible: true } ? "Hide overlay" : "Show overlay";

    public void Update(Snapshot s, AppSettings set)
    {
        string text = TooltipText(s, set);
        if (_icon.Text != text) _icon.Text = text;
    }

    public static string TooltipText(Snapshot s, AppSettings set)
    {
        string text = set.TrayShowGpu
            ? $"PyDisplay · GPU {Fmt.Pct(s.GpuLoad)} · CPU {Fmt.Pct(s.CpuLoad)} · RAM {Fmt.Pct(s.RamPct)}"
            : "PyDisplay";
        return text.Length > 63 ? text[..63] : text;   // Windows tooltip limit
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (res != null)
                using (res.Stream) return new System.Drawing.Icon(res.Stream, WinForms.SystemInformation.SmallIconSize);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
