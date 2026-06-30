using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

/// <summary>Пункт боковой навигации.</summary>
public sealed record NavItem(string Title, string IconKey, PageViewModel Page, string Tip);

/// <summary>Шаг обучения: заголовок, текст, на какой раздел переключиться,
/// имя подсвечиваемого контрола (x:Name) и короткая подсказка-указатель.</summary>
public sealed record OnboardingStep(
    string Title,
    string Body,
    int NavIndex,
    string TargetName,
    string Hint);

/// <summary>Главная вью-модель: боковая навигация и активная страница.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IReadOnlyList<OnboardingStep> _onboardingSteps;

    public string AppName => Strings.AppName;
    public string AppTagline => Strings.AppTagline;
    public string HelpText => Strings.HelpButton;

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private bool _isOnboardingVisible;
    [ObservableProperty] private int _onboardingIndex;

    private bool _suppress;

    public MainWindowViewModel(
        NavigationService nav,
        ISettingsService settingsService,
        HomeViewModel home,
        VolumesViewModel volumes,
        ExplorerViewModel explorer,
        ScanViewModel scan,
        ResultsViewModel results,
        StatisticsViewModel statistics,
        ConsolidationViewModel consolidation,
        JournalViewModel journal,
        SettingsViewModel settings,
        DiskHealthViewModel diskHealth,
        CleanupViewModel cleanup,
        CrossDiskViewModel crossDisk)
    {
        _settings = settingsService;
        NavItems = new ObservableCollection<NavItem>
        {
            new("Главная", "IconGrid", home, "Крупные действия и краткая сводка"),
            new("Найти дубликаты", "IconSearch", scan, "Проверить выбранные диски на копии файлов"),
            new("Результаты", "IconLayers", results, "Посмотреть найденные копии и убрать лишнее"),
            new("Диски", "IconHdd", volumes, "Список известных дисков и сохранённых файлов"),
            new("Мусор", "IconBroom", cleanup, "Найти временные файлы, пустые папки и битые ярлыки"),
            new("Проверить диски", "IconHealth", diskHealth, "Посмотреть состояние накопителей"),
            new(Strings.NavSettings, "IconSettings", settings, "Основные параметры безопасности и поиска"),
        };

        _onboardingSteps = new[]
        {
            new OnboardingStep(
                "Начните отсюда",
                "Эта кнопка запускает поиск дублей — одинаковые файлы, похожие фото, музыка и документы. Это главное действие программы.",
                0, "OnboardingHomeDuplicates", "Нажмите сюда, чтобы начать"),
            new OnboardingStep(
                "Выберите диски",
                "Отметьте диски для проверки. Внешние выбираются автоматически, системный — вручную, если это действительно нужно.",
                1, "OnboardingScanDisks", "Проверьте галочки дисков"),
            new OnboardingStep(
                "Сначала отметить",
                "После поиска эта кнопка отметит лишние точные копии. В каждой группе одна копия всегда останется — программа не даст удалить всё.",
                2, "OnboardingAutoSelect", "Отмечает лишние копии"),
            new OnboardingStep(
                "Потом удалить",
                "Удаление срабатывает только после подтверждения и уходит в Корзину. Ничего не пропадает внезапно.",
                2, "OnboardingDeleteSelected", "Удаляет только отмеченное"),
            new OnboardingStep(
                "Архив дисков",
                "Программа запоминает диски и файлы. Список остаётся доступен даже после того, как диск отключили.",
                3, "OnboardingVolumeList", "Сохранённые диски"),
            new OnboardingStep(
                "Очистка мусора",
                "Пустые папки, временные файлы и битые ярлыки. Тоже удаляются только после подтверждения и уходят в Корзину.",
                4, "OnboardingCleanupOptions", "Что искать"),
            new OnboardingStep(
                "Состояние дисков",
                "Здоровье накопителей по данным SMART. Если статус станет жёлтым или красным — заранее сделайте резервную копию важных файлов.",
                5, "OnboardingHealthList", "Здоровье накопителей"),
            new OnboardingStep(
                "Главное меню",
                "Слева — основные разделы. Это короткое обучение всегда можно повторить кнопкой внизу слева.",
                0, "OnboardingMainNav", "Разделы программы"),
        };

        nav.Navigator = SetPage;
        SelectedNav = NavItems[0];
        IsOnboardingVisible = !_settings.Current.OnboardingCompleted;
        RefreshOnboardingProperties();
    }

    public int OnboardingStepNumber => OnboardingIndex + 1;
    public int OnboardingStepCount => _onboardingSteps.Count;
    public string OnboardingProgressText => $"Шаг {OnboardingStepNumber} из {OnboardingStepCount}";
    public double OnboardingProgress => OnboardingStepCount == 0 ? 0 : OnboardingStepNumber * 100.0 / OnboardingStepCount;
    public string OnboardingTitle => _onboardingSteps[OnboardingIndex].Title;
    public string OnboardingBody => _onboardingSteps[OnboardingIndex].Body;
    public string OnboardingHint => _onboardingSteps[OnboardingIndex].Hint;
    public string OnboardingTargetName => _onboardingSteps[OnboardingIndex].TargetName;
    public bool OnboardingHasTarget => !string.IsNullOrEmpty(OnboardingTargetName);
    public string OnboardingNextText => OnboardingIndex == _onboardingSteps.Count - 1 ? "Готово" : "Далее";
    public bool CanGoBack => OnboardingIndex > 0;

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (_suppress || value is null) return;
        SetPage(value.Page);
    }

    private void SetPage(PageViewModel page)
    {
        var previous = CurrentPage;
        if (!ReferenceEquals(previous, page)) previous?.OnNavigatedFrom();

        _suppress = true;
        CurrentPage = page;
        SelectedNav = NavItems.FirstOrDefault(n => ReferenceEquals(n.Page, page));
        _suppress = false;
        page.OnNavigatedTo();
    }

    [RelayCommand]
    private void ShowOnboarding()
    {
        OnboardingIndex = 0;
        IsOnboardingVisible = true;
        ApplyOnboardingStep();
        RefreshOnboardingProperties();
    }

    [RelayCommand]
    private void NextOnboarding()
    {
        if (OnboardingIndex >= _onboardingSteps.Count - 1)
        {
            CompleteOnboarding();
            return;
        }

        OnboardingIndex++;
    }

    [RelayCommand]
    private void PreviousOnboarding()
    {
        if (OnboardingIndex <= 0) return;
        OnboardingIndex--;
    }

    [RelayCommand]
    private void SkipOnboarding() => CompleteOnboarding();

    partial void OnOnboardingIndexChanged(int value)
    {
        ApplyOnboardingStep();
        RefreshOnboardingProperties();
    }

    private void ApplyOnboardingStep()
    {
        if (_onboardingSteps.Count == 0) return;
        // Переключаемся на соответствующий шагу раздел — он виден затемнённым позади карточки.
        var index = _onboardingSteps[OnboardingIndex].NavIndex;
        if (index >= 0 && index < NavItems.Count)
            SelectedNav = NavItems[index];
    }

    private void CompleteOnboarding()
    {
        IsOnboardingVisible = false;
        var s = _settings.Current;
        s.OnboardingCompleted = true;
        _settings.Save(s);
    }

    private void RefreshOnboardingProperties()
    {
        OnPropertyChanged(nameof(OnboardingStepNumber));
        OnPropertyChanged(nameof(OnboardingStepCount));
        OnPropertyChanged(nameof(OnboardingProgressText));
        OnPropertyChanged(nameof(OnboardingProgress));
        OnPropertyChanged(nameof(OnboardingTitle));
        OnPropertyChanged(nameof(OnboardingBody));
        OnPropertyChanged(nameof(OnboardingHint));
        OnPropertyChanged(nameof(OnboardingTargetName));
        OnPropertyChanged(nameof(OnboardingHasTarget));
        OnPropertyChanged(nameof(OnboardingNextText));
        OnPropertyChanged(nameof(CanGoBack));
    }
}
