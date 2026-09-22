using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using gcviewer.Models;
using gcviewer.Services;
using Microsoft.Win32;

namespace gcviewer.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly GcLogParser parser = new();
    private readonly GcReportCalculator calculator = new();
    private GcParseResult? parseResult;
    private string selectedFilePath = "";
    private string statusMessage = "请选择一个 GC 日志文件开始分析。";
    private double progressValue;
    private bool isBusy;
    private GcReport? report;
    private TimeRangeOption? selectedTimeRange;

    public MainViewModel()
    {
        BrowseCommand = new RelayCommand(async () => await BrowseAsync(), () => !IsBusy);
        ReanalyzeCommand = new RelayCommand(async () => await AnalyzeCurrentFileAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedFilePath));
        TimeRanges.Add(new TimeRangeOption("全部", null));
        selectedTimeRange = TimeRanges[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand BrowseCommand { get; }
    public ICommand ReanalyzeCommand { get; }
    public ObservableCollection<TimeRangeOption> TimeRanges { get; } = new();

    public string SelectedFilePath
    {
        get => selectedFilePath;
        set
        {
            if (selectedFilePath == value)
            {
                return;
            }

            selectedFilePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedFileName));
            OnPropertyChanged(nameof(FileSummary));
            RaiseCommandStates();
        }
    }

    public string SelectedFileName => string.IsNullOrWhiteSpace(SelectedFilePath) ? "未选择文件" : Path.GetFileName(SelectedFilePath);

    public string StatusMessage
    {
        get => statusMessage;
        set
        {
            if (statusMessage == value)
            {
                return;
            }

            statusMessage = value;
            OnPropertyChanged();
        }
    }

    public double ProgressValue
    {
        get => progressValue;
        set
        {
            if (Math.Abs(progressValue - value) < 0.01)
            {
                return;
            }

            progressValue = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        set
        {
            if (isBusy == value)
            {
                return;
            }

            isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotBusy));
            RaiseCommandStates();
        }
    }

    public bool IsNotBusy => !IsBusy;

    public GcReport? Report
    {
        get => report;
        set
        {
            if (report == value)
            {
                return;
            }

            report = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReport));
            OnPropertyChanged(nameof(FileSummary));
            OnPropertyChanged(nameof(RangeSummary));
        }
    }

    public bool HasReport => Report is not null;

    public TimeRangeOption? SelectedTimeRange
    {
        get => selectedTimeRange;
        set
        {
            if (selectedTimeRange == value)
            {
                return;
            }

            selectedTimeRange = value;
            OnPropertyChanged();
            RebuildReportForRange();
        }
    }

    public string FileSummary
    {
        get
        {
            if (Report is null)
            {
                return "选择日志后会在这里显示文件大小、行数和解析结果。";
            }

            return $"{Report.FileName} · {GcText.FormatFileSize(Report.FileSizeBytes)} · {Report.LineCount:N0} 行 · 当前范围 {Report.CompletedCycleCount:N0} 个 cycle / {Report.CompletedEventCount:N0} 条完成行";
        }
    }

    public string RangeSummary
    {
        get
        {
            if (Report is null)
            {
                return "";
            }

            return $"时间轴 {GcText.FormatRelativeTime(0)} 到 {GcText.FormatRelativeTime(Report.SpanSeconds)}";
        }
    }

    private async Task BrowseAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 GC 日志文件",
            Filter = "GC 日志 (*.log;*.txt)|*.log;*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SelectedFilePath = dialog.FileName;
        await AnalyzeCurrentFileAsync();
    }

    private async Task AnalyzeCurrentFileAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilePath))
        {
            StatusMessage = "请先选择 GC 日志文件。";
            return;
        }

        try
        {
            IsBusy = true;
            ProgressValue = 0;
            Report = null;
            StatusMessage = "正在读取并解析日志...";

            var progress = new Progress<GcParseProgress>(item =>
            {
                ProgressValue = item.Percent;
                StatusMessage = $"正在解析：{item.LinesRead:N0} 行，{item.Percent:0.0}%";
            });

            var result = await Task.Run(() => parser.Parse(SelectedFilePath, progress));
            parseResult = result;
            BuildTimeRanges(result);

            SelectedTimeRange = TimeRanges[0];
            Report = calculator.Build(result);

            var warningText = result.Warnings.Count == 0 ? "" : $" 提示：{string.Join("；", result.Warnings)}";
            StatusMessage = $"分析完成：识别 {result.Events.Count:N0} 个 GC id，当前范围 {Report.CompletedCycleCount:N0} 个 cycle / {Report.CompletedEventCount:N0} 条完成行。{warningText}";
            ProgressValue = 100;
        }
        catch (Exception ex)
        {
            StatusMessage = $"分析失败：{ex.Message}";
            Report = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildTimeRanges(GcParseResult result)
    {
        TimeRanges.Clear();
        TimeRanges.Add(new TimeRangeOption("全部", null));

        var completed = result.Events.Where(gc => gc.IsCompleted).OrderBy(gc => gc.EndSeconds).ToList();
        if (completed.Count >= 2)
        {
            var span = completed.Last().EndSeconds - completed.First().EndSeconds;
            if (span > 2 * 60 * 60)
            {
                TimeRanges.Add(new TimeRangeOption("最近2小时", 2 * 60 * 60));
            }

            if (span > 4 * 60 * 60)
            {
                TimeRanges.Add(new TimeRangeOption("最近4小时", 4 * 60 * 60));
            }

            if (span > 6 * 60 * 60)
            {
                TimeRanges.Add(new TimeRangeOption("最近6小时", 6 * 60 * 60));
            }
        }

        selectedTimeRange = TimeRanges[0];
        OnPropertyChanged(nameof(TimeRanges));
        OnPropertyChanged(nameof(SelectedTimeRange));
    }

    private void RebuildReportForRange()
    {
        if (parseResult is null || SelectedTimeRange is null)
        {
            return;
        }

        var completed = parseResult.Events.Where(gc => gc.IsCompleted).OrderBy(gc => gc.EndSeconds).ToList();
        if (completed.Count == 0)
        {
            Report = calculator.Build(parseResult);
            return;
        }

        if (!SelectedTimeRange.DurationSeconds.HasValue)
        {
            Report = calculator.Build(parseResult);
            StatusMessage = $"已切换时间范围：全部，{Report.CompletedCycleCount:N0} 个 cycle / {Report.CompletedEventCount:N0} 条完成行。";
            return;
        }

        var end = completed.Last().EndSeconds;
        var start = Math.Max(completed.First().EndSeconds, end - SelectedTimeRange.DurationSeconds.Value);
        Report = calculator.Build(parseResult, start, end);
        StatusMessage = $"已切换时间范围：{SelectedTimeRange.Name}，{Report.CompletedCycleCount:N0} 个 cycle / {Report.CompletedEventCount:N0} 条完成行。";
    }

    private void RaiseCommandStates()
    {
        if (BrowseCommand is RelayCommand browse)
        {
            browse.RaiseCanExecuteChanged();
        }

        if (ReanalyzeCommand is RelayCommand reanalyze)
        {
            reanalyze.RaiseCanExecuteChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record TimeRangeOption(string Name, double? DurationSeconds)
{
    public override string ToString() => Name;
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool>? canExecute;

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return canExecute?.Invoke() ?? true;
    }

    public async void Execute(object? parameter)
    {
        await execute();
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
