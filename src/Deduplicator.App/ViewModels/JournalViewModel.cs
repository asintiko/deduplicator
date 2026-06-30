using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Data;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

/// <summary>Журнал операций и восстановление из карантина (экран 9).</summary>
public partial class JournalViewModel : PageViewModel
{
    private readonly OperationLogRepository _log;
    private readonly IFileOpsService _fileOps;

    public override string Title => Strings.JournalTitle;

    public ObservableCollection<JournalItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _empty;
    [ObservableProperty] private string? _status;

    public JournalViewModel(OperationLogRepository log, IFileOpsService fileOps)
    {
        _log = log;
        _fileOps = fileOps;
    }

    public override void OnNavigatedTo() => _ = LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        var entries = await Task.Run(() => _log.GetAll());
        Items.Clear();
        foreach (var e in entries) Items.Add(new JournalItemViewModel(e));
        Empty = Items.Count == 0 ? Strings.Nothing : null;
        IsBusy = false;
    }

    [RelayCommand]
    private async Task RestoreAsync(JournalItemViewModel? item)
    {
        if (item is null || !item.CanRestore) return;
        // #24: восстановление может не удаться (исходный путь занят, файл в карантине недоступен) —
        // раньше это молчало, теперь показываем причину.
        var ok = await Task.Run(() => _fileOps.Restore(item.Model));
        Status = ok ? null : Strings.RestoreFailed;
        await LoadAsync();
    }
}
