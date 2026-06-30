using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Deduplicator.App.ViewModels;

namespace Deduplicator.App.Views;

public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ResultsViewModel vm)
            {
                vm.PickSaveFile = PickSaveFileAsync;
                vm.PickDestinationFolder = PickFolderAsync;
            }
        };
    }

    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var result = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false, Title = "Папка назначения" });
        return result.Count == 0 ? null : result[0].TryGetLocalPath();
    }

    private async Task<string?> PickSaveFileAsync(string suggested)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggested,
            DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } }
        });
        return file?.TryGetLocalPath();
    }
}
