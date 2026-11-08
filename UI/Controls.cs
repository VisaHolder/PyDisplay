using System.Windows;
using System.Windows.Media;

namespace PyDisplay.UI;

/// <summary>Load bar. Blocks = terminal-style segments; otherwise a thin solid bar. Turns amber/red when high.</summary>
public sealed class BlockBar : FrameworkElement
{
    private double _v;
    public double Value { get => _v; set { value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0; if (Math.Abs(value - _v) > 0.0005) { _v = value; InvalidateVisual(); } } }
    public bool Blocks { get; init; } = true;
    public bool Heat { get; init; } = true;          // colour by load (off for network, which has no "full")
    public required Brush Accent { get; set; }
    public required Brush Track { get; init; }
    public required Brush Warn { get; init; }
    public required Brush Hot { get; init; }

    public BlockBar() { SnapsToDevicePixels = true; }

    private Brush Fill => !Heat ? Accent : _v > 0.85 ? Hot : _v > 0.60 ? Warn : Accent;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 2) return;
        if (!Blocks)
        {
            dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), h / 2, h / 2);
            if (_v > 0) dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(h, w * _v), h), h / 2, h / 2);
            return;
        }
        double cell = Math.Max(3, h * 0.75), gap = Math.Max(1.5, h * 0.25);
        int n = Math.Max(1, (int)((w + gap) / (cell + gap)));
        cell = (w - gap * (n - 1)) / n;
        int lit = (int)Math.Round(_v * n);
        if (_v > 0 && lit == 0) lit = 1;   // anything above 0 shows at least one block
        var fill = Fill;
        for (int i = 0; i < n; i++)
            dc.DrawRectangle(i < lit ? fill : Track, null, new Rect(i * (cell + gap), 0, cell, h));
    }
}

/// <summary>60-second line graph with a soft fill. Optional second line (network upload).</summary>
public sealed class Sparkline : FrameworkElement
{
    public const int Capacity = 60;
    private double[] _a = [], _b = [];
    public double Max { get; init; } = 100;        // fixed scale (percent)
    public bool AutoMax { get; init; }             // scale to the data (network/disk)
    public required Brush Stroke { get; set; }
    public Brush? StrokeB { get; set; }
    public required Brush Baseline { get; init; }

    public Sparkline() { SnapsToDevicePixels = false; }

    public void Set(double[] a, double[]? b = null) { _a = a; _b = b ?? []; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        dc.DrawRectangle(Baseline, null, new Rect(0, h - 1, w, 1));
        double max = Max;
        if (AutoMax)
        {
            double m = 0;
            foreach (var v in _a) m = Math.Max(m, v);
            foreach (var v in _b) m = Math.Max(m, v);
            max = Math.Max(m * 1.15, 1);
        }
        if (_b.Length > 1 && StrokeB != null) Draw(dc, _b, StrokeB, max, w, h, false);
        if (_a.Length > 1) Draw(dc, _a, Stroke, max, w, h, true);
    }

    private static void Draw(DrawingContext dc, double[] d, Brush stroke, double max, double w, double h, bool fill)
    {
        double step = w / (Capacity - 1);
        double x0 = w - step * (d.Length - 1);
        Point P(int i) => new(x0 + i * step, h - 1 - Math.Clamp(d[i] / max, 0, 1) * (h - 2));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false, false);
            for (int i = 1; i < d.Length; i++) g.LineTo(P(i), true, true);
        }
        line.Freeze();

        if (fill && stroke is SolidColorBrush sb)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(x0, h - 1), true, true);
                for (int i = 0; i < d.Length; i++) g.LineTo(P(i), false, false);
                g.LineTo(new Point(w, h - 1), false, false);
            }
            area.Freeze();
            var fb = new SolidColorBrush(Color.FromArgb(38, sb.Color.R, sb.Color.G, sb.Color.B));
            fb.Freeze();
            dc.DrawGeometry(fb, null, area);
        }
        var pen = new Pen(stroke, 1.4) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>One small vertical bar per CPU thread.</summary>
public sealed class CoreBars : FrameworkElement
{
    private double[] _v = [];
    public required Brush Accent { get; set; }
    public required Brush Track { get; init; }

    public void Set(double[] v) { _v = v; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        int n = _v.Length;
        if (n == 0 || w < n * 2 || h < 2) return;
        double gap = Math.Max(1, Math.Min(3, w / n * 0.25));
        double bw = (w - gap * (n - 1)) / n;
        for (int i = 0; i < n; i++)
        {
            double x = i * (bw + gap);
            dc.DrawRectangle(Track, null, new Rect(x, 0, bw, h));
            double fh = Math.Clamp(_v[i] / 100, 0, 1) * h;
            if (fh > 0) dc.DrawRectangle(Accent, null, new Rect(x, h - Math.Max(1, fh), bw, Math.Max(1, fh)));
        }
    }
}
