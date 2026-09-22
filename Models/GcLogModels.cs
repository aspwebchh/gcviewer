using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace gcviewer.Models;

public sealed class GcParseProgress
{
    public long LinesRead { get; init; }
    public long BytesRead { get; init; }
    public long TotalBytes { get; init; }

    public double Percent => TotalBytes <= 0 ? 0 : Math.Min(100, BytesRead * 100d / TotalBytes);
}

public sealed class GcParseResult
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public long FileSizeBytes { get; init; }
    public long LineCount { get; init; }
    public DateTime ParsedAt { get; init; } = DateTime.Now;
    public List<GcEvent> Events { get; init; } = new();
    public List<GcCompletionRecord> CompletionRecords { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public sealed class GcCompletionRecord
{
    public int Id { get; init; }
    public double EndSeconds { get; init; }
    public required string NameRaw { get; init; }
    public required string TypeRaw { get; init; }
    public required string CauseRaw { get; init; }
    public double? HeapBeforeMb { get; init; }
    public double? HeapAfterMb { get; init; }
    public double? HeapBeforePercent { get; init; }
    public double? HeapAfterPercent { get; init; }
    public double? MaxCapacityMb { get; init; }
    public double? ExplicitReclaimedMb { get; init; }
    public double DurationMs { get; init; }

    public bool IsPause => NameRaw.StartsWith("Pause", StringComparison.OrdinalIgnoreCase)
                           || NameRaw.StartsWith("Full GC", StringComparison.OrdinalIgnoreCase)
                           || TypeRaw.Equals("Full", StringComparison.OrdinalIgnoreCase);
    public bool IsConcurrent => NameRaw.StartsWith("Concurrent", StringComparison.OrdinalIgnoreCase);
    public double? ReclaimedMb => ExplicitReclaimedMb
        ?? (HeapBeforeMb.HasValue && HeapAfterMb.HasValue
            ? Math.Max(0, HeapBeforeMb.Value - HeapAfterMb.Value)
            : null);
}

public sealed class GcEvent
{
    public int Id { get; init; }
    public double FirstSeenSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string TypeRaw { get; set; } = "";
    public string CauseRaw { get; set; } = "";
    public double? HeapBeforeMb { get; set; }
    public double? HeapAfterMb { get; set; }
    public double? HeapHighMb { get; set; }
    public double? HeapBeforePercent { get; set; }
    public double? HeapAfterPercent { get; set; }
    public double? ExplicitReclaimedMb { get; set; }
    public double? DurationMs { get; set; }
    public double? MaxCapacityMb { get; set; }
    public double? SoftMaxCapacityMb { get; set; }
    public int? TenuringThreshold { get; set; }
    public int AllocationStallPeak { get; set; }
    public GcLoadSample? Load { get; set; }
    public GcMetaspaceSample? Metaspace { get; set; }
    public List<GcMmuSample> MmuSamples { get; } = new();
    public List<GcPhase> Phases { get; } = new();
    public List<GcAgeTableRow> AgeTable { get; } = new();
    public Dictionary<string, GcGenerationStats> GenerationStats { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsCompleted => DurationMs.HasValue;
    public double PauseDurationMs => Phases.Where(phase => phase.IsPause).Sum(phase => phase.DurationMs);
    public double ConcurrentDurationMs => Phases.Where(phase => phase.IsConcurrent).Sum(phase => phase.DurationMs);
    public double? ReclaimedMb => ExplicitReclaimedMb
        ?? (HeapBeforeMb.HasValue && HeapAfterMb.HasValue
            ? Math.Max(0, HeapBeforeMb.Value - HeapAfterMb.Value)
            : null);
}

public sealed class GcPhase
{
    public required string NameRaw { get; init; }
    public required string Name { get; init; }
    public required string Generation { get; init; }
    public double EndSeconds { get; init; }
    public double DurationMs { get; init; }
    public bool IsPause => NameRaw.StartsWith("Pause", StringComparison.OrdinalIgnoreCase);
    public bool IsConcurrent => NameRaw.StartsWith("Concurrent", StringComparison.OrdinalIgnoreCase);
    public bool IsGenerationSummary => NameRaw is "Young Generation" or "Old Generation";
}

public sealed class GcGenerationStats
{
    public required string Name { get; init; }
    public double? UsedBeforeMb { get; set; }
    public double? UsedAfterMb { get; set; }
    public double? LiveMb { get; set; }
    public double? GarbageMb { get; set; }
    public double? AllocatedMb { get; set; }
    public double? ReclaimedMb { get; set; }
    public double? PromotedMb { get; set; }
    public double? CompactedMb { get; set; }
}

public sealed class GcMetaspaceSample
{
    public double UsedMb { get; init; }
    public double CommittedMb { get; init; }
    public double? ReservedMb { get; init; }
}

public sealed class GcLoadSample
{
    public double OneMinute { get; init; }
    public double FiveMinutes { get; init; }
    public double FifteenMinutes { get; init; }
}

public sealed class GcMmuSample
{
    public double WindowMs { get; init; }
    public double Percent { get; init; }
}

public sealed class GcAgeTableRow
{
    public required string Name { get; init; }
    public double LiveMb { get; init; }
    public double GarbageMb { get; init; }
}

public sealed class GcReport
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public long FileSizeBytes { get; init; }
    public long LineCount { get; init; }
    public DateTime ParsedAt { get; init; }
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public double SpanSeconds { get; init; }
    public double HeapCapacityLimitGb { get; init; } = double.NaN;
    public double PauseDurationYAxisMinimumMs { get; init; } = double.NaN;
    public double PauseDurationYAxisMaximumMs { get; init; } = double.NaN;
    public int CompletedCycleCount { get; init; }
    public int CompletedEventCount { get; init; }
    public int PauseCompletionCount { get; init; }
    public int ParsedEventCount { get; init; }
    public required string CollectorType { get; init; }
    public required string CollectorDescription { get; init; }
    public required string HealthTitle { get; init; }
    public required string HealthLevel { get; init; }
    public required string HealthSummary { get; init; }
    public List<string> Recommendations { get; init; } = new();
    public ObservableCollection<GcKpi> OverviewKpis { get; init; } = new();
    public ObservableCollection<GcKpi> AdvancedKpis { get; init; } = new();
    public ObservableCollection<GcStatisticRow> TypeStatistics { get; init; } = new();
    public ObservableCollection<GcStatisticRow> CauseStatistics { get; init; } = new();
    public ObservableCollection<GcPhaseStatisticRow> PhaseStatistics { get; init; } = new();
    public ObservableCollection<GcGenerationSummaryRow> GenerationStatistics { get; init; } = new();
    public ObservableCollection<GcEventRow> EventRows { get; init; } = new();
    public ObservableCollection<ChartSeries> HeapBeforeSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> HeapAfterSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> HeapReclaimedSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> DurationSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> PauseSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> CumulativeDurationSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> CauseSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> TypeSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> PhaseTotalSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> PhaseAverageSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> GenerationSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> MetaspaceSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> LoadSeries { get; init; } = new();
    public ObservableCollection<ChartSeries> MmuSeries { get; init; } = new();
}

public sealed class GcKpi
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public string Description { get; init; } = "";
    public string Tone { get; init; } = "Neutral";
}

