using System.Globalization;
using System.Text;

namespace Deduplicator.Core.Util;

/// <summary>
/// Минимальный потокобезопасный файловый лог (NFR-1: ошибки «пропускаем с логированием»).
/// Путь задаётся один раз при старте; до этого вызовы — no-op. Сам логгер никогда не бросает.
/// </summary>
public static class AppLog
{
    private static string? _path;
    private static readonly object _gate = new();

    // Логи содержат пути с кириллицей (имя пользователя). Без BOM Notepad/PowerShell читают файл
    // в системной ANSI-кодировке -> кракозябры. Пишем BOM при первом создании файла, дальше — без BOM.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static void Configure(string path) => _path = path;

    public static void Write(string message)
    {
        var path = _path;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:o} {message}{Environment.NewLine}");
            lock (_gate)
            {
                // Каталог логов мог быть удалён после старта — восстанавливаем перед записью.
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(path)) File.WriteAllText(path, string.Empty, Utf8Bom);
                File.AppendAllText(path, line, Utf8NoBom);
            }
        }
        catch { /* лог — вспомогательный, не мешаем работе */ }
    }

    public static void Error(string context, Exception ex) =>
        Write($"ERROR {context}: {ex.GetType().Name}: {ex.Message}");
}
