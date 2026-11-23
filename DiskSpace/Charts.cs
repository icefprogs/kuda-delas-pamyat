using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiskSpace;

internal sealed record RingPart(string Label, long Bytes, Brush Brush, Action? Open);

internal static class Plot
{
    public static readonly Color[] Colors = [Color.FromRgb(114, 217, 198), Color.FromRgb(129, 171, 251),
        Color.FromRgb(249, 194, 124), Color.FromRgb(190, 155, 242), Color.FromRgb(164, 211, 121), Color.FromRgb(242, 155, 176)];

    public static Brush Brush(FrameworkElement owner, string key) => (Brush)owner.FindResource(key);
    public static FormattedText Text(FrameworkElement owner, string text, double size, string brush, FontWeight? weight = null)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, Brush(owner, brush), VisualTreeHelper.GetDpi(owner).PixelsPerDip);

    public static void Center(DrawingContext context, FrameworkElement owner, string text, double size, string color, double x, double y, bool bold = false)
    {
        var label = Text(owner, text, size, color, bold ? FontWeights.SemiBold : null);
        context.DrawText(label, new Point(x - label.Width / 2, y - label.Height / 2));
    }
}

internal sealed class DiskRing(IReadOnlyList<RingPart> parts, string used, string free, bool motion) : FrameworkElement
{
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(double),
        typeof(DiskRing), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }
    private int active = -1;
    private bool initial = true;

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (initial && motion)
        {
            initial = false;
            Dispatcher.BeginInvoke(() => BeginAnimation(RevealProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(480))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }));
        }
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(1, Math.Min(center.X, center.Y) - 20);
        context.DrawEllipse(null, new Pen(Plot.Brush(this, "Line"), 19), center, radius, radius);
        var total = parts.Sum(part => (double)part.Bytes);
        double start = 0;
        for (var index = 0; total > 0 && index < parts.Count; index++)
        {
            var part = parts[index];
            var slice = 360 * part.Bytes / total;
            var end = Math.Min(start + slice - 1.4, 360 * Reveal);
            if (end > start) Arc(context, center, radius, start + .7, end, part.Brush, index == active ? 25 : 19);
            start += slice;
        }
        Plot.Center(context, this, "Занято", 13, "Muted", center.X, center.Y - 33);
        Plot.Center(context, this, used, 33, "Text", center.X, center.Y - 4, true);
        Plot.Center(context, this, free, 13, "Muted", center.X, center.Y + 30);
    }

    private static void Arc(DrawingContext context, Point center, double radius, double start, double end, Brush brush, double width)
    {
        Point Pos(double angle)
        {
            var radians = (angle - 90) * Math.PI / 180;
            return new(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(Pos(start), false, false);
            path.ArcTo(Pos(end), new Size(radius, radius), 0, end - start > 180, SweepDirection.Clockwise, true, false);
        }
        context.DrawGeometry(null, new Pen(brush, width), geometry);
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        var position = args.GetPosition(this);
        var x = position.X - ActualWidth / 2;
        var y = position.Y - ActualHeight / 2;
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 20;
        var angle = (Math.Atan2(y, x) * 180 / Math.PI + 450) % 360;
        var total = parts.Sum(part => (double)part.Bytes);
        int next = -1;
        if (Math.Abs(Math.Sqrt(x * x + y * y) - radius) <= 15 && total > 0)
        {
            double accumulated = 0;
            for (var index = 0; index < parts.Count; index++)
            {
                accumulated += 360 * parts[index].Bytes / total;
                if (angle < accumulated) { next = index; break; }
            }
        }
        if (next == active) return;
        active = next;
        ToolTip = active < 0 ? null : parts[active].Label;
        Cursor = active >= 0 && parts[active].Open is not null ? Cursors.Hand : Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs args) { active = -1; InvalidateVisual(); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        if (active >= 0) parts[active].Open?.Invoke();
    }
}

