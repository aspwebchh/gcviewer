using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using gcviewer.Models;

namespace gcviewer.Controls;

public sealed class SimpleChart : FrameworkElement
{
    private const int MarkerPointLimit = 150;

    private static readonly Color[] Palette = ChartPalette.Colors;
    private static readonly FontFamily ChartFont = new("Segoe UI, Microsoft YaHei UI");
    private static readonly Typeface NormalTypeface = new(ChartFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBoldTypeface = new(ChartFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush TitleBrush = Frozen(Color.FromRgb(31, 37, 48));
    private static readonly Brush LegendBrush = Frozen(Color.FromRgb(58, 70, 89));
    private static readonly Brush MutedBrush = Frozen(Color.FromRgb(106, 119, 138));
    private static readonly Brush HiddenLegendBrush = Frozen(Color.FromRgb(176, 185, 198));
    private static readonly Pen AxisPen = FrozenPen(Color.FromRgb(213, 220, 229));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(238, 241, 245));
    private static readonly Pen SliceSeparatorPen = FrozenPen(Colors.White);

    private double legendBottom;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series),
        typeof(IEnumerable),
        typeof(SimpleChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(ChartKind),
        typeof(SimpleChart),
        new FrameworkPropertyMetadata(ChartKind.Line, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(SimpleChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty XAxisTitleProperty = DependencyProperty.Register(
        nameof(XAxisTitle),
        typeof(string),
        typeof(SimpleChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty YAxisTitleProperty = DependencyProperty.Register(
        nameof(YAxisTitle),
        typeof(string),
        typeof(SimpleChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty YAxisMinimumProperty = DependencyProperty.Register(
        nameof(YAxisMinimum),
        typeof(double),
        typeof(SimpleChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty YAxisMaximumProperty = DependencyProperty.Register(
        nameof(YAxisMaximum),
        typeof(double),
        typeof(SimpleChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueSuffixProperty = DependencyProperty.Register(
        nameof(ValueSuffix),
        typeof(string),
        typeof(SimpleChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly List<HitTarget> pointHits = new();
    private readonly List<LegendHit> legendHits = new();

    public IEnumerable? Series
    {
        get => (IEnumerable?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public ChartKind Kind
    {
        get => (ChartKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string XAxisTitle
    {
        get => (string)GetValue(XAxisTitleProperty);
        set => SetValue(XAxisTitleProperty, value);
    }

    public string YAxisTitle
    {
        get => (string)GetValue(YAxisTitleProperty);
        set => SetValue(YAxisTitleProperty, value);
    }

    public double YAxisMinimum
    {
        get => (double)GetValue(YAxisMinimumProperty);
        set => SetValue(YAxisMinimumProperty, value);
    }

    public double YAxisMaximum
    {
        get => (double)GetValue(YAxisMaximumProperty);
        set => SetValue(YAxisMaximumProperty, value);
    }

    public string ValueSuffix
    {
        get => (string)GetValue(ValueSuffixProperty);
        set => SetValue(ValueSuffixProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(
            double.IsInfinity(availableSize.Width) ? 420 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 280 : availableSize.Height);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        pointHits.Clear();
        legendHits.Clear();

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 80 || bounds.Height < 80)
        {
            return;
        }

        // 透明背景仍需绘制，保证空白区域也能命中鼠标事件以显示 tooltip。
        context.DrawRectangle(Brushes.Transparent, null, bounds);
        var titleRight = DrawTitle(context);

        var series = GetChartSeries().ToList();
        DrawLegend(context, series, titleRight);

        if (series.All(item => !item.IsVisible || item.Points.Count == 0))
        {
            DrawCenteredText(context, "无可展示数据", bounds, 14, MutedBrush);
            return;
        }

        if (Kind == ChartKind.Donut)
        {
            DrawDonut(context, series);
        }
        else if (Kind == ChartKind.Bar)
        {
            DrawBar(context, series);
        }
        else
        {
            DrawLineOrArea(context, series);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var position = e.GetPosition(this);
        var legend = legendHits.FirstOrDefault(hit => hit.Bounds.Contains(position));
        if (legend is not null)
        {
            Cursor = Cursors.Hand;
            ToolTip = $"点击切换：{legend.Series.Name}";
            return;
        }

        Cursor = Cursors.Arrow;
        var target = pointHits
            .Select(hit => new { Hit = hit, Distance = (hit.Center - position).Length })
            .Where(item => item.Distance <= item.Hit.Radius)
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
        ToolTip = target?.Hit.Point.Tooltip;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        var position = e.GetPosition(this);
        var legend = legendHits.FirstOrDefault(hit => hit.Bounds.Contains(position));
        if (legend is null)
        {
            return;
        }

        legend.Series.IsVisible = !legend.Series.IsVisible;
        InvalidateVisual();
    }

    private List<ChartSeries> GetChartSeries()
    {
        if (Series is null)
        {
            return new List<ChartSeries>();
        }

        return Series.Cast<object>().OfType<ChartSeries>().ToList();
    }

    private Rect PlotArea()
    {
        var top = Math.Max(62d, legendBottom + 30);
        var bottom = 58d;
        var left = 62d;
        var right = 24d;

        if (Kind == ChartKind.Donut)
        {
            left = 24d;
            right = 160d;
            bottom = 24d;
        }
        else if (Kind == ChartKind.Bar)
        {
            bottom = 86d;
        }

        return new Rect(left, top, Math.Max(10, ActualWidth - left - right), Math.Max(10, ActualHeight - top - bottom));
    }

    private double DrawTitle(DrawingContext context)
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            return 0;
        }

        var title = MakeText(Title, 15, TitleBrush, FontWeights.SemiBold);
        context.DrawText(title, new Point(4, 4));
        return 4 + title.Width;
    }

    private void DrawLegend(DrawingContext context, IReadOnlyList<ChartSeries> series, double titleRight)
    {
        var x = titleRight > 0 ? titleRight + 24 : 4d;
        var y = 6d;
        legendBottom = 0;

        foreach (var item in series.Where(item => item.Points.Count > 0))
        {
            var label = MakeText(item.Name, 12, item.IsVisible ? LegendBrush : HiddenLegendBrush, FontWeights.Normal);
            var width = label.Width + 22;
            if (x + width > ActualWidth - 4 && x > 4)
            {
                x = 4;
                y += 22;
            }

            var marker = new Rect(x, y + 3, 10, 10);
            context.DrawRoundedRectangle(item.IsVisible ? item.Stroke : Brushes.Transparent, new Pen(item.Stroke, 1.5), marker, 5, 5);
            context.DrawText(label, new Point(x + 16, y - 1));

            legendHits.Add(new LegendHit(new Rect(x - 3, y - 3, width + 6, 22), item));
            legendBottom = y + 16;
            x += width + 14;
        }
    }

    private void DrawLineOrArea(DrawingContext context, IReadOnlyList<ChartSeries> series)
    {
        var visible = series.Where(item => item.IsVisible && item.Points.Count > 0).ToList();
        var allPoints = visible.SelectMany(item => item.Points).ToList();
        var plot = PlotArea();
        var minX = allPoints.Min(point => point.X);
        var maxX = allPoints.Max(point => point.X);
        var dataMinY = allPoints.Min(point => point.Y);
        var dataMaxY = allPoints.Max(point => point.Y);
        var minY = Math.Min(0, dataMinY);
        var maxY = dataMaxY;
        if (!double.IsNaN(YAxisMinimum) && !double.IsInfinity(YAxisMinimum))
        {
            minY = Math.Min(dataMinY, YAxisMinimum);
        }

        if (!double.IsNaN(YAxisMaximum) && !double.IsInfinity(YAxisMaximum) && YAxisMaximum > minY)
        {
            maxY = Math.Max(dataMaxY, YAxisMaximum);
        }

        if (Math.Abs(maxX - minX) < 0.001)
        {
            maxX = minX + 1;
        }

        if (Math.Abs(maxY - minY) < 0.001)
        {
            maxY = minY + 1;
        }

        DrawAxes(context, plot, minX, maxX, minY, maxY, true);

        foreach (var item in visible)
        {
            var mapped = item.Points
                .Select(point => new
                {
                    Point = point,
                    Screen = Map(point.X, point.Y, plot, minX, maxX, minY, maxY)
                })
                .ToList();

            if (mapped.Count == 0)
            {
                continue;
            }

            if (Kind == ChartKind.Area && mapped.Count > 1)
            {
                var area = new StreamGeometry();
                using (var geometry = area.Open())
                {
                    var baseline = Map(mapped[0].Point.X, minY, plot, minX, maxX, minY, maxY);
                    geometry.BeginFigure(baseline, true, true);
                    foreach (var point in mapped)
                    {
                        geometry.LineTo(point.Screen, true, false);
                    }

                    var endBaseline = Map(mapped[^1].Point.X, minY, plot, minX, maxX, minY, maxY);
                    geometry.LineTo(endBaseline, true, false);
                }

                area.Freeze();
                context.DrawGeometry(item.Fill, null, area);
            }

            var line = new StreamGeometry();
            using (var geometry = line.Open())
            {
                geometry.BeginFigure(mapped[0].Screen, false, false);
                foreach (var point in mapped.Skip(1))
                {
                    geometry.LineTo(point.Screen, true, false);
                }
            }

            line.Freeze();
            var linePen = new Pen(item.Stroke, 1.8) { LineJoin = PenLineJoin.Round };
            linePen.Freeze();
            context.DrawGeometry(null, linePen, line);

            var drawMarkers = mapped.Count <= MarkerPointLimit;
            var markerPen = new Pen(item.Stroke, 1.5);
            markerPen.Freeze();
            foreach (var point in mapped)
            {
                pointHits.Add(new HitTarget(point.Screen, point.Point, drawMarkers ? 9 : 6));
                if (drawMarkers)
                {
                    context.DrawEllipse(Brushes.White, markerPen, point.Screen, 3, 3);
                }
            }
        }
    }

    private void DrawBar(DrawingContext context, IReadOnlyList<ChartSeries> series)
    {
        var visible = series.Where(item => item.IsVisible && item.Points.Count > 0).ToList();
        var allPoints = visible.SelectMany(item => item.Points).ToList();
        var plot = PlotArea();
        var maxY = allPoints.Select(point => point.Y).DefaultIfEmpty(0).Max();
        if (maxY <= 0)
        {
            maxY = 1;
        }

        DrawAxes(context, plot, 0, Math.Max(1, allPoints.Count), 0, maxY, false);

        var groupCount = visible.Max(item => item.Points.Count);
        var groupWidth = plot.Width / Math.Max(1, groupCount);
        var slotWidth = groupWidth / Math.Max(1, visible.Count);
        // 柱子过密时改为无描边的细实线，避免强制最小宽度导致柱子互相覆盖成一整块。
        var dense = slotWidth < 4;
        var barWidth = dense ? Math.Max(1, slotWidth) : Math.Min(56, slotWidth * 0.62);

        for (var seriesIndex = 0; seriesIndex < visible.Count; seriesIndex++)
        {
            var item = visible[seriesIndex];
            var barPen = dense ? null : new Pen(item.Stroke, 1);
            barPen?.Freeze();
            for (var pointIndex = 0; pointIndex < item.Points.Count; pointIndex++)
            {
                var point = item.Points[pointIndex];
                var x = plot.Left + pointIndex * groupWidth + groupWidth / 2 - (visible.Count * barWidth) / 2 + seriesIndex * barWidth;
                var height = point.Y / maxY * plot.Height;
                var bar = new Rect(x, plot.Bottom - height, barWidth, height);
                if (dense)
                {
                    context.DrawRectangle(item.Stroke, null, bar);
                }
                else
                {
                    context.DrawRoundedRectangle(item.Fill, barPen, bar, 2, 2);
                }

                pointHits.Add(new HitTarget(new Point(bar.Left + bar.Width / 2, bar.Top), point, Math.Max(12, bar.Width)));

                if (groupCount <= 12 && seriesIndex == 0)
                {
                    var labelBounds = new Rect(
                        plot.Left + pointIndex * groupWidth + 2,
                        plot.Bottom + 7,
                        Math.Max(10, groupWidth - 4),
                        34);
                    DrawWrappedLabel(context, point.Label, labelBounds, 10, 2);
                }
            }
        }
    }

    private void DrawDonut(DrawingContext context, IReadOnlyList<ChartSeries> series)
    {
        var points = series
            .Where(item => item.IsVisible)
            .SelectMany(item => item.Points.Select(point => new { Series = item, Point = point }))
            .Where(item => item.Point.Y > 0)
            .ToList();

        if (points.Count == 0)
        {
            DrawCenteredText(context, "无可展示数据", new Rect(0, 0, ActualWidth, ActualHeight), 14, MutedBrush);
            return;
        }

        var plot = PlotArea();
        var center = new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2);
        var radius = Math.Min(plot.Width, plot.Height) / 2 * 0.82;
        var inner = radius * 0.58;
        var total = points.Sum(item => item.Point.Y);
        var angle = -90d;

        for (var i = 0; i < points.Count; i++)
        {
            var item = points[i];
            var sweep = Math.Max(0.1, item.Point.Y / total * 360d);
            var color = Palette[i % Palette.Length];
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            var slice = CreateDonutSlice(center, radius, inner, angle, Math.Min(359.9, sweep));
            context.DrawGeometry(brush, SliceSeparatorPen, slice);

            var midAngle = angle + sweep / 2;
            var hitPoint = PointOnCircle(center, (radius + inner) / 2, midAngle);
            pointHits.Add(new HitTarget(hitPoint, item.Point, radius / 2));
            angle += sweep;
        }

        DrawCenteredText(context, $"{total:N0}\n总次数", new Rect(center.X - inner, center.Y - inner / 2, inner * 2, inner), 13, TitleBrush);

        var legendX = Math.Min(ActualWidth - 145, plot.Right + 24);
        var legendY = plot.Top;
        for (var i = 0; i < points.Count; i++)
        {
            var item = points[i];
            var color = Palette[i % Palette.Length];
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            context.DrawRoundedRectangle(brush, null, new Rect(legendX, legendY + i * 22 + 4, 10, 10), 5, 5);
            context.DrawText(MakeText($"{TrimText(item.Point.Label, 9)} {item.Point.Y:N0}", 11, LegendBrush, FontWeights.Normal),
                new Point(legendX + 16, legendY + i * 22));
        }
    }

    private void DrawAxes(DrawingContext context, Rect plot, double minX, double maxX, double minY, double maxY, bool timeAxis)
    {
        for (var i = 0; i <= 4; i++)
        {
            var ratio = i / 4d;
            var y = plot.Bottom - ratio * plot.Height;
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var value = minY + ratio * (maxY - minY);
            var text = MakeText(FormatValue(value), 10, MutedBrush, FontWeights.Normal);
            context.DrawText(text, new Point(Math.Max(2, plot.Left - text.Width - 8), y - text.Height / 2));
        }

        for (var i = 0; i <= 4; i++)
        {
            var ratio = i / 4d;
            var x = plot.Left + ratio * plot.Width;
            if (i > 0)
            {
                context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            }

            var value = minX + ratio * (maxX - minX);
            var label = timeAxis ? GcText.FormatRelativeTime(value) : "";
            if (!string.IsNullOrWhiteSpace(label))
            {
                var text = MakeText(label, 10, MutedBrush, FontWeights.Normal);
                var textX = Math.Clamp(x - text.Width / 2, 2, Math.Max(2, ActualWidth - text.Width - 2));
                context.DrawText(text, new Point(textX, plot.Bottom + 8));
            }
        }

        context.DrawLine(AxisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        context.DrawLine(AxisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));

        if (!string.IsNullOrWhiteSpace(XAxisTitle))
        {
            var text = MakeText(XAxisTitle, 11, MutedBrush, FontWeights.Normal);
            context.DrawText(text, new Point(plot.Left + plot.Width / 2 - text.Width / 2, ActualHeight - 20));
        }

        if (!string.IsNullOrWhiteSpace(YAxisTitle))
        {
            context.DrawText(MakeText(YAxisTitle, 11, MutedBrush, FontWeights.Normal), new Point(4, plot.Top - 24));
        }
    }

    private Point Map(double x, double y, Rect plot, double minX, double maxX, double minY, double maxY)
    {
        var px = plot.Left + (x - minX) / (maxX - minX) * plot.Width;
        var py = plot.Bottom - (y - minY) / (maxY - minY) * plot.Height;
        return new Point(px, py);
    }

    private StreamGeometry CreateDonutSlice(Point center, double outerRadius, double innerRadius, double startAngle, double sweepAngle)
    {
        var outerStart = PointOnCircle(center, outerRadius, startAngle);
        var outerEnd = PointOnCircle(center, outerRadius, startAngle + sweepAngle);
        var innerStart = PointOnCircle(center, innerRadius, startAngle);
        var innerEnd = PointOnCircle(center, innerRadius, startAngle + sweepAngle);
        var largeArc = sweepAngle > 180;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(outerStart, true, true);
            context.ArcTo(outerEnd, new Size(outerRadius, outerRadius), 0, largeArc, SweepDirection.Clockwise, true, false);
            context.LineTo(innerEnd, true, false);
            context.ArcTo(innerStart, new Size(innerRadius, innerRadius), 0, largeArc, SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }

    private FormattedText MakeText(string text, double size, Brush brush, FontWeight weight)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            weight == FontWeights.Normal ? NormalTypeface : SemiBoldTypeface,
            size,
            brush,
            dpi.PixelsPerDip);
    }

    private void DrawCenteredText(DrawingContext context, string text, Rect bounds, double size, Brush brush)
    {
        var lines = text.Split('\n');
        var totalHeight = lines.Length * (size + 4);
        var y = bounds.Top + bounds.Height / 2 - totalHeight / 2;
        foreach (var line in lines)
        {
            var formatted = MakeText(line, size, brush, FontWeights.Normal);
            context.DrawText(formatted, new Point(bounds.Left + bounds.Width / 2 - formatted.Width / 2, y));
            y += size + 6;
        }
    }

    private void DrawWrappedLabel(DrawingContext context, string text, Rect bounds, double size, int maxLines)
    {
        var lines = WrapLabel(text, Math.Max(20, bounds.Width), size, maxLines);
        var y = bounds.Top;
        foreach (var line in lines)
        {
            var formatted = MakeText(line, size, MutedBrush, FontWeights.Normal);
            var x = bounds.Left + bounds.Width / 2 - formatted.Width / 2;
            context.DrawText(formatted, new Point(Math.Max(bounds.Left, x), y));
            y += size + 3;
        }
    }

    private List<string> WrapLabel(string text, double maxWidth, double size, int maxLines)
    {
        var remaining = text.Trim();
        var lines = new List<string>();

        while (remaining.Length > 0 && lines.Count < maxLines)
        {
            var fit = FindFittingLength(remaining, maxWidth, size);
            if (fit <= 0)
            {
                break;
            }

            if (fit < remaining.Length)
            {
                var prefix = remaining[..fit];
                var breakAt = prefix.LastIndexOf(' ');
                if (breakAt > 4)
                {
                    fit = breakAt;
                }
            }

            lines.Add(remaining[..fit].Trim());
            remaining = remaining[fit..].TrimStart();
        }

        if (remaining.Length > 0 && lines.Count > 0)
        {
            var last = lines[^1].TrimEnd();
            while (last.Length > 0 && MakeText(last + "…", size, MutedBrush, FontWeights.Normal).Width > maxWidth)
            {
                last = last[..^1].TrimEnd();
            }

            lines[^1] = last.Length == 0 ? "…" : last + "…";
        }

        return lines;
    }

    private int FindFittingLength(string text, double maxWidth, double size)
    {
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var formatted = MakeText(text[..mid], size, MutedBrush, FontWeights.Normal);
            if (formatted.Width <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private string FormatValue(double value)
    {
        var suffix = ValueSuffix ?? "";
        if (Math.Abs(value) >= 1000)
        {
            return $"{value:0,0}{suffix}";
        }

        if (Math.Abs(value) >= 100)
        {
            return $"{value:0}{suffix}";
        }

        if (Math.Abs(value) >= 10)
        {
            return $"{value:0.#}{suffix}";
        }

        return $"{value:0.##}{suffix}";
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color)
    {
        var pen = new Pen(Frozen(color), 1);
        pen.Freeze();
        return pen;
    }

    private static string TrimText(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(0, maxLength - 1)] + "…";
    }

    private sealed record HitTarget(Point Center, ChartPoint Point, double Radius);
    private sealed record LegendHit(Rect Bounds, ChartSeries Series);
}