public sealed class GcStatisticRow
{
    public required string Name { get; init; }
    public int Count { get; init; }
    public double TotalDurationMs { get; init; }
    public double AverageDurationMs { get; init; }
    public double DurationPercent { get; init; }
    public string TotalDurationText => GcText.FormatDuration(TotalDurationMs);
    public string AverageDurationText => GcText.FormatMs(AverageDurationMs);
    public string DurationPercentText => $"{DurationPercent:0.##}%";
}

public sealed class GcPhaseStatisticRow
{
    public required string Name { get; init; }
    public int Count { get; init; }
    public double TotalMs { get; init; }
    public double AverageMs { get; init; }
    public double MaxMs { get; init; }
    public string TotalText => GcText.FormatDuration(TotalMs);
    public string AverageText => GcText.FormatMs(AverageMs);
    public string MaxText => GcText.FormatMs(MaxMs);
}

public sealed class GcGenerationSummaryRow
{
    public required string Name { get; init; }
    public int Samples { get; init; }
    public double? PeakUsedMb { get; init; }
    public double? LatestUsedMb { get; init; }
    public double? TotalReclaimedMb { get; init; }
    public string PeakUsedText => GcText.FormatMb(PeakUsedMb);
    public string LatestUsedText => GcText.FormatMb(LatestUsedMb);
    public string TotalReclaimedText => GcText.FormatMb(TotalReclaimedMb);
}

