namespace Deduplicator.Core.Services;

public interface IDriveWatcher
{
    /// <summary>Срабатывает при подключении/извлечении носителя (в фоновом потоке).</summary>
    event Action? DrivesChanged;
    void Start();
    void Stop();
}

/// <summary>
/// Следит за подключением/извлечением носителей опросом раз в ~2 секунды и оповещает,
/// когда набор готовых дисков меняется (реальное время для каталога).
/// </summary>
public sealed class DriveWatcher : IDriveWatcher, IDisposable
{
    private readonly System.Threading.Timer _timer;
    private string _signature = "";
    private bool _started;

    public event Action? DrivesChanged;

    public DriveWatcher()
    {
        _timer = new System.Threading.Timer(_ => Check(), null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _signature = Signature();
        _timer.Change(2000, 2000);
    }

    public void Stop() => _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

    private void Check()
    {
        var sig = Signature();
        if (sig == _signature) return;
        _signature = sig;
        try { DrivesChanged?.Invoke(); } catch { }
    }

    private static string Signature()
    {
        try
        {
            var parts = DriveInfo.GetDrives()
                .Where(d => { try { return d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable; } catch { return false; } })
                .Select(d => { try { return d.Name + "|" + d.TotalSize; } catch { return d.Name; } })
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
            return string.Join(";", parts);
        }
        catch
        {
            return "";
        }
    }

    public void Dispose() => _timer.Dispose();
}
