using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels;
using Deduplicator.App.Views;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;
using Microsoft.Extensions.DependencyInjection;

namespace Deduplicator.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            splash.Show();

            try
            {
                // Тяжёлая инициализация — в фоне, чтобы экран загрузки оставался отзывчивым.
                splash.SetStatus(Strings.SplashDb);
                Services = await Task.Run(Composition.Build);

                // Тему применяем на UI-потоке.
                ApplyTheme(Services.GetRequiredService<ISettingsService>().Current.Theme);

                splash.SetStatus(Strings.SplashVolumes);
                var volumes = Services.GetRequiredService<IVolumeService>();
                await Task.Run(() => { try { volumes.SyncAndGetCatalog(); } catch { /* носители — не критично */ } });

                // Живое отслеживание подключения/извлечения носителей.
                Services.GetRequiredService<IDriveWatcher>().Start();

                splash.SetStatus(Strings.SplashAlmost);
                await Task.Delay(450); // мягкий минимум показа, чтобы экран не «мигал»

                var main = new MainWindow
                {
                    DataContext = Services.GetRequiredService<MainWindowViewModel>()
                };
                desktop.MainWindow = main;
                main.Show();
                splash.Close();

                // «Программа сама замечает»: сторож новых дубликатов и подсказка о носителе
                // с фотографиями показывают тосты поверх главного окна. Сбой этой обвязки
                // не должен мешать запуску — приложение полноценно работает и без неё.
                try { WireBackgroundHints(main); }
                catch (Exception ex) { AppLog.Error("WireBackgroundHints", ex); }
            }
            catch (Exception ex)
            {
                // Ранее необработанное исключение здесь роняло приложение «в рабочий стол»
                // (повреждённая БД, нет прав на запись, диск полон). Теперь — понятное окно.
                AppLog.Error("Startup", ex);
                try { splash.Close(); } catch { /* ignore */ }
                ShowStartupError(desktop, ex);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Подписка тостов на события сторожа (Sentinel-lite) и подсказки «носитель с фотографиями»,
    /// плюс автозапуск сторожа, если он включён в настройках. Вкл/выкл на лету делает
    /// SettingsViewModel.Save (как fpcalc/тема).
    /// </summary>
    private void WireBackgroundHints(MainWindow main)
    {
        // А. Сторож: новый файл в наблюдаемой папке оказался копией уже известного.
        var sentinel = Services.GetRequiredService<ISentinelService>();
        sentinel.DuplicateFound += e => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            main.ShowToast(
                "Новый дубликат: " + System.IO.Path.GetFileName(e.FilePath),
                $"Такой файл уже есть ({e.ExistingCount} шт., {Infrastructure.Format.Bytes(e.Size)})."));
        var s = Services.GetRequiredService<ISettingsService>().Current;
        if (s.SentinelEnabled && s.SentinelFolders.Count > 0)
            sentinel.Start(s.SentinelFolders);

        // Б. Подключён носитель с папкой DCIM: тост-приглашение (клик — сразу к проверке);
        // надёжный основной путь — баннер с кнопкой на Главной (HomeViewModel).
        var home = Services.GetRequiredService<HomeViewModel>();
        home.PhotoDeviceDetected += label => // событие приходит уже на UI-потоке
            main.ShowToast(
                "Носитель с фотографиями",
                $"Подключён носитель с фотографиями ({label}). Нажмите, чтобы проверить на дубликаты.",
                onClick: () => home.CheckPhotoDeviceCommand.Execute(null));
    }

    /// <summary>Окно с понятной ошибкой запуска вместо краха процесса.</summary>
    private static void ShowStartupError(IClassicDesktopStyleApplicationLifetime desktop, Exception ex)
    {
        var close = new Button { Content = "Закрыть", HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = "Deduplicator — ошибка запуска",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Не удалось запустить приложение.\n\n" + ex.Message +
                               "\n\nПроверьте права на папку данных и свободное место на диске. " +
                               "Подробности — в файле журнала deduplicator.log.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    close
                }
            }
        };
        close.Click += (_, _) => window.Close();
        desktop.MainWindow = window;
        window.Show();
    }

    public void ApplyTheme(string theme) => RequestedThemeVariant = theme switch
    {
        "Light" => ThemeVariant.Light,
        "Dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };
}
