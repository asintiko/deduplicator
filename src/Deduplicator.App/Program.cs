using System.Runtime.InteropServices;
using Avalonia;

namespace Deduplicator.App;

internal static class Program
{
    // Держим mutex живым всё время работы процесса (иначе GC отпустит и защита исчезнет).
    private static Mutex? _singleInstanceMutex;

    // Не использовать Avalonia/сторонние API до вызова BuildAvaloniaApp.
    [STAThread]
    public static void Main(string[] args)
    {
        // Один экземпляр на машину: стартовая чистка второго экземпляра (MarkUnfinishedAsFailed +
        // ClearGroupsNotInCompletedSessions) помечала АКТИВНУЮ сессию первого как Failed и удаляла
        // группы идущего скана — тот завершался «успехом» с неполными результатами (аудит 2026-07-02).
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Global\Deduplicator.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBoxW(IntPtr.Zero,
                "Deduplicator уже запущен.\nПереключитесь на открытое окно программы.",
                "Deduplicator", 0x00000040 /* MB_ICONINFORMATION */);
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
