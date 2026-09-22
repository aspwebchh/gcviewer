using System.Collections.ObjectModel;
using System.Windows.Media;
using gcviewer.Models;

namespace gcviewer.Services;

public sealed class GcReportCalculator
{
    private const int MaxDrawPoints = 20_000;

    private sealed record PauseCycleSample(GcEvent Event, double EndSeconds, double DurationMs);

    private static readonly Color[] Palette =
    [
        Color.FromRgb(21, 176, 167),
        Color.FromRgb(15, 37, 86),
        Color.FromRgb(0, 113, 188),
        Color.FromRgb(140, 198, 63),
        Color.FromRgb(194, 70, 66),
        Color.FromRgb(250, 163, 23),
        Color.FromRgb(79, 53, 141),
        Color.FromRgb(48, 116, 185),
        Color.FromRgb(35, 191, 170),
        Color.FromRgb(235, 140, 198)
    ];

    public GcReport Build(GcParseResult source, double? startSeconds = null, double? endSeconds = null)
    {
        var completedCycles = source.Events
            .Where(gc => gc.IsCompleted)
            .OrderBy(gc => gc.EndSeconds)
            .ToList();
        var completedRecords = source.CompletionRecords
            .OrderBy(record => record.EndSeconds)
            .ToList();
        var allTimes = source.Events
            .SelectMany(gc => new[] { gc.FirstSeenSeconds, gc.EndSeconds })
            .Concat(completedRecords.Select(record => record.EndSeconds))
            .ToList();

        var allStart = allTimes.Count == 0 ? 0 : allTimes.Min();
        var allEnd = allTimes.Count == 0 ? 0 : allTimes.Max();
        var effectiveStart = startSeconds ?? allStart;
        var effectiveEnd = endSeconds ?? allEnd;

        var cycles = completedCycles
            .Where(gc => gc.EndSeconds >= effectiveStart && gc.EndSeconds <= effectiveEnd)
            .OrderBy(gc => gc.EndSeconds)
            .ToList();
        var completions = completedRecords
            .Where(record => record.EndSeconds >= effectiveStart && record.EndSeconds <= effectiveEnd)
            .OrderBy(record => record.EndSeconds)
            .ToList();
        var pausePhases = source.Events
            .SelectMany(gc => gc.Phases.Select(phase => new { Event = gc, Phase = phase }))
            .Where(item => item.Phase.IsPause
                           && item.Phase.EndSeconds >= effectiveStart
                           && item.Phase.EndSeconds <= effectiveEnd)
            .ToList();
        var pauseCycles = pausePhases
            .GroupBy(item => item.Event)
            .Select(group => new PauseCycleSample(
                group.Key,
                group.Max(item => item.Phase.EndSeconds),
                group.Sum(item => item.Phase.DurationMs)))
            .OrderBy(sample => sample.EndSeconds)
            .ToList();

        if (cycles.Count == 0 && completions.Count == 0 && pauseCycles.Count == 0)
        {
            return BuildEmptyReport(source, effectiveStart, effectiveEnd);
        }

        var start = effectiveStart;
        var end = effectiveEnd;
        var spanSeconds = Math.Max(0, end - start);
        var totalCycleGcMs = cycles.Sum(gc => gc.DurationMs ?? 0);
        var totalCompletionMs = completions.Sum(record => record.DurationMs);
        var totalPauseMs = pauseCycles.Sum(sample => sample.DurationMs);
        var totalConcurrentMs = cycles.Sum(gc => gc.ConcurrentDurationMs);
        var throughput = spanSeconds <= 0
            ? (double?)null
            : Math.Max(0, (spanSeconds * 1000d - totalPauseMs) * 100d / (spanSeconds * 1000d));
        var maxCycleDurationMs = cycles.Count == 0 ? 0 : cycles.Max(gc => gc.DurationMs ?? 0);
        var averageCycleDurationMs = cycles.Count == 0 ? 0 : cycles.Average(gc => gc.DurationMs ?? 0);
        var pauseCompletionRecords = completions.Where(record => record.IsPause).ToList();
        var averagePauseMs = pauseCycles.Count == 0 ? 0 : pauseCycles.Average(sample => sample.DurationMs);
        var maxPausePhaseMs = pausePhases.Select(item => item.Phase.DurationMs).DefaultIfEmpty(0).Max();
        var totalReclaimedMb = cycles.Sum(gc => gc.ReclaimedMb ?? 0);
        var latestMetaspace = cycles.LastOrDefault(gc => gc.Metaspace is not null)?.Metaspace;
        var allocationStallPeak = source.Events
            .Where(gc => gc.EndSeconds >= effectiveStart && gc.EndSeconds <= effectiveEnd)
            .Select(gc => gc.AllocationStallPeak)
            .DefaultIfEmpty(0)
            .Max();
        var peakHeapMb = cycles
            .Select(gc => gc.HeapHighMb
                ?? new[] { gc.HeapBeforeMb, gc.HeapAfterMb }
                    .Where(value => value.HasValue)
                    .Select(value => value.GetValueOrDefault())
                    .DefaultIfEmpty(0)
                    .Max())
            .DefaultIfEmpty(0)
            .Max();
        var latestAfterHeapMb = cycles.LastOrDefault(gc => gc.HeapAfterMb.HasValue)?.HeapAfterMb;
        var heapCapacityLimitGb = CalculateHeapCapacityLimitGb(cycles, source.Events);
        var pauseDurationYAxis = CalculatePauseDurationYAxis(pauseCycles);
        var collector = DetectCollector(cycles.Count == 0 ? source.Events : cycles);
        var health = BuildHealth(throughput, maxPausePhaseMs, allocationStallPeak, cycles);

        var report = new GcReport
        {
            FilePath = source.FilePath,
            FileName = source.FileName,
            FileSizeBytes = source.FileSizeBytes,
            LineCount = source.LineCount,
            ParsedAt = source.ParsedAt,
            StartSeconds = start,
            EndSeconds = end,
            SpanSeconds = spanSeconds,
            HeapCapacityLimitGb = heapCapacityLimitGb,
            PauseDurationYAxisMinimumMs = pauseDurationYAxis.MinimumMs,
            PauseDurationYAxisMaximumMs = pauseDurationYAxis.MaximumMs,
            ParsedEventCount = source.Events.Count,
            CompletedCycleCount = cycles.Count,
            CompletedEventCount = completions.Count,
            PauseCompletionCount = pauseCompletionRecords.Count,
            CollectorType = collector.Type,
            CollectorDescription = collector.Description,
            HealthLevel = health.Level,
            HealthTitle = health.Title,
            HealthSummary = health.Summary,
            Recommendations = health.Recommendations
        };

        AddOverviewKpis(report, collector, spanSeconds, throughput, totalCycleGcMs, averageCycleDurationMs, maxCycleDurationMs,
            totalPauseMs, averagePauseMs, maxPausePhaseMs, totalReclaimedMb, latestMetaspace, allocationStallPeak, peakHeapMb, latestAfterHeapMb);
        AddAdvancedKpis(report, cycles, completions, totalConcurrentMs, totalCompletionMs, pauseCompletionRecords);
        AddStatistics(report, cycles, completions, totalCompletionMs);
        AddEventRows(report, cycles, completions, start);
        AddCharts(report, cycles, completions, pauseCycles, start);

        return report;
    }