internal sealed class HistoryPlot(IReadOnlyList<Snapshot> data, Func<long, string> format) : FrameworkElement
{
    protected override void OnRender(DrawingContext draw)
    {
        base.OnRender(draw);
        if (data.Count < 2)
        {
            Plot.Center(draw, this, "График появится после второго замера", 13, "Muted", ActualWidth / 2, ActualHeight / 2);
            return;
        }
        var max = data.Max(item => item.Free);
        var min = data.Min(item => item.Free);
        var range = Math.Max(1, max - min);
        var start = data[0].Time;
        var duration = Math.Max(1, (data[^1].Time - start).TotalSeconds);
        var width = Math.Max(1, ActualWidth - 104);
        var height = Math.Max(1, ActualHeight - 45);
        for (var i = 0; i < 3; i++)
        {
            var y = 13 + height * i / 2;
            draw.DrawLine(new Pen(Plot.Brush(this, "Line"), 1), new Point(82, y), new Point(82 + width, y));
            draw.DrawText(Plot.Text(this, format(max - (max - min) * i / 2), 12, "Muted"), new Point(7, y - 8));
        }
        var prior = new Point();
        for (var i = 0; i < data.Count; i++)
        {
            var dot = new Point(82 + width * (data[i].Time - start).TotalSeconds / duration,
                13 + height * (max - data[i].Free) / range);
            if (i > 0)
            {
                var line = new Pen(Plot.Brush(this, "Accent"), 2);
                if (data[i].Time - data[i - 1].Time > TimeSpan.FromHours(27) || data[i].Skipped != 0 || data[i - 1].Skipped != 0)
                    line.DashStyle = DashStyles.Dash;
                draw.DrawLine(line, prior, dot);
            }
            draw.DrawEllipse(Plot.Brush(this, "Accent"), null, dot, 3, 3);
            prior = dot;
        }
        draw.DrawText(Plot.Text(this, start.ToLocalTime().ToString("dd.MM HH:mm"), 11, "Muted"), new Point(82, ActualHeight - 24));
        var right = Plot.Text(this, data[^1].Time.ToLocalTime().ToString("dd.MM HH:mm"), 11, "Muted");
        draw.DrawText(right, new Point(82 + width - right.Width, ActualHeight - 24));
    }
}

internal sealed class FolderMap(IReadOnlyList<FileRow> rows, Action<FileRow> open, Func<long, string> format) : FrameworkElement
{
    private readonly List<(Rect Bounds, FileRow Row)> targets = [];

    protected override void OnRender(DrawingContext draw)
    {
        base.OnRender(draw);
        targets.Clear();
        var visible = rows.Where(row => row.Bytes > 0).Take(18).ToArray();
        if (visible.Length > 0) Place(draw, visible, 0, visible.Length, new Rect(0, 0, ActualWidth, ActualHeight));
        else Plot.Center(draw, this, "В этой папке пока нет данных для карты", 13, "Muted", ActualWidth / 2, ActualHeight / 2);
    }

    private void Place(DrawingContext draw, FileRow[] rows, int begin, int end, Rect rect)
    {
        if (rect.Width <= 3 || rect.Height <= 3) return;
        if (end - begin == 1)
        {
            var cell = new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
            var tone = Plot.Colors[begin % Plot.Colors.Length];
            draw.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(64, tone.R, tone.G, tone.B)),
                new Pen(new SolidColorBrush(tone), 1), cell, 5, 5);
            draw.PushClip(new RectangleGeometry(cell));
            if (cell.Width >= 48 && cell.Height >= 22)
                draw.DrawText(Plot.Text(this, rows[begin].Name, 13, "Text", FontWeights.SemiBold), new Point(cell.X + 8, cell.Y + 5));
            if (cell.Width >= 65 && cell.Height >= 45)
                draw.DrawText(Plot.Text(this, format(rows[begin].Bytes), 11, "Muted"), new Point(cell.X + 8, cell.Y + 25));
            draw.Pop();
            targets.Add((cell, rows[begin]));
            return;
        }
        var total = rows[begin..end].Sum(row => (double)row.Bytes);
        var cut = begin;
        double sum = 0;
        do { sum += rows[cut++].Bytes; } while (cut < end - 1 && sum < total / 2);
        var ratio = sum / total;
        if (rect.Width >= rect.Height)
        {
            var split = rect.Width * ratio;
            Place(draw, rows, begin, cut, new Rect(rect.X, rect.Y, split, rect.Height));
            Place(draw, rows, cut, end, new Rect(rect.X + split, rect.Y, rect.Width - split, rect.Height));
        }
        else
        {
            var split = rect.Height * ratio;
            Place(draw, rows, begin, cut, new Rect(rect.X, rect.Y, rect.Width, split));
            Place(draw, rows, cut, end, new Rect(rect.X, rect.Y + split, rect.Width, rect.Height - split));
        }
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        var target = targets.FirstOrDefault(item => item.Bounds.Contains(args.GetPosition(this)));
        ToolTip = target.Row is null ? null : target.Row.Path;
        Cursor = target.Row is null ? Cursors.Arrow : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        var target = targets.FirstOrDefault(item => item.Bounds.Contains(args.GetPosition(this)));
        if (target.Row is not null) open(target.Row);
    }
}
