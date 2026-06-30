using Avalonia.Media;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>Карточка состояния одного диска.</summary>
public sealed class DiskHealthItemViewModel
{
    public DiskHealthInfo Model { get; }
    public DiskHealthItemViewModel(DiskHealthInfo model) => Model = model;

    public string Name => Model.Name;
    public string SerialText => Model.Serial ?? "—";
    public string MediaType => Model.MediaType;
    public string BusType => Model.BusType;
    public string SizeText => Format.Bytes(Model.SizeBytes);
    public bool PredictFailure => Model.PredictFailure;

    public string HealthText => Model.Health switch
    {
        DiskHealth.Healthy => Strings.HealthHealthy,
        DiskHealth.Warning => Strings.HealthWarning,
        DiskHealth.Unhealthy => Strings.HealthUnhealthy,
        _ => Strings.HealthUnknown
    };

    public IBrush HealthBrush => new SolidColorBrush(Color.Parse(Model.Health switch
    {
        DiskHealth.Healthy => "#34C759",
        DiskHealth.Warning => "#FF9500",
        DiskHealth.Unhealthy => "#FF3B30",
        _ => "#8E8E93"
    }));

    public bool HasTemp => Model.TemperatureC is > 0;
    public string TempText => $"{Model.TemperatureC} °C";

    public bool HasPowerOn => Model.PowerOnHours is > 0;
    public string PowerOnText => $"{Model.PowerOnHours:N0} {Strings.HealthHours}";

    public bool HasWear => Model.WearPercent is int w && w > 0;
    public string WearText => $"{Model.WearPercent}%";

    public bool HasReadErrors => Model.ReadErrors is not null;
    public string ReadErrorsText => $"{Model.ReadErrors:N0}";

    public bool HasRpm => Model.SpindleRpm is > 0;
    public string RpmText => $"{Model.SpindleRpm:N0}";
}