    private static GcReport BuildEmptyReport(GcParseResult source, double startSeconds, double endSeconds)
    {
        var collector = DetectCollector(source.Events);
        var report = new GcReport
        {
            FilePath = source.FilePath,
            FileName = source.FileName,
            FileSizeBytes = source.FileSizeBytes,
            LineCount = source.LineCount,
            ParsedAt = source.ParsedAt,
            StartSeconds = startSeconds,
            EndSeconds = endSeconds,
            SpanSeconds = Math.Max(0, endSeconds - startSeconds),
            HeapCapacityLimitGb = CalculateHeapCapacityLimitGb([], source.Events),
            ParsedEventCount = source.Events.Count,
            CompletedCycleCount = 0,
            CompletedEventCount = 0,
            PauseCompletionCount = 0,
            CollectorType = collector.Type,
            CollectorDescription = collector.Description,
            HealthLevel = "无数据",
            HealthTitle = "没有可展示的完成 GC 事件",
            HealthSummary = "当前时间范围内没有解析到带耗时和堆变化的 GC 完成事件。",
            Recommendations = ["请选择包含完整 JDK Unified GC 日志的文件，或切换到更大的时间范围。"]
        };

        report.OverviewKpis.Add(new GcKpi { Name = "GC收集器类型", Value = collector.Type, Description = collector.Description, Tone = collector.Tone });
        report.OverviewKpis.Add(new GcKpi { Name = "GC Cycle数", Value = "0", Description = "当前范围内没有完成的 GC cycle" });
        report.OverviewKpis.Add(new GcKpi { Name = "完成事件数", Value = "0", Description = "当前范围内没有带耗时的完成行" });
        return report;
    }

    private static double CalculateHeapCapacityLimitGb(IEnumerable<GcEvent> preferredEvents, IEnumerable<GcEvent> fallbackEvents)
    {
        var capacityMb = FindHeapCapacityLimitMb(preferredEvents) ?? FindHeapCapacityLimitMb(fallbackEvents);
        return capacityMb.HasValue ? capacityMb.Value / 1024d : double.NaN;
    }