public sealed class GcEventRow
{
    public int Id { get; init; }
    public required string TimeText { get; init; }
    public required string TypeText { get; init; }
    public required string CauseText { get; init; }
    public string HeapBeforeText { get; init; } = GcText.NotReported;
    public string HeapAfterText { get; init; } = GcText.NotReported;
    public string ReclaimedText { get; init; } = GcText.NotReported;
    public string DurationText { get; init; } = GcText.NotReported;
    public string PauseText { get; init; } = GcText.NotReported;
    public string ConcurrentText { get; init; } = GcText.NotReported;
    public string MetaspaceText { get; init; } = GcText.NotReported;
    public int AllocationStallPeak { get; init; }
}

public enum ChartKind
{
    Line,
    Area,
    Bar,
    Donut
}

public sealed class ChartSeries : INotifyPropertyChanged
{
    private bool isVisible = true;

    public required string Name { get; init; }
    public Brush Stroke { get; init; } = Brushes.SteelBlue;
    public Brush Fill { get; init; } = Brushes.SteelBlue;
    public ObservableCollection<ChartPoint> Points { get; init; } = new();

    public bool IsVisible
    {
        get => isVisible;
        set
        {
            if (isVisible == value)
            {
                return;
            }

            isVisible = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ChartPoint
{
    public double X { get; init; }
    public double Y { get; init; }
    public required string Label { get; init; }
    public required string Tooltip { get; init; }
}

public static class GcText
{
    public const string NotReported = "未报告";

    public static string FormatMb(double? value)
    {
        if (!value.HasValue)
        {
            return NotReported;
        }

        if (value.Value >= 1024 * 1024)
        {
            return $"{value.Value / 1024 / 1024:0.##} TB";
        }

        if (value.Value >= 1024)
        {
            return $"{value.Value / 1024:0.##} GB";
        }

        return $"{value.Value:0.##} MB";
    }

    public static string FormatMs(double? value)
    {
        if (!value.HasValue)
        {
            return NotReported;
        }

        return value.Value >= 1000 ? $"{value.Value / 1000:0.###} 秒" : $"{value.Value:0.###} ms";
    }

    public static string FormatDuration(double? milliseconds)
    {
        if (!milliseconds.HasValue)
        {
            return NotReported;
        }

        var value = milliseconds.Value;
        if (value < 1000)
        {
            return $"{value:0.###} ms";
        }

        var span = TimeSpan.FromMilliseconds(value);
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours} 小时 {span.Minutes} 分 {span.Seconds} 秒";
        }

        if (span.TotalMinutes >= 1)
        {
            return $"{span.Minutes} 分 {span.Seconds} 秒 {span.Milliseconds} ms";
        }

        return $"{span.Seconds} 秒 {span.Milliseconds} ms";
    }

    public static string FormatRelativeTime(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(seconds);
        return $"+{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d / 1024d:0.##} GB";
        }

        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.##} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024d:0.##} KB";
        }

        return $"{bytes} B";
    }
}
