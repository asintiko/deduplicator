using CommunityToolkit.Mvvm.ComponentModel;

namespace Deduplicator.App.Infrastructure;

/// <summary>Базовый класс для страниц-вью-моделей.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Вызывается при переходе на страницу — место для (пере)загрузки данных.</summary>
    public virtual void OnNavigatedTo() { }

    /// <summary>Вызывается при уходе со страницы — место для очистки незавершённых диалогов/операций.</summary>
    public virtual void OnNavigatedFrom() { }
}