    private static double? FindHeapCapacityLimitMb(IEnumerable<GcEvent> events)
    {
        var eventList = events as IReadOnlyCollection<GcEvent> ?? events.ToList();
        var maxCapacityMb = eventList
            .Where(gc => gc.MaxCapacityMb.HasValue)
            .Select(gc => gc.MaxCapacityMb!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (maxCapacityMb > 0)
        {
            return maxCapacityMb;
        }

        var softMaxCapacityMb = eventList
            .Where(gc => gc.SoftMaxCapacityMb.HasValue)
            .Select(gc => gc.SoftMaxCapacityMb!.Value)
            .DefaultIfEmpty(0)
            .Max();
        return softMaxCapacityMb > 0 ? softMaxCapacityMb : null;
    }

    private static (double MinimumMs, double MaximumMs) CalculatePauseDurationYAxis(IEnumerable<PauseCycleSample> pauseCycles)
    {
        var pauseDurations = pauseCycles
            .Select(sample => sample.DurationMs)
            .Where(value => value > 0)
            .ToList();
        if (pauseDurations.Count == 0)
        {
            return (double.NaN, double.NaN);
        }

        var minPauseMs = pauseDurations.Min();
        var maxPauseMs = pauseDurations.Max();
        if (maxPauseMs >= 1)
        {
            return (double.NaN, double.NaN);
        }

        var paddingMs = Math.Max((maxPauseMs - minPauseMs) * 0.1, 0.005);
        return (Math.Max(0, minPauseMs - paddingMs), maxPauseMs + paddingMs);
    }

    private sealed record CollectorSummary(string Type, string Description, string Tone);

    private static CollectorSummary DetectCollector(IEnumerable<GcEvent> events)
    {
        var eventList = events as IReadOnlyCollection<GcEvent> ?? events.ToList();
        if (eventList.Count == 0)
        {
            return new CollectorSummary("未识别", "当前范围内没有可用于判断收集器的完成事件", "Neutral");
        }

        var g1Signals = eventList.Count(IsG1CollectorEvent);
        var zgcSignals = eventList.Count(IsZgcCollectorEvent);

        if (g1Signals == 0 && zgcSignals == 0)
        {
            return new CollectorSummary("未识别", "日志中没有出现 G1 或 ZGC 的典型特征", "Neutral");
        }

        if (g1Signals >= zgcSignals)
        {
            return new CollectorSummary("G1", $"识别到 {g1Signals:N0} 个 G1 特征事件，例如 G1 疏散暂停、混合回收或并发标记周期", "Good");
        }

        return new CollectorSummary("ZGC", $"识别到 {zgcSignals:N0} 个 ZGC 特征事件，例如 Minor/Major Collection、MMU 或分配停顿", "Good");
    }

    private static bool IsG1CollectorEvent(GcEvent gc)
    {
        var type = gc.TypeRaw ?? string.Empty;
        var cause = gc.CauseRaw ?? string.Empty;

        return type.Contains("G1", StringComparison.OrdinalIgnoreCase)
               || cause.Contains("G1", StringComparison.OrdinalIgnoreCase)
               || type.Contains("混合GC", StringComparison.Ordinal)
               || type.Contains("并发标记周期", StringComparison.Ordinal)
               || type.Contains("Remark暂停", StringComparison.Ordinal)
               || type.Contains("清理暂停", StringComparison.Ordinal)
               || gc.Phases.Any(phase => phase.NameRaw.Contains("Evacuate Collection Set", StringComparison.OrdinalIgnoreCase)
                                          || phase.NameRaw.Contains("Merge Heap Roots", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsZgcCollectorEvent(GcEvent gc)
    {
        var type = gc.TypeRaw ?? string.Empty;
        var cause = gc.CauseRaw ?? string.Empty;

        return type.Equals("Minor", StringComparison.OrdinalIgnoreCase)
               || type.Equals("Major", StringComparison.OrdinalIgnoreCase)
               || type.Equals("Full", StringComparison.OrdinalIgnoreCase)
               || cause.Equals("High Usage", StringComparison.OrdinalIgnoreCase)
               || cause.Equals("Allocation Rate", StringComparison.OrdinalIgnoreCase)
               || cause.Equals("Allocation Stall", StringComparison.OrdinalIgnoreCase)
               || gc.AllocationStallPeak > 0
               || gc.MmuSamples.Count > 0;
    }
    private static void AddOverviewKpis(
        GcReport report,
        CollectorSummary collector,
        double spanSeconds,
        double? throughput,
        double totalCycleGcMs,
        double averageCycleDurationMs,
        double maxCycleDurationMs,
        double totalPauseMs,
        double averagePauseMs,
        double maxPausePhaseMs,
        double totalReclaimedMb,
        GcMetaspaceSample? latestMetaspace,
        int allocationStallPeak,
        double peakHeapMb,
        double? latestAfterHeapMb)
    {
        var reclaimedRate = spanSeconds <= 0 ? 0 : totalReclaimedMb / spanSeconds;

        report.OverviewKpis.Add(new GcKpi { Name = "GC收集器类型", Value = collector.Type, Description = collector.Description, Tone = collector.Tone });
        report.OverviewKpis.Add(new GcKpi { Name = "GC Cycle数", Value = report.CompletedCycleCount.ToString("N0"), Description = "按 GC id 聚合后的完成 cycle 数" });
        report.OverviewKpis.Add(new GcKpi { Name = "完成事件数", Value = report.CompletedEventCount.ToString("N0"), Description = "日志中带耗时的 [gc] 完成行数" });
        report.OverviewKpis.Add(new GcKpi { Name = "暂停完成事件数", Value = report.PauseCompletionCount.ToString("N0"), Description = "G1 Pause/Full GC 等暂停完成行数" });
        report.OverviewKpis.Add(new GcKpi { Name = "日志跨度", Value = GcText.FormatDuration(spanSeconds * 1000), Description = "按当前时间窗口计算" });
        report.OverviewKpis.Add(new GcKpi
        {
            Name = "吞吐量",
            Value = throughput.HasValue ? $"{throughput.Value:0.####}%" : GcText.NotReported,
            Description = throughput.HasValue ? "按当前窗口内 Pause 阶段时间计算" : "当前时间范围没有可计算的时间跨度",
            Tone = !throughput.HasValue ? "Neutral" : throughput.Value >= 99.99 ? "Good" : "Warn"
        });
        report.OverviewKpis.Add(new GcKpi { Name = "总GC耗时(Cycle)", Value = GcText.FormatDuration(totalCycleGcMs), Description = "按 GC id 聚合后的 cycle 耗时总和" });
        report.OverviewKpis.Add(new GcKpi { Name = "总暂停时间", Value = GcText.FormatDuration(totalPauseMs), Description = "Cycle 内 Pause 阶段总和", Tone = totalPauseMs < 1000 ? "Good" : "Warn" });
        report.OverviewKpis.Add(new GcKpi { Name = "平均Cycle耗时", Value = GcText.FormatMs(averageCycleDurationMs), Description = "完成 GC cycle 平均耗时" });
        report.OverviewKpis.Add(new GcKpi { Name = "最大Cycle耗时", Value = GcText.FormatMs(maxCycleDurationMs), Description = "单个 GC cycle 耗时峰值", Tone = maxCycleDurationMs > 5000 ? "Warn" : "Neutral" });
        report.OverviewKpis.Add(new GcKpi { Name = "平均暂停时间", Value = GcText.FormatMs(averagePauseMs), Description = "按每个含暂停的 cycle 求均值", Tone = averagePauseMs < 10 ? "Good" : "Warn" });
        report.OverviewKpis.Add(new GcKpi { Name = "最大暂停阶段", Value = GcText.FormatMs(maxPausePhaseMs), Description = "单个 Pause 阶段最大值", Tone = maxPausePhaseMs < 10 ? "Good" : "Warn" });
        report.OverviewKpis.Add(new GcKpi { Name = "回收总量", Value = GcText.FormatMb(totalReclaimedMb), Description = "仅统计同时存在 GC 前后堆的 cycle" });
        report.OverviewKpis.Add(new GcKpi { Name = "平均回收速率", Value = $"{reclaimedRate:0.##} MB/s", Description = "回收总量 / 当前时间窗口" });
        report.OverviewKpis.Add(new GcKpi { Name = "堆峰值", Value = GcText.FormatMb(peakHeapMb), Description = "ZGC High 列或 GC 前后堆使用量峰值" });
        report.OverviewKpis.Add(new GcKpi { Name = "最新GC后堆", Value = GcText.FormatMb(latestAfterHeapMb), Description = "时间范围内最后一次 GC 后堆" });
        report.OverviewKpis.Add(new GcKpi { Name = "元空间使用", Value = GcText.FormatMb(latestMetaspace?.UsedMb), Description = latestMetaspace is null ? "日志未报告" : "最后一次报告的 used 值" });
        report.OverviewKpis.Add(new GcKpi { Name = "最大并发分配停顿", Value = allocationStallPeak.ToString("N0"), Description = "Allocation Stalls 阶段快照峰值", Tone = allocationStallPeak == 0 ? "Good" : "Warn" });
    }

    private static void AddAdvancedKpis(
        GcReport report,
        List<GcEvent> events,
        List<GcCompletionRecord> completions,
        double totalConcurrentMs,
        double totalCompletionMs,
        List<GcCompletionRecord> pauseCompletionRecords)
    {
        var concurrentPhases = events.SelectMany(gc => gc.Phases).Where(phase => phase.IsConcurrent).ToList();
        var pausePhases = events.SelectMany(gc => gc.Phases).Where(phase => phase.IsPause).ToList();
        var youngSamples = events.Count(gc => gc.GenerationStats.ContainsKey("Young"));
        var oldSamples = events.Count(gc => gc.GenerationStats.ContainsKey("Old"));
        var latestLoad = events.LastOrDefault(gc => gc.Load is not null)?.Load;
        var mmuSamples = events.SelectMany(gc => gc.MmuSamples).ToList();
        var totalPromoted = events
            .SelectMany(gc => gc.GenerationStats.Values)
            .Sum(stats => stats.PromotedMb ?? 0);

        report.AdvancedKpis.Add(new GcKpi { Name = "完成行总耗时", Value = GcText.FormatDuration(totalCompletionMs), Description = "所有带耗时 [gc] 完成行总和" });
        report.AdvancedKpis.Add(new GcKpi { Name = "完成行平均", Value = GcText.FormatMs(completions.Count == 0 ? null : completions.Average(record => record.DurationMs)), Description = "完成行耗时均值" });
        report.AdvancedKpis.Add(new GcKpi { Name = "暂停完成平均", Value = GcText.FormatMs(pauseCompletionRecords.Count == 0 ? null : pauseCompletionRecords.Average(record => record.DurationMs)), Description = "Pause/Full GC 完成行耗时均值" });
        report.AdvancedKpis.Add(new GcKpi { Name = "暂停完成最大", Value = GcText.FormatMs(pauseCompletionRecords.Count == 0 ? null : pauseCompletionRecords.Max(record => record.DurationMs)), Description = "Pause/Full GC 完成行耗时峰值" });
        report.AdvancedKpis.Add(new GcKpi { Name = "并发GC总耗时", Value = GcText.FormatDuration(totalConcurrentMs), Description = "Concurrent 阶段总和" });
        report.AdvancedKpis.Add(new GcKpi { Name = "并发阶段平均", Value = GcText.FormatMs(concurrentPhases.Count == 0 ? null : concurrentPhases.Average(phase => phase.DurationMs)), Description = "所有 Concurrent 阶段均值" });
        report.AdvancedKpis.Add(new GcKpi { Name = "并发阶段最大", Value = GcText.FormatMs(concurrentPhases.Count == 0 ? null : concurrentPhases.Max(phase => phase.DurationMs)), Description = "所有 Concurrent 阶段峰值" });
        report.AdvancedKpis.Add(new GcKpi { Name = "暂停阶段样本", Value = pausePhases.Count.ToString("N0"), Description = "Pause 阶段行数" });
        report.AdvancedKpis.Add(new GcKpi { Name = "年轻代样本", Value = youngSamples.ToString("N0"), Description = "报告年轻代统计的事件数" });
        report.AdvancedKpis.Add(new GcKpi { Name = "老年代样本", Value = oldSamples.ToString("N0"), Description = "报告老年代统计的事件数" });
        report.AdvancedKpis.Add(new GcKpi { Name = "最小MMU", Value = mmuSamples.Count == 0 ? GcText.NotReported : $"{mmuSamples.Min(sample => sample.Percent):0.##}%", Description = "日志报告的最小 MMU 百分比" });
        report.AdvancedKpis.Add(new GcKpi { Name = "最新系统负载", Value = latestLoad is null ? GcText.NotReported : $"{latestLoad.OneMinute:0.##} / {latestLoad.FiveMinutes:0.##} / {latestLoad.FifteenMinutes:0.##}", Description = "1/5/15 分钟 Load" });
        report.AdvancedKpis.Add(new GcKpi { Name = "晋升总量", Value = GcText.FormatMb(totalPromoted), Description = "Promoted 行的可解析总和" });
    }

    private static void AddStatistics(GcReport report, List<GcEvent> events, List<GcCompletionRecord> completions, double totalCompletionMs)
    {
        foreach (var row in completions
                     .GroupBy(record => TranslateType(record.TypeRaw))
                     .Select(group => BuildStatisticRow(group.Key, group.ToList(), totalCompletionMs))
                     .OrderByDescending(row => row.Count))
        {
            report.TypeStatistics.Add(row);
        }

        foreach (var row in completions
                     .GroupBy(record => TranslateCause(record.CauseRaw))
                     .Select(group => BuildStatisticRow(group.Key, group.ToList(), totalCompletionMs))
                     .OrderByDescending(row => row.Count))
        {
            report.CauseStatistics.Add(row);
        }

        foreach (var row in events
                     .SelectMany(gc => gc.Phases)
                     .Where(phase => !phase.IsGenerationSummary)
                     .GroupBy(phase => phase.Name)
                     .Select(group => new GcPhaseStatisticRow
                     {
                         Name = group.Key,
                         Count = group.Count(),
                         TotalMs = group.Sum(phase => phase.DurationMs),
                         AverageMs = group.Average(phase => phase.DurationMs),
                         MaxMs = group.Max(phase => phase.DurationMs)
                     })
                     .OrderByDescending(row => row.TotalMs))
        {
            report.PhaseStatistics.Add(row);
        }

        foreach (var generationName in new[] { "Young", "Old" })
        {
            var samples = events
                .Select(gc => gc.GenerationStats.TryGetValue(generationName, out var stats) ? stats : null)
                .Where(stats => stats is not null)
                .Cast<GcGenerationStats>()
                .ToList();

            if (samples.Count == 0)
            {
                continue;
            }

            report.GenerationStatistics.Add(new GcGenerationSummaryRow
            {
                Name = generationName == "Young" ? "年轻代" : "老年代",
                Samples = samples.Count,
                PeakUsedMb = samples
                    .SelectMany(stats => new[] { stats.UsedBeforeMb, stats.UsedAfterMb })
                    .Where(value => value.HasValue)
                    .Select(value => value.GetValueOrDefault())
                    .DefaultIfEmpty(0)
                    .Max(),
                LatestUsedMb = samples.LastOrDefault(stats => stats.UsedAfterMb.HasValue)?.UsedAfterMb,
                TotalReclaimedMb = samples.Sum(stats => stats.ReclaimedMb ?? 0)
            });
        }
    }

    private static GcStatisticRow BuildStatisticRow(string name, List<GcCompletionRecord> records, double totalCompletionMs)
    {
        var total = records.Sum(record => record.DurationMs);
        return new GcStatisticRow
        {
            Name = name,
            Count = records.Count,
            TotalDurationMs = total,
            AverageDurationMs = records.Count == 0 ? 0 : total / records.Count,
            DurationPercent = totalCompletionMs <= 0 ? 0 : total * 100d / totalCompletionMs
        };
    }

    private static void AddEventRows(GcReport report, List<GcEvent> events, List<GcCompletionRecord> completions, double startSeconds)
    {
        var eventsById = events.ToDictionary(gc => gc.Id);
        foreach (var record in completions)
        {
            eventsById.TryGetValue(record.Id, out var cycle);
            report.EventRows.Add(new GcEventRow
            {
                Id = record.Id,
                TimeText = GcText.FormatRelativeTime(record.EndSeconds - startSeconds),
                TypeText = TranslateType(record.TypeRaw),
                CauseText = TranslateCause(record.CauseRaw),
                HeapBeforeText = GcText.FormatMb(record.HeapBeforeMb),
                HeapAfterText = GcText.FormatMb(record.HeapAfterMb),
                ReclaimedText = GcText.FormatMb(record.ReclaimedMb),
                DurationText = GcText.FormatMs(record.DurationMs),
                PauseText = record.IsPause ? GcText.FormatMs(record.DurationMs) : GcText.NotReported,
                ConcurrentText = record.IsConcurrent ? GcText.FormatMs(record.DurationMs) : GcText.NotReported,
                MetaspaceText = GcText.FormatMb(cycle?.Metaspace?.UsedMb),
                AllocationStallPeak = cycle?.AllocationStallPeak ?? 0
            });
        }
    }

    private static void AddCharts(
        GcReport report,
        List<GcEvent> events,
        List<GcCompletionRecord> completions,
        List<PauseCycleSample> pauseCycles,
        double startSeconds)
    {
        report.HeapBeforeSeries.Add(MakeSeries("GC前堆", Palette[1], events
            .Where(gc => gc.HeapBeforeMb.HasValue)
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.HeapBeforeMb!.Value / 1024d, "GB", "GC前堆"))));

        report.HeapAfterSeries.Add(MakeSeries("GC后堆", Palette[0], events
            .Where(gc => gc.HeapAfterMb.HasValue)
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.HeapAfterMb!.Value / 1024d, "GB", "GC后堆"))));

        report.HeapReclaimedSeries.Add(MakeSeries("回收量", Palette[3], events
            .Where(gc => (gc.ReclaimedMb ?? 0) > 0)
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.ReclaimedMb.GetValueOrDefault() / 1024d, "GB", "回收量"))));

        report.DurationSeries.Add(MakeSeries("GC Cycle耗时", Palette[4], events
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.DurationMs ?? 0, "ms", "GC Cycle耗时"))));

        report.PauseSeries.Add(MakeSeries("暂停耗时", Palette[5], pauseCycles
            .Select(sample => MakeTimePoint(sample.Event, sample.EndSeconds, startSeconds, sample.DurationMs, "ms", "暂停耗时"))));

        var cumulative = 0d;
        report.CumulativeDurationSeries.Add(MakeSeries("累计GC耗时", Palette[2], events.Select(gc =>
        {
            cumulative += gc.DurationMs ?? 0;
            return MakeTimePoint(gc, startSeconds, cumulative / 1000d, "秒", "累计GC耗时");
        })));

        report.CauseSeries.Add(MakeSeries("GC原因", Palette[0], completions
            .GroupBy(record => TranslateCause(record.CauseRaw))
            .OrderByDescending(group => group.Count())
            .Select((group, index) => new ChartPoint
            {
                X = index,
                Y = group.Count(),
                Label = group.Key,
                Tooltip = $"{group.Key}: {group.Count():N0} 次"
            })));

        report.TypeSeries.Add(MakeSeries("GC类型", Palette[2], completions
            .GroupBy(record => TranslateType(record.TypeRaw))
            .OrderByDescending(group => group.Count())
            .Select((group, index) => new ChartPoint
            {
                X = index,
                Y = group.Count(),
                Label = group.Key,
                Tooltip = $"{group.Key}: {group.Count():N0} 次"
            })));

        report.PhaseTotalSeries.Add(MakeSeries("阶段总耗时", Palette[1], report.PhaseStatistics
            .Take(12)
            .Select((row, index) => new ChartPoint
            {
                X = index,
                Y = row.TotalMs / 1000d,
                Label = row.Name,
                Tooltip = $"{row.Name}: {row.TotalText}"
            })));

        report.PhaseAverageSeries.Add(MakeSeries("阶段平均耗时", Palette[6], report.PhaseStatistics
            .Take(12)
            .Select((row, index) => new ChartPoint
            {
                X = index,
                Y = row.AverageMs,
                Label = row.Name,
                Tooltip = $"{row.Name}: {row.AverageText}"
            })));

        AddGenerationSeries(report, events, startSeconds);
        AddMetaspaceSeries(report, events, startSeconds);
        AddLoadSeries(report, events, startSeconds);
        AddMmuSeries(report, events, startSeconds);
    }

    private static void AddGenerationSeries(GcReport report, List<GcEvent> events, double startSeconds)
    {
        var youngPoints = events
            .Where(gc => gc.GenerationStats.TryGetValue("Young", out var stats) && stats.UsedAfterMb.HasValue)
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.GenerationStats["Young"].UsedAfterMb!.Value / 1024d, "GB", "年轻代使用量"));
        var oldPoints = events
            .Where(gc => gc.GenerationStats.TryGetValue("Old", out var stats) && stats.UsedAfterMb.HasValue)
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.GenerationStats["Old"].UsedAfterMb!.Value / 1024d, "GB", "老年代使用量"));

        report.GenerationSeries.Add(MakeSeries("年轻代使用量", Palette[0], youngPoints));
        report.GenerationSeries.Add(MakeSeries("老年代使用量", Palette[4], oldPoints));
    }

    private static void AddMetaspaceSeries(GcReport report, List<GcEvent> events, double startSeconds)
    {
        report.MetaspaceSeries.Add(MakeSeries("元空间已用", Palette[2], events
            .Where(gc => gc.Metaspace is not null)
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.Metaspace!.UsedMb, "MB", "元空间已用"))));
        report.MetaspaceSeries.Add(MakeSeries("元空间已提交", Palette[5], events
            .Where(gc => gc.Metaspace is not null)
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.Metaspace!.CommittedMb, "MB", "元空间已提交"))));
        report.MetaspaceSeries.Add(MakeSeries("元空间保留", Palette[7], events
            .Where(gc => gc.Metaspace?.ReservedMb.HasValue == true)
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.Metaspace!.ReservedMb.GetValueOrDefault(), "MB", "元空间保留"))));
    }

    private static void AddLoadSeries(GcReport report, List<GcEvent> events, double startSeconds)
    {
        report.LoadSeries.Add(MakeSeries("1分钟负载", Palette[0], events
            .Where(gc => gc.Load is not null)
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.Load!.OneMinute, "", "1分钟负载"))));
        report.LoadSeries.Add(MakeSeries("5分钟负载", Palette[2], events
            .Where(gc => gc.Load is not null)
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.Load!.FiveMinutes, "", "5分钟负载"))));
        report.LoadSeries.Add(MakeSeries("15分钟负载", Palette[5], events
            .Where(gc => gc.Load is not null)
            .Select(gc => MakeTimePoint(gc, startSeconds, gc.Load!.FifteenMinutes, "", "15分钟负载"))));
    }

    private static void AddMmuSeries(GcReport report, List<GcEvent> events, double startSeconds)
    {
        var windows = events
            .SelectMany(gc => gc.MmuSamples)
            .Select(sample => sample.WindowMs)
            .Distinct()
            .OrderBy(window => window)
            .Take(6)
            .ToList();

        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var points = events
                .Select(gc => new { Event = gc, Sample = gc.MmuSamples.FirstOrDefault(sample => Math.Abs(sample.WindowMs - window) < 0.001) })
                .Where(item => item.Sample is not null)
                .Select(item => MakeTimePoint(item.Event, startSeconds, item.Sample!.Percent, "%", $"MMU {window:0}ms"));

            report.MmuSeries.Add(MakeSeries($"MMU {window:0}ms", Palette[index % Palette.Length], points));
        }
    }

    private static ChartPoint MakeTimePoint(GcEvent gc, double startSeconds, double y, string unit, string metricName)
    {
        return MakeTimePoint(gc, gc.EndSeconds, startSeconds, y, unit, metricName);
    }

    private static ChartPoint MakeTimePoint(
        GcEvent gc,
        double eventSeconds,
        double startSeconds,
        double y,
        string unit,
        string metricName)
    {
        var relative = eventSeconds - startSeconds;
        var label = GcText.FormatRelativeTime(relative);
        var unitText = string.IsNullOrWhiteSpace(unit) ? "" : $" {unit}";
        return new ChartPoint
        {
            X = relative,
            Y = y,
            Label = label,
            Tooltip = $"{label}\n{metricName}: {y:0.###}{unitText}\n类型: {TranslateType(gc.TypeRaw)}\n原因: {TranslateCause(gc.CauseRaw)}"
        };
    }

    private static ChartSeries MakeSeries(string name, Color color, IEnumerable<ChartPoint> points)
    {
        var solid = new SolidColorBrush(color);
        var fill = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B));
        solid.Freeze();
        fill.Freeze();

        return new ChartSeries
        {
            Name = name,
            Stroke = solid,
            Fill = fill,
            Points = new ObservableCollection<ChartPoint>(Sample(points.ToList(), MaxDrawPoints))
        };
    }

    private static IEnumerable<ChartPoint> Sample(List<ChartPoint> points, int maxPoints)
    {
        if (maxPoints <= 0)
        {
            return [];
        }

        if (points.Count <= maxPoints)
        {
            return points;
        }

        if (maxPoints <= 2)
        {
            return maxPoints == 1 ? [points[0]] : [points[0], points[^1]];
        }

        if (maxPoints == 3)
        {
            var peak = points
                .Select((point, index) => new { Point = point, Index = index })
                .Skip(1)
                .Take(points.Count - 2)
                .OrderByDescending(item => Math.Abs(item.Point.Y))
                .First();
            return [points[0], points[peak.Index], points[^1]];
        }

        var sampled = new List<ChartPoint>(maxPoints) { points[0] };
        var interiorCount = points.Count - 2;
        var bucketCount = Math.Max(1, (maxPoints - 2) / 2);
        var bucketSize = interiorCount / (double)bucketCount;

        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = 1 + (int)Math.Floor(bucket * bucketSize);
            var end = bucket == bucketCount - 1
                ? points.Count - 1
                : 1 + (int)Math.Floor((bucket + 1) * bucketSize);
            if (start >= end)
            {
                continue;
            }

            var minIndex = start;
            var maxIndex = start;
            for (var index = start + 1; index < end; index++)
            {
                if (points[index].Y < points[minIndex].Y)
                {
                    minIndex = index;
                }

                if (points[index].Y > points[maxIndex].Y)
                {
                    maxIndex = index;
                }
            }

            if (minIndex == maxIndex)
            {
                sampled.Add(points[minIndex]);
            }
            else
            {
                sampled.Add(points[Math.Min(minIndex, maxIndex)]);
                sampled.Add(points[Math.Max(minIndex, maxIndex)]);
            }
        }

        sampled.Add(points[^1]);
        return sampled;
    }

    private static (string Level, string Title, string Summary, List<string> Recommendations) BuildHealth(
        double? throughput,
        double maxPauseMs,
        int allocationStallPeak,
        List<GcEvent> events)
    {
        var recommendations = new List<string>();
        var heapTrend = CalculateAfterGcTrend(events);

        if (!throughput.HasValue)
        {
            recommendations.Add("当前时间范围没有可计算的时间跨度，吞吐量未报告。");
        }
        else if (throughput.Value >= 99.99 && maxPauseMs < 10 && allocationStallPeak == 0)
        {
            recommendations.Add("暂停时间非常低，当前日志中的 ZGC 活动整体健康。");
        }
        else
        {
            recommendations.Add("关注暂停阶段、分配停顿和 GC 后堆占用走势，优先定位峰值附近的业务流量或分配模式。");
        }

        if (heapTrend > 0.2)
        {
            recommendations.Add("GC 后堆占用呈上升趋势，建议结合堆转储或对象分配统计继续排查长期存活对象。");
        }
        else
        {
            recommendations.Add("GC 后堆占用没有明显持续上升，当前日志未显示强内存泄漏信号。");
        }

        if (allocationStallPeak > 0)
        {
            recommendations.Add("日志中出现并发分配停顿，建议检查堆大小、分配速率和应用线程在峰值时段的行为。");
        }

        if (!throughput.HasValue)
        {
            return ("无数据", "吞吐量未报告", $"当前时间范围没有可计算的时间跨度，最大暂停阶段 {GcText.FormatMs(maxPauseMs)}。", recommendations);
        }

        if (throughput.Value >= 99.99 && maxPauseMs < 10 && allocationStallPeak == 0)
        {
            return ("健康", "GC 活动健康", $"吞吐量 {throughput.Value:0.####}%，最大暂停阶段 {GcText.FormatMs(maxPauseMs)}。", recommendations);
        }

        if (throughput.Value >= 99.9 && maxPauseMs < 100)
        {
            return ("注意", "GC 活动需要关注", $"吞吐量 {throughput.Value:0.####}%，最大暂停阶段 {GcText.FormatMs(maxPauseMs)}。", recommendations);
        }

        return ("风险", "GC 活动存在风险", $"吞吐量 {throughput.Value:0.####}%，最大暂停阶段 {GcText.FormatMs(maxPauseMs)}。", recommendations);
    }

    private static double CalculateAfterGcTrend(List<GcEvent> events)
    {
        var values = events.Where(gc => gc.HeapAfterMb.HasValue).Select(gc => gc.HeapAfterMb!.Value).ToList();
        if (values.Count < 20)
        {
            return 0;
        }

        var window = Math.Max(5, values.Count / 10);
        var firstAverage = values.Take(window).Average();
        var lastAverage = values.TakeLast(window).Average();
        if (firstAverage <= 0)
        {
            return 0;
        }

        return (lastAverage - firstAverage) / firstAverage;
    }

    public static string TranslateType(string raw)
    {
        return raw switch
        {
            "Minor" => "年轻代GC",
            "Major" => "主要GC",
            "Full" => "完全GC",
            "" => GcText.NotReported,
            _ => raw
        };
    }

    public static string TranslateCause(string raw)
    {
        return raw switch
        {
            "High Usage" => "高使用率",
            "Allocation Rate" => "分配速率",
            "Allocation Stall" => "分配停顿",
            "Proactive" => "主动触发",
            "Remark" => "重新标记",
            "Cleanup" => "清理",
            "Concurrent Start" => "并发标记启动",
            "Concurrent Mark Cycle" => "并发标记周期",
            "G1 Evacuation Pause" => "G1疏散暂停",
            "G1 Humongous Allocation" => "G1巨型对象分配",
            "G1 Preventive Collection" => "G1预防性回收",
            "Prepare Mixed" => "准备混合回收",
            "Mixed" => "混合回收",
            "" => GcText.NotReported,
            _ => raw
        };
    }
}
