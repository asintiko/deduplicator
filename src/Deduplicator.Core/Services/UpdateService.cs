using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Deduplicator.Core.Services;

public sealed class UpdateInfo
{
    public string LatestVersion { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Notes { get; init; }
    public bool IsNewer { get; init; }
}

public interface IUpdateService
{
    string CurrentVersion { get; }

    /// <summary>
    /// Проверить наличие новой версии на GitHub Releases. Сетевой запрос выполняется
    /// только по явному вызову (офлайн-приложение, NFR-10). repo в формате "owner/name".
    /// </summary>
    Task<UpdateInfo?> CheckAsync(string repo, CancellationToken ct = default);
}

/// <summary>Проверка обновлений через публичный GitHub Releases API (без зависимостей).</summary>
public sealed class UpdateService : IUpdateService
{
    // Один переиспользуемый HttpClient (рекомендация .NET — не создавать на каждый запрос).
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Deduplicator-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public string CurrentVersion =>
        (Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly)
            .GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<UpdateInfo?> CheckAsync(string repo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/')) return null;

        var url = $"https://api.github.com/repos/{repo.Trim()}/releases/latest";
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        string htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

        var latest = NormalizeVersion(tag);
        bool isNewer = Version.TryParse(latest, out var lv) &&
                       Version.TryParse(NormalizeVersion(CurrentVersion), out var cv) &&
                       lv > cv;

        return new UpdateInfo
        {
            LatestVersion = string.IsNullOrEmpty(tag) ? latest : tag,
            Url = htmlUrl,
            Notes = notes,
            IsNewer = isNewer
        };
    }

    private static string NormalizeVersion(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        // Оставляем только числовую часть "1.2.3".
        var span = s.AsSpan();
        int end = 0;
        while (end < span.Length && (char.IsDigit(span[end]) || span[end] == '.')) end++;
        var num = s[..end].Trim('.');
        return string.IsNullOrEmpty(num) ? "0.0" : num;
    }
}
