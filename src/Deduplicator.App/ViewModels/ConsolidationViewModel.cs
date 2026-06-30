using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

public sealed record RuleOption(RepresentativeRule Value, string Label);

/// <summary>Консолидация в чистый архив (экран 8).</summary>
public partial class ConsolidationViewModel : PageViewModel
{
    private readonly IConsolidationService _consolidation;
    private readonly IVolumeService _volumes;
    private CancellationTokenSource? _cts;

    public override string Title => Strings.ConsolidationTitle;

    public ObservableCollection<VolumeCheckItem> Sources { get; } = new();
    public ObservableCollection<RuleOption> Rules { get; } = new();
    public ObservableCollection<ConsolidationPreviewItem> Preview { get; } = new();

    /// <summary>Устанавливается представлением — выбор папки через системный диалог.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    [ObservableProperty] private RuleOption? _selectedRule;
    [ObservableProperty] private string? _targetPath;
    [ObservableProperty] private bool _preserveStructure = true;
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private bool _isExecuting;
    [ObservableProperty] private string _uniqueText = "0";
    [ObservableProperty] private string _skippedText = "0";
    [ObservableProperty] private string _bytesCopyText = "—";
    [ObservableProperty] private string _bytesSavedText = "—";
    [ObservableProperty] private double _execProgress;
    [ObservableProperty] private string? _execCurrent;
    [ObservableProperty] private string? _reportText;

    private ConsolidationPlan? _plan;

    public ConsolidationViewModel(IConsolidationService consolidation, IVolumeService volumes)
    {
        _consolidation = consolidation;
        _volumes = volumes;
        Rules.Add(new RuleOption(RepresentativeRule.Oldest, Strings.RuleOldest));
        Rules.Add(new RuleOption(RepresentativeRule.Newest, Strings.RuleNewest));
        Rules.Add(new RuleOption(RepresentativeRule.ShortestPath, Strings.RuleShortestPath));
        Rules.Add(new RuleOption(RepresentativeRule.LongestName, Strings.RuleLongestName));
        SelectedRule = Rules[0];
    }

    public override void OnNavigatedTo() => _ = LoadVolumesAsync();

    private async Task LoadVolumesAsync()
    {
        var catalog = await Task.Run(() => _volumes.SyncAndGetCatalog());
        Sources.Clear();
        foreach (var v in catalog) Sources.Add(new VolumeCheckItem(v));
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder();
        if (!string.IsNullOrEmpty(folder)) TargetPath = folder;
    }

    [RelayCommand]
    private async Task BuildPlanAsync()
    {
        var ids = Sources.Where(s => s.IsChecked).Select(s => s.Id).ToList();
        // #23: молчаливое бездействие заменяем понятной подсказкой, что именно не заполнено.
        if (ids.Count == 0 || string.IsNullOrWhiteSpace(TargetPath))
        {
            ReportText = Strings.ConsolidationNeedInput;
            return;
        }

        var options = new ConsolidationOptions
        {
            SourceVolumeIds = ids,
            TargetPath = TargetPath!,
            Rule = SelectedRule?.Value ?? RepresentativeRule.Oldest,
            PreserveFolderStructure = PreserveStructure
        };

        ReportText = null;
        var plan = await Task.Run(() => _consolidation.BuildPlan(options));
        _plan = plan;

        UniqueText = $"{plan.UniqueCount:N0}";
        SkippedText = $"{plan.SkippedCount:N0}";
        BytesCopyText = Format.Bytes(plan.BytesToCopy);
        BytesSavedText = Format.Bytes(plan.BytesSaved);

        Preview.Clear();
        foreach (var item in plan.ToCopy.Take(200))
            Preview.Add(new ConsolidationPreviewItem(item, true));
        foreach (var item in plan.ToSkip.Take(100))
            Preview.Add(new ConsolidationPreviewItem(item, false));

        HasPlan = true;
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (_plan is null || string.IsNullOrWhiteSpace(TargetPath)) return;

        var options = new ConsolidationOptions
        {
            SourceVolumeIds = Sources.Where(s => s.IsChecked).Select(s => s.Id).ToList(),
            TargetPath = TargetPath!,
            Rule = SelectedRule?.Value ?? RepresentativeRule.Oldest,
            PreserveFolderStructure = PreserveStructure
        };

        _cts = new CancellationTokenSource();
        IsExecuting = true;
        ReportText = null;

        try
        {
            var report = await Task.Run(() => _consolidation.Execute(_plan, options, (done, total, name) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ExecProgress = total == 0 ? 0 : done * 100.0 / total;
                    ExecCurrent = name;
                });
            }, _cts.Token));

            ReportText = $"{Strings.ConsolidationDone}. {Strings.Copied}: {report.Copied:N0}, " +
                         $"{Strings.PlanSkipped}: {report.Skipped:N0}, {Strings.FailedItems}: {report.Failed:N0}. " +
                         $"{Strings.PlanBytesCopy}: {Format.Bytes(report.BytesCopied)}";
        }
        catch (OperationCanceledException)
        {
            // #22: после отмены UI не должен зависнуть в состоянии «выполняется».
            ReportText = Strings.ConsolidationCancelled;
        }
        catch (Exception ex)
        {
            ReportText = $"{Strings.FailedItems}: {ex.Message}";
        }
        finally
        {
            IsExecuting = false;
            ExecCurrent = null;
        }
    }

    [RelayCommand]
    private void CancelExecute() => _cts?.Cancel();
}

/// <summary>Строка предпросмотра плана консолидации.</summary>
public sealed class ConsolidationPreviewItem
{
    public ConsolidationPreviewItem(ConsolidationItem item, bool willCopy)
    {
        Name = item.File.Name;
        Source = item.File.VolumeLabel ?? $"Том #{item.File.VolumeId}";
        Target = willCopy ? item.TargetRelPath : "—";
        SizeText = Format.Bytes(item.File.Size);
        ActionText = willCopy ? Strings.PlanUnique : Strings.PlanSkipped;
        WillCopy = willCopy;
    }

    public string Name { get; }
    public string Source { get; }
    public string Target { get; }
    public string SizeText { get; }
    public string ActionText { get; }
    public bool WillCopy { get; }
}
