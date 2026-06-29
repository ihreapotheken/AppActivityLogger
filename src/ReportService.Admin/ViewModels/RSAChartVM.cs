namespace ReportService.Admin.ViewModels;

/// <summary>One slice of a donut chart: a label, its numeric value, and the categorical palette
/// class (<c>chart-c1</c>…<c>chart-c6</c>) that colours both the ring segment and its legend key.</summary>
public sealed record RSADonutSlice(string Label, double Value, string ColorClass);

/// <summary>Model for the reusable <c>_DonutChart</c> partial — a proportion ring with a centred
/// total and a value/percent legend. Zero-value slices are skipped; an all-zero set renders an
/// empty-state note instead.</summary>
public sealed record RSADonutChartVM(
    IReadOnlyList<RSADonutSlice> Slices,
    string CenterLabel = "total",
    string AriaLabel = "Proportion donut chart");

/// <summary>One point on a line chart (x label + y value).</summary>
public sealed record RSALinePoint(string Label, double Value);

/// <summary>Model for the reusable <c>_LineChart</c> partial — a single-series trend with an area
/// fill, value dots, y-axis gridlines + ticks, and x-axis labels. <paramref name="ValueNoun"/> is
/// appended in each dot's hover tooltip (e.g. "reports" → "12 reports"). <paramref name="LabelStep"/>
/// thins the x-axis labels on long ranges. <paramref name="ViewHeight"/> is the SVG viewBox height
/// (width is fixed at 720); since the chart scales uniformly to its container, raising it makes the
/// chart taller — bump it for charts laid out in narrow multi-column rows so they don't render squat.</summary>
public sealed record RSALineChartVM(
    IReadOnlyList<RSALinePoint> Points,
    string ValueNoun = "",
    int LabelStep = 1,
    string AriaLabel = "Trend line chart",
    int ViewHeight = 170);
