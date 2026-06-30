using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Deduplicator.App.ViewModels.Items;

namespace Deduplicator.App.Views;

public partial class ExplorerView : UserControl
{
    public ExplorerView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Раскрытие/сворачивание папки двойным кликом. В Avalonia TreeView по умолчанию двойной клик
    /// по строке узел не раскрывает; делаем это явно и надёжно — управляем контейнером напрямую
    /// (локальное значение перекрывает капризы style-биндинга) и параллельно триггерим ленивую
    /// подгрузку через модель.
    /// </summary>
    private void OnNodeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual v) return;
        var item = v.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item?.DataContext is not ExplorerNode node || !node.IsFolder) return;

        bool expand = !item.IsExpanded;
        item.IsExpanded = expand; // визуальное состояние контейнера (надёжно)
        node.IsExpanded = expand; // гарантированно запускает ленивую подгрузку детей
        e.Handled = true;
    }
}
