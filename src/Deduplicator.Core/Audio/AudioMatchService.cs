using System.Diagnostics;

namespace Deduplicator.Core.Audio;

public interface IAudioMatchService
{
    /// <summary>Доступен ли fpcalc.exe (иначе аудио-сверка пропускается).</summary>
    bool IsAvailable { get; }

    /// <summary>Путь к используемому fpcalc (для диагностики/настроек).</summary>
    string? FpcalcPath { get; }

    /// <summary>Переопределить путь к fpcalc «на лету» (после изменения в настройках), без перезапуска.</summary>
    void Reconfigure(string? configuredPath);

    /// <summary>Аудио-отпечаток Chromaprint в упакованном виде. null — если не удалось.</summary>
    byte[]? ComputeFingerprint(string fullPath, CancellationToken ct = default);
}

/// <summary>Интеграция с Chromaprint через внешний fpcalc.exe (AcoustID).</summary>
public sealed class AudioMatchService : IAudioMatchService
{
    private string? _fpcalcPath;

    public AudioMatchService(string? configuredPath = null)
    {
        _fpcalcPath = ResolveFpcalc(configuredPath);
    }

    public bool IsAvailable => _fpcalcPath is not null && File.Exists(_fpcalcPath);
    public string? FpcalcPath => _fpcalcPath;

    public void Reconfigure(string? configuredPath) => _fpcalcPath = ResolveFpcalc(configuredPath);

    public byte[]? ComputeFingerprint(string fullPath, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _fpcalcPath!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-raw");
            psi.ArgumentList.Add("-length");
            psi.ArgumentList.Add("120");
            psi.ArgumentList.Add(fullPath);

            using var p = Process.Start(psi);
            if (p is null) return null;

            // Читаем stdout и stderr параллельно, чтобы не было взаимоблокировки при
            // переполнении буфера одного из потоков.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            // Ждём с учётом отмены.
            while (!p.WaitForExit(200))
            {
                if (ct.IsCancellationRequested)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    ct.ThrowIfCancellationRequested();
                }
            }

            string output = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();

            if (p.ExitCode != 0) return null;
            return ParseFingerprint(output);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static byte[]? ParseFingerprint(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("FINGERPRINT=", StringComparison.Ordinal)) continue;
            var data = trimmed.Substring("FINGERPRINT=".Length);
            var parts = data.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var ints = new int[parts.Length];
            int n = 0;
            foreach (var part in parts)
                if (int.TryParse(part, out var v)) ints[n++] = v;
            if (n == 0) return null;
            return AudioFingerprint.Pack(ints, n);
        }
        return null;
    }

    private static string? ResolveFpcalc(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "fpcalc.exe"),
            Path.Combine(baseDir, "tools", "fpcalc.exe"),
            Path.Combine(baseDir, "tools", "chromaprint", "fpcalc.exe"),
            Path.Combine(baseDir, "fpcalc"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // Попробовать PATH.
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), "fpcalc.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        return null;
    }
}

/// <summary>Упаковка/распаковка аудио-отпечатка (массива int32) и построение множества субсигнатур.</summary>
public static class AudioFingerprint
{
    public static byte[] Pack(int[] ints, int count)
    {
        var bytes = new byte[count * 4];
        Buffer.BlockCopy(ints, 0, bytes, 0, count * 4);
        return bytes;
    }

    public static int[] Unpack(byte[] packed)
    {
        var ints = new int[packed.Length / 4];
        Buffer.BlockCopy(packed, 0, ints, 0, ints.Length * 4);
        return ints;
    }

    /// <summary>Множество субсигнатур (uint) для оценки схожести по Жаккару.</summary>
    public static HashSet<uint> ToSet(byte[] packed)
    {
        var ints = Unpack(packed);
        var set = new HashSet<uint>(ints.Length);
        foreach (var i in ints) set.Add((uint)i);
        return set;
    }
}
