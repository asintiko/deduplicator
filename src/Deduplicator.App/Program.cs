using Avalonia;

namespace Deduplicator.App;

internal static class Program
{
    // Не использовать Avalonia/сторонние API до вызова BuildAvaloniaApp.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
