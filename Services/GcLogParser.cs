using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using gcviewer.Models;

namespace gcviewer.Services;

public sealed class GcLogParser
{
    private static readonly Regex LogLineRegex = new(
        @"^\[(?<time>[\d.]+)s\]\[info\s*\]\[(?<tags>[^\]]+)\]\s+GC\((?<id>\d+)\)\s+(?:(?<gen>[yYoO]):\s+)?(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CollectionRegex = new(
        @"^(?<type>Minor|Major|Full)\s+Collection\s+\((?<cause>[^)]+)\)(?:\s+(?<before>\d+(?:\.\d+)?)(?<beforeUnit>[KMGTP])\((?<beforePct>\d+(?:\.\d+)?)%\)->(?<after>\d+(?:\.\d+)?)(?<afterUnit>[KMGTP])\((?<afterPct>\d+(?:\.\d+)?)%\)\s+(?<duration>\d+(?:\.\d+)?)(?<durationUnit>ms|s))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex G1CollectionRegex = new(
        @"^(?<name>.+?)(?:\s+(?<before>\d+(?:\.\d+)?)(?<beforeUnit>[KMGTP])->(?<after>\d+(?:\.\d+)?)(?<afterUnit>[KMGTP])\((?<capacity>\d+(?:\.\d+)?)(?<capacityUnit>[KMGTP])\))?\s+(?<duration>\d+(?:\.\d+)?)(?<durationUnit>ms|s)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PhaseDurationRegex = new(
        @"^(?<name>.+?)(?::)?\s+(?<value>\d+(?:\.\d+)?)(?<unit>ms|s)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MemoryTokenRegex = new(
        @"(?<value>\d+(?:\.\d+)?)(?<unit>[KMGTP])\s*\((?<percent>\d+(?:\.\d+)?)%\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CapacityRegex = new(
        @":\s*(?<value>\d+(?:\.\d+)?)(?<unit>[KMGTP])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MetaspaceRegex = new(
        @"Metaspace:\s+(?<used>\d+(?:\.\d+)?)(?<usedUnit>[KMGTP])\s+used,\s+(?<committed>\d+(?:\.\d+)?)(?<committedUnit>[KMGTP])\s+committed,\s+(?<reserved>\d+(?:\.\d+)?)(?<reservedUnit>[KMGTP])\s+reserved",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex G1MetaspaceRegex = new(
        @"Metaspace:\s+(?<before>\d+(?:\.\d+)?)(?<beforeUnit>[KMGTP])\((?<beforeCommitted>\d+(?:\.\d+)?)(?<beforeCommittedUnit>[KMGTP])\)->(?<after>\d+(?:\.\d+)?)(?<afterUnit>[KMGTP])\((?<afterCommitted>\d+(?:\.\d+)?)(?<afterCommittedUnit>[KMGTP])\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LoadRegex = new(
        @"Load:\s+(?<one>\d+(?:\.\d+)?).*?/\s+(?<five>\d+(?:\.\d+)?).*?/\s+(?<fifteen>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MmuRegex = new(
        @"(?<window>\d+(?:\.\d+)?)ms/(?<percent>\d+(?:\.\d+)?)%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AllocationStallsRegex = new(
        @"Allocation Stalls:\s+(?<value>\d+)(?:\s+(?<value>\d+))*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex G1HeapRegionSizeRegex = new(
        @"Heap Region Size:\s+(?<value>\d+(?:\.\d+)?)(?<unit>[KMGTP])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TenuringRegex = new(
        @"Using tenuring threshold:\s+(?<threshold>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AgeRowRegex = new(
        @"^(?<name>Eden|Survivor\s+\d+)\s+(?<live>\d+(?:\.\d+)?)(?<liveUnit>[KMGTP])\s+\(\d+%\)\s+(?<garbage>\d+(?:\.\d+)?)(?<garbageUnit>[KMGTP])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex G1RegionRegex = new(
        @"^(?<name>Eden|Survivor|Old|Humongous) regions:\s+(?<before>\d+)->(?<after>\d+)(?:\((?<target>\d+)\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly double[] G1RegionSizeMbCandidates = [1d, 2d, 4d, 8d, 16d, 32d];

    public GcParseResult Parse(string filePath, IProgress<GcParseProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        var events = new Dictionary<int, GcEvent>();
        var completionRecords = new List<GcCompletionRecord>();
        var heapSections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var g1Regions = new Dictionary<int, G1RegionAccumulator>();
        double? g1RegionSizeMb = null;
        long lineCount = 0;

        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fileStream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            lineCount++;
            ParseLine(line, events, completionRecords, heapSections, g1Regions, ref g1RegionSizeMb);

            if (lineCount % 2000 == 0)
            {
                progress?.Report(new GcParseProgress
                {
                    LinesRead = lineCount,
                    BytesRead = fileStream.Position,
                    TotalBytes = fileInfo.Length
                });
            }
        }

        progress?.Report(new GcParseProgress
        {
            LinesRead = lineCount,
            BytesRead = fileInfo.Length,
            TotalBytes = fileInfo.Length
        });

        var orderedEvents = events.Values
            .OrderBy(gc => gc.EndSeconds > 0 ? gc.EndSeconds : gc.FirstSeenSeconds)
            .ThenBy(gc => gc.Id)
            .ToList();

        var orderedCompletionRecords = completionRecords
            .OrderBy(record => record.EndSeconds)
            .ThenBy(record => record.Id)
            .ToList();

        var result = new GcParseResult
        {
            FilePath = fileInfo.FullName,
            FileName = fileInfo.Name,
            FileSizeBytes = fileInfo.Length,
            LineCount = lineCount,
            Events = orderedEvents,
            CompletionRecords = orderedCompletionRecords
        };

        if (orderedEvents.Count == 0)
        {
            result.Warnings.Add("没有识别到 GC 事件。");
        }
        else if (orderedCompletionRecords.Count == 0)
        {
            result.Warnings.Add("识别到了 GC 起始事件，但没有找到带耗时的完成行。");
        }

        return result;
    }

    private static void ParseLine(
        string line,
        Dictionary<int, GcEvent> events,
        List<GcCompletionRecord> completionRecords,
        Dictionary<string, string> heapSections,
        Dictionary<int, G1RegionAccumulator> g1Regions,
        ref double? g1RegionSizeMb)
    {
        if (TryParseG1HeapRegionSize(line, out var parsedG1RegionSizeMb))
        {
            g1RegionSizeMb = parsedG1RegionSizeMb;
        }

        var match = LogLineRegex.Match(line);
        if (!match.Success)
        {
            return;
        }

        var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
        var seconds = ParseDouble(match.Groups["time"].Value);
        var tags = match.Groups["tags"].Value.Trim();
        var generation = NormalizeGeneration(match.Groups["gen"].Value);
        var message = match.Groups["message"].Value.TrimEnd();

        if (!events.TryGetValue(id, out var gcEvent))
        {
            gcEvent = new GcEvent
            {
                Id = id,
                FirstSeenSeconds = seconds,
                EndSeconds = seconds
            };
            events[id] = gcEvent;
        }

        gcEvent.EndSeconds = Math.Max(gcEvent.EndSeconds, seconds);

        if (tags == "gc")
        {
            ParseCollectionLine(gcEvent, completionRecords, seconds, message, g1Regions, g1RegionSizeMb);
            return;
        }

        if (tags.Contains("gc,phases", StringComparison.OrdinalIgnoreCase))
        {
            ParsePhaseLine(gcEvent, generation, seconds, message, false);
            return;
        }

        if (tags.Contains("gc,marking", StringComparison.OrdinalIgnoreCase))
        {
            ParsePhaseLine(gcEvent, generation, seconds, message, true);
            return;
        }

        if (tags.Contains("gc,heap", StringComparison.OrdinalIgnoreCase))
        {
            ParseHeapLine(gcEvent, generation, message, heapSections, g1Regions, g1RegionSizeMb);
            return;
        }

        if (tags.Contains("gc,metaspace", StringComparison.OrdinalIgnoreCase))
        {
            ParseMetaspace(gcEvent, message);
            return;
        }

        if (tags.Contains("gc,alloc", StringComparison.OrdinalIgnoreCase))
        {
            ParseAllocation(gcEvent, message);
            return;
        }

        if (tags.Contains("gc,load", StringComparison.OrdinalIgnoreCase))
        {
            ParseLoad(gcEvent, message);
            return;
        }

        if (tags.Contains("gc,mmu", StringComparison.OrdinalIgnoreCase))
        {
            ParseMmu(gcEvent, message);
            return;
        }

        if (tags.Contains("gc,reloc", StringComparison.OrdinalIgnoreCase))
        {
            ParseRelocation(gcEvent, message);
        }
    }

    private static void ParseCollectionLine(
        GcEvent gcEvent,
        List<GcCompletionRecord> completionRecords,
        double seconds,
        string message,
        Dictionary<int, G1RegionAccumulator> g1Regions,
        double? g1RegionSizeMb)
    {
        var collectionMatch = CollectionRegex.Match(message);
        if (collectionMatch.Success)
        {
            gcEvent.TypeRaw = collectionMatch.Groups["type"].Value;
            gcEvent.CauseRaw = collectionMatch.Groups["cause"].Value;
            gcEvent.EndSeconds = seconds;

            if (!collectionMatch.Groups["duration"].Success)
            {
                return;
            }

            var before = ConvertToMb(collectionMatch.Groups["before"].Value, collectionMatch.Groups["beforeUnit"].Value);
            var after = ConvertToMb(collectionMatch.Groups["after"].Value, collectionMatch.Groups["afterUnit"].Value);
            var beforePercent = ParseDouble(collectionMatch.Groups["beforePct"].Value);
            var afterPercent = ParseDouble(collectionMatch.Groups["afterPct"].Value);
            var collectionDurationMs = ConvertToMs(collectionMatch.Groups["duration"].Value, collectionMatch.Groups["durationUnit"].Value);

            gcEvent.HeapBeforeMb = before;
            gcEvent.HeapAfterMb = after;
            gcEvent.HeapBeforePercent = beforePercent;
            gcEvent.HeapAfterPercent = afterPercent;
            gcEvent.DurationMs = collectionDurationMs;
            var explicitReclaimedMb = SumExplicitGenerationReclaimed(gcEvent);
            gcEvent.ExplicitReclaimedMb = explicitReclaimedMb;
            completionRecords.Add(new GcCompletionRecord
            {
                Id = gcEvent.Id,
                EndSeconds = seconds,
                NameRaw = $"{gcEvent.TypeRaw} Collection",
                TypeRaw = gcEvent.TypeRaw,
                CauseRaw = gcEvent.CauseRaw,
                HeapBeforeMb = before,
                HeapAfterMb = after,
                HeapBeforePercent = beforePercent,
                HeapAfterPercent = afterPercent,
                ExplicitReclaimedMb = explicitReclaimedMb,
                DurationMs = collectionDurationMs
            });
            return;
        }

        var g1Match = G1CollectionRegex.Match(message);
        if (!g1Match.Success)
        {
            return;
        }

        var name = g1Match.Groups["name"].Value.Trim();
        var durationMs = ConvertToMs(g1Match.Groups["duration"].Value, g1Match.Groups["durationUnit"].Value);
        var isG1ConcurrentMarkCycle = name.StartsWith("Concurrent Mark Cycle", StringComparison.OrdinalIgnoreCase);
        gcEvent.TypeRaw = TranslateG1Type(name);
        gcEvent.CauseRaw = TranslateG1Cause(name);
        gcEvent.EndSeconds = seconds;
        gcEvent.DurationMs = durationMs;

        if (isG1ConcurrentMarkCycle && !g1Match.Groups["before"].Success)
        {
            gcEvent.HeapBeforeMb = null;
            gcEvent.HeapAfterMb = null;
            gcEvent.HeapBeforePercent = null;
            gcEvent.HeapAfterPercent = null;
        }

        double? heapBefore = null;
        double? heapAfter = null;
        double? heapBeforePercent = null;
        double? heapAfterPercent = null;
        double? capacityMb = null;

        if (g1Match.Groups["before"].Success)
        {
            var before = ConvertToMb(g1Match.Groups["before"].Value, g1Match.Groups["beforeUnit"].Value);
            var after = ConvertToMb(g1Match.Groups["after"].Value, g1Match.Groups["afterUnit"].Value);
            var capacity = ConvertToMb(g1Match.Groups["capacity"].Value, g1Match.Groups["capacityUnit"].Value);
            heapBefore = before;
            heapAfter = after;
            capacityMb = capacity;
            heapBeforePercent = capacity <= 0 ? null : before * 100d / capacity;
            heapAfterPercent = capacity <= 0 ? null : after * 100d / capacity;
            gcEvent.HeapBeforeMb = before;
            gcEvent.HeapAfterMb = after;
            gcEvent.MaxCapacityMb = capacity;
            gcEvent.SoftMaxCapacityMb = capacity;
            gcEvent.HeapBeforePercent = heapBeforePercent;
            gcEvent.HeapAfterPercent = heapAfterPercent;
            ApplyG1RegionSize(gcEvent, g1Regions, g1RegionSizeMb);
        }

        completionRecords.Add(new GcCompletionRecord
        {
            Id = gcEvent.Id,
            EndSeconds = seconds,
            NameRaw = name,
            TypeRaw = TranslateG1Type(name),
            CauseRaw = TranslateG1Cause(name),
            HeapBeforeMb = heapBefore,
            HeapAfterMb = heapAfter,
            HeapBeforePercent = heapBeforePercent,
            HeapAfterPercent = heapAfterPercent,
            MaxCapacityMb = capacityMb,
            DurationMs = durationMs
        });

        if (name.StartsWith("Pause", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Full GC", StringComparison.OrdinalIgnoreCase))
        {
            AddSyntheticPhase(gcEvent, seconds, name, TranslateG1PausePhase(name), durationMs);
        }
    }

    private static void ParsePhaseLine(GcEvent gcEvent, string generation, double seconds, string message, bool isG1Marking)
    {
        var match = PhaseDurationRegex.Match(message.Trim());
        if (!match.Success)
        {
            return;
        }

        var rawName = NormalizePhaseName(match.Groups["name"].Value.Trim());
        if (isG1Marking && rawName.Equals("Concurrent Mark", StringComparison.OrdinalIgnoreCase))
        {
            // The detailed G1 marking sub-phases cover this interval, so skip it to avoid double counting.
            return;
        }

        var durationMs = ConvertToMs(match.Groups["value"].Value, match.Groups["unit"].Value);
        gcEvent.Phases.Add(new GcPhase
        {
            NameRaw = rawName,
            Name = TranslatePhase(rawName),
            Generation = generation,
            EndSeconds = seconds,
            DurationMs = durationMs
        });
    }

    private static void ParseHeapLine(
        GcEvent gcEvent,
        string generation,
        string message,
        Dictionary<string, string> heapSections,
        Dictionary<int, G1RegionAccumulator> g1Regions,
        double? g1RegionSizeMb)
    {
        if (ParseG1RegionLine(gcEvent, message, g1Regions))
        {
            return;
        }

        var sectionKey = $"{gcEvent.Id}:{generation}";

        if (message.Contains("Young Generation Statistics:", StringComparison.OrdinalIgnoreCase))
        {
            heapSections[sectionKey] = "Young";
            EnsureGeneration(gcEvent, "Young");
            return;
        }

        if (message.Contains("Old Generation Statistics:", StringComparison.OrdinalIgnoreCase))
        {
            heapSections[sectionKey] = "Old";
            EnsureGeneration(gcEvent, "Old");
            return;
        }

        if (message.Contains("Heap Statistics:", StringComparison.OrdinalIgnoreCase))
        {
            heapSections[sectionKey] = "Heap";
            return;
        }

        if (message.StartsWith("Max Capacity:", StringComparison.OrdinalIgnoreCase))
        {
            gcEvent.MaxCapacityMb = ParseCapacity(message);
            return;
        }

        if (message.StartsWith("Soft Max Capacity:", StringComparison.OrdinalIgnoreCase))
        {
            gcEvent.SoftMaxCapacityMb = ParseCapacity(message);
            return;
        }

        if (!heapSections.TryGetValue(sectionKey, out var section))
        {
            return;
        }

        var rowName = GetHeapRowName(message);
        if (rowName is null)
        {
            return;
        }

        var tokens = ParseMemoryTokens(message);
        if (tokens.Count == 0)
        {
            return;
        }

        if (section == "Heap" && rowName == "Used")
        {
            gcEvent.HeapBeforeMb ??= tokens[0].ValueMb;
            gcEvent.HeapAfterMb ??= tokens.Count >= 4 ? tokens[3].ValueMb : tokens[^1].ValueMb;
            if (tokens.Count >= 5)
            {
                gcEvent.HeapHighMb = Math.Max(gcEvent.HeapHighMb ?? tokens[4].ValueMb, tokens[4].ValueMb);
            }

            return;
        }

        if (section is not ("Young" or "Old"))
        {
            return;
        }

        var stats = EnsureGeneration(gcEvent, section);
        var lastValue = tokens[^1].ValueMb;

        switch (rowName)
        {
            case "Used":
                stats.UsedBeforeMb = tokens[0].ValueMb;
                stats.UsedAfterMb = lastValue;
                break;
            case "Live":
                stats.LiveMb = lastValue;
                break;
            case "Garbage":
                stats.GarbageMb = lastValue;
                break;
            case "Allocated":
                stats.AllocatedMb = lastValue;
                break;
            case "Reclaimed":
                stats.ReclaimedMb = lastValue;
                break;
            case "Promoted":
                stats.PromotedMb = lastValue;
                break;
            case "Compacted":
                stats.CompactedMb = lastValue;
                break;
        }
    }

    private static bool ParseG1RegionLine(GcEvent gcEvent, string message, Dictionary<int, G1RegionAccumulator> g1Regions)
    {
        var match = G1RegionRegex.Match(message.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!g1Regions.TryGetValue(gcEvent.Id, out var regions))
        {
            regions = new G1RegionAccumulator();
            g1Regions[gcEvent.Id] = regions;
        }

        var before = ParseDouble(match.Groups["before"].Value);
        var after = ParseDouble(match.Groups["after"].Value);
        var name = match.Groups["name"].Value;

        if (name is "Eden" or "Survivor")
        {
            regions.YoungBefore += before;
            regions.YoungAfter += after;
        }
        else
        {
            regions.OldBefore += before;
            regions.OldAfter += after;
        }

        return true;
    }

    private static void ApplyG1RegionSize(GcEvent gcEvent, Dictionary<int, G1RegionAccumulator> g1Regions, double? g1RegionSizeMb)
    {
        if (!gcEvent.HeapAfterMb.HasValue || !g1Regions.TryGetValue(gcEvent.Id, out var regions))
        {
            return;
        }

        var totalAfterRegions = regions.YoungAfter + regions.OldAfter;
        if (totalAfterRegions <= 0)
        {
            return;
        }

        var regionSizeMb = g1RegionSizeMb ?? NormalizeG1RegionSizeMb(gcEvent.HeapAfterMb.Value / totalAfterRegions);
        ApplyRegionStats(gcEvent, "Young", regions.YoungBefore, regions.YoungAfter, regionSizeMb);
        ApplyRegionStats(gcEvent, "Old", regions.OldBefore, regions.OldAfter, regionSizeMb);
    }

    private static double NormalizeG1RegionSizeMb(double estimatedRegionSizeMb)
    {
        foreach (var candidate in G1RegionSizeMbCandidates)
        {
            if (estimatedRegionSizeMb <= candidate)
            {
                return candidate;
            }
        }

        return G1RegionSizeMbCandidates[^1];
    }

    private static void ApplyRegionStats(GcEvent gcEvent, string generation, double beforeRegions, double afterRegions, double regionSizeMb)
    {
        var stats = EnsureGeneration(gcEvent, generation);
        stats.UsedBeforeMb = beforeRegions * regionSizeMb;
        stats.UsedAfterMb = afterRegions * regionSizeMb;
        stats.ReclaimedMb = Math.Max(0, stats.UsedBeforeMb.Value - stats.UsedAfterMb.Value);
    }

    private static void ParseMetaspace(GcEvent gcEvent, string message)
    {
        var match = MetaspaceRegex.Match(message);
        if (match.Success)
        {
            gcEvent.Metaspace = new GcMetaspaceSample
            {
                UsedMb = ConvertToMb(match.Groups["used"].Value, match.Groups["usedUnit"].Value),
                CommittedMb = ConvertToMb(match.Groups["committed"].Value, match.Groups["committedUnit"].Value),
                ReservedMb = ConvertToMb(match.Groups["reserved"].Value, match.Groups["reservedUnit"].Value)
            };
            return;
        }

        var g1Match = G1MetaspaceRegex.Match(message);
        if (!g1Match.Success)
        {
            return;
        }

        var afterUsed = ConvertToMb(g1Match.Groups["after"].Value, g1Match.Groups["afterUnit"].Value);
        var afterCommitted = ConvertToMb(g1Match.Groups["afterCommitted"].Value, g1Match.Groups["afterCommittedUnit"].Value);
        gcEvent.Metaspace = new GcMetaspaceSample
        {
            UsedMb = afterUsed,
            CommittedMb = afterCommitted,
            ReservedMb = null
        };
    }

    private static void ParseAllocation(GcEvent gcEvent, string message)
    {
        if (!message.Contains("Allocation Stalls:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var match = AllocationStallsRegex.Match(message);
        if (!match.Success)
        {
            return;
        }

        var peak = match.Groups["value"].Captures
            .Select(capture => int.Parse(capture.Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();
        gcEvent.AllocationStallPeak = Math.Max(gcEvent.AllocationStallPeak, peak);
    }

    private static void ParseLoad(GcEvent gcEvent, string message)
    {
        var match = LoadRegex.Match(message);
        if (!match.Success)
        {
            return;
        }

        gcEvent.Load = new GcLoadSample
        {
            OneMinute = ParseDouble(match.Groups["one"].Value),
            FiveMinutes = ParseDouble(match.Groups["five"].Value),
            FifteenMinutes = ParseDouble(match.Groups["fifteen"].Value)
        };
    }

    private static void ParseMmu(GcEvent gcEvent, string message)
    {
        foreach (Match match in MmuRegex.Matches(message))
        {
            gcEvent.MmuSamples.Add(new GcMmuSample
            {
                WindowMs = ParseDouble(match.Groups["window"].Value),
                Percent = ParseDouble(match.Groups["percent"].Value)
            });
        }
    }

    private static void ParseRelocation(GcEvent gcEvent, string message)
    {
        var tenuringMatch = TenuringRegex.Match(message);
        if (tenuringMatch.Success)
        {
            gcEvent.TenuringThreshold = int.Parse(tenuringMatch.Groups["threshold"].Value, CultureInfo.InvariantCulture);
            return;
        }

        var ageMatch = AgeRowRegex.Match(message.Trim());
        if (!ageMatch.Success)
        {
            return;
        }

        gcEvent.AgeTable.Add(new GcAgeTableRow
        {
            Name = ageMatch.Groups["name"].Value,
            LiveMb = ConvertToMb(ageMatch.Groups["live"].Value, ageMatch.Groups["liveUnit"].Value),
            GarbageMb = ConvertToMb(ageMatch.Groups["garbage"].Value, ageMatch.Groups["garbageUnit"].Value)
        });
    }

    private static GcGenerationStats EnsureGeneration(GcEvent gcEvent, string name)
    {
        if (!gcEvent.GenerationStats.TryGetValue(name, out var stats))
        {
            stats = new GcGenerationStats { Name = name };
            gcEvent.GenerationStats[name] = stats;
        }

        return stats;
    }

    private static void AddSyntheticPhase(GcEvent gcEvent, double seconds, string rawName, string name, double durationMs)
    {
        if (gcEvent.Phases.Any(phase => phase.NameRaw == rawName && Math.Abs(phase.DurationMs - durationMs) < 0.001))
        {
            return;
        }

        gcEvent.Phases.Add(new GcPhase
        {
            NameRaw = rawName,
            Name = name,
            Generation = "",
            EndSeconds = seconds,
            DurationMs = durationMs
        });
    }

    private static double? SumExplicitGenerationReclaimed(GcEvent gcEvent)
    {
        var values = gcEvent.GenerationStats.Values
            .Where(stats => stats.ReclaimedMb.HasValue)
            .Select(stats => stats.ReclaimedMb!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Sum();
    }

    private static string? GetHeapRowName(string message)
    {
        var trimmed = message.TrimStart();
        foreach (var rowName in new[] { "Capacity", "Free", "Used", "Live", "Garbage", "Allocated", "Reclaimed", "Promoted", "Compacted" })
        {
            if (trimmed.StartsWith(rowName + ":", StringComparison.OrdinalIgnoreCase))
            {
                return rowName;
            }
        }

        return null;
    }

    private static List<(double ValueMb, double? Percent)> ParseMemoryTokens(string message)
    {
        return MemoryTokenRegex.Matches(message)
            .Select(match => (
                ConvertToMb(match.Groups["value"].Value, match.Groups["unit"].Value),
                (double?)ParseDouble(match.Groups["percent"].Value)))
            .ToList();
    }

    private static double? ParseCapacity(string message)
    {
        var match = CapacityRegex.Match(message);
        if (!match.Success)
        {
            return null;
        }

        return ConvertToMb(match.Groups["value"].Value, match.Groups["unit"].Value);
    }

    private static bool TryParseG1HeapRegionSize(string message, out double regionSizeMb)
    {
        var match = G1HeapRegionSizeRegex.Match(message);
        if (match.Success)
        {
            regionSizeMb = ConvertToMb(match.Groups["value"].Value, match.Groups["unit"].Value);
            return true;
        }

        regionSizeMb = 0;
        return false;
    }

    private static string NormalizeGeneration(string raw)
    {
        return raw switch
        {
            "y" or "Y" => "Young",
            "o" or "O" => "Old",
            _ => ""
        };
    }

    private static string NormalizePhaseName(string rawName)
    {
        if (rawName.StartsWith("Young Generation ", StringComparison.OrdinalIgnoreCase))
        {
            return "Young Generation";
        }

        if (rawName.StartsWith("Old Generation ", StringComparison.OrdinalIgnoreCase))
        {
            return "Old Generation";
        }

        return rawName;
    }

    private static string TranslatePhase(string rawName)
    {
        return rawName switch
        {
            "Pause Mark Start" => "暂停标记开始",
            "Pause Mark End" => "暂停标记结束",
            "Pause Relocate Start" => "暂停重定位开始",
            "Concurrent Mark" => "并发标记",
            "Concurrent Mark Free" => "并发标记释放",
            "Concurrent Reset Relocation Set" => "并发重置重定位集合",
            "Concurrent Select Relocation Set" => "并发选择重定位集合",
            "Concurrent Relocate" => "并发重定位",
            "Young Generation" => "年轻代阶段",
            "Old Generation" => "老年代阶段",
            "Pre Evacuate Collection Set" => "疏散前处理",
            "Merge Heap Roots" => "合并堆根",
            "Evacuate Collection Set" => "疏散回收集合",
            "Post Evacuate Collection Set" => "疏散后处理",
            "Other" => "其他阶段",
            "Concurrent Scan Root Regions" => "并发扫描根区域",
            "Concurrent Mark From Roots" => "并发根标记",
            "Concurrent Preclean" => "并发预清理",
            "Concurrent Rebuild Remembered Sets and Scrub Regions" => "并发重建记忆集",
            "Concurrent Clear Claimed Marks" => "并发清除标记声明",
            "Concurrent Cleanup for Next Mark" => "并发清理下次标记",
            _ => rawName
        };
    }

    private static string TranslateG1Type(string name)
    {
        if (name.StartsWith("Pause Young", StringComparison.OrdinalIgnoreCase))
        {
            return name.Contains("(Mixed)", StringComparison.OrdinalIgnoreCase) ? "混合GC" : "年轻代GC";
        }

        if (name.StartsWith("Pause Remark", StringComparison.OrdinalIgnoreCase))
        {
            return "Remark暂停";
        }

        if (name.StartsWith("Pause Cleanup", StringComparison.OrdinalIgnoreCase))
        {
            return "清理暂停";
        }

        if (name.StartsWith("Pause Full", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Full GC", StringComparison.OrdinalIgnoreCase))
        {
            return "完全GC";
        }

        if (name.StartsWith("Concurrent Mark Cycle", StringComparison.OrdinalIgnoreCase))
        {
            return "并发标记周期";
        }

        if (name.StartsWith("Concurrent", StringComparison.OrdinalIgnoreCase))
        {
            return "并发GC";
        }

        return name;
    }

    private static string TranslateG1Cause(string name)
    {
        var parts = Regex.Matches(name, @"\((?<part>[^)]+)\)")
            .Select(match => TranslateG1CausePart(match.Groups["part"].Value))
            .ToList();

        if (parts.Count > 0)
        {
            return string.Join(" / ", parts);
        }

        if (name.StartsWith("Pause Remark", StringComparison.OrdinalIgnoreCase))
        {
            return "Remark";
        }

        if (name.StartsWith("Pause Cleanup", StringComparison.OrdinalIgnoreCase))
        {
            return "Cleanup";
        }

        if (name.StartsWith("Concurrent Mark Cycle", StringComparison.OrdinalIgnoreCase))
        {
            return "并发标记周期";
        }

        return name;
    }

    private static string TranslateG1CausePart(string part)
    {
        return part switch
        {
            "Prepare Mixed" => "准备混合回收",
            "Mixed" => "混合回收",
            "Concurrent Start" => "并发标记启动",
            "G1 Evacuation Pause" => "G1疏散暂停",
            "G1 Humongous Allocation" => "G1巨型对象分配",
            "G1 Preventive Collection" => "G1预防性回收",
            _ => part
        };
    }

    private static string TranslateG1PausePhase(string name)
    {
        if (name.StartsWith("Pause Young", StringComparison.OrdinalIgnoreCase))
        {
            return "G1年轻代暂停";
        }

        if (name.StartsWith("Pause Remark", StringComparison.OrdinalIgnoreCase))
        {
            return "G1 Remark暂停";
        }

        if (name.StartsWith("Pause Cleanup", StringComparison.OrdinalIgnoreCase))
        {
            return "G1清理暂停";
        }

        if (name.StartsWith("Pause Full", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Full GC", StringComparison.OrdinalIgnoreCase))
        {
            return "G1完全GC暂停";
        }

        return name;
    }

    private static double ConvertToMb(string value, string unit)
    {
        var number = ParseDouble(value);
        return unit switch
        {
            "K" => number / 1024d,
            "M" => number,
            "G" => number * 1024d,
            "T" => number * 1024d * 1024d,
            "P" => number * 1024d * 1024d * 1024d,
            _ => number
        };
    }

    private static double ConvertToMs(string value, string unit)
    {
        var number = ParseDouble(value);
        return unit == "s" ? number * 1000d : number;
    }

    private static double ParseDouble(string value)
    {
        return double.Parse(value, CultureInfo.InvariantCulture);
    }

    private sealed class G1RegionAccumulator
    {
        public double YoungBefore { get; set; }
        public double YoungAfter { get; set; }
        public double OldBefore { get; set; }
        public double OldAfter { get; set; }
    }
}
