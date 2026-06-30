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
