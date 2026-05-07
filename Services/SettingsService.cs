using System.Globalization;
using TodoFloat.Data;

namespace TodoFloat.Services;

public class SettingsService
{
    private readonly SettingsRepository _repo = new();

    public double WindowLeft
    {
        get => GetDouble("window.left", double.NaN);
        set => SetDouble("window.left", value);
    }

    public double WindowTop
    {
        get => GetDouble("window.top", double.NaN);
        set => SetDouble("window.top", value);
    }

    public double WindowWidth
    {
        get => GetDouble("window.width", 360);
        set => SetDouble("window.width", value);
    }

    public double WindowHeight
    {
        get => GetDouble("window.height", 620);
        set => SetDouble("window.height", value);
    }

    public bool AutoHide
    {
        get => _repo.Get("autohide") == "1";
        set => _repo.Set("autohide", value ? "1" : "0");
    }

    public string SnapEdge
    {
        get => _repo.Get("snap.edge") ?? "right";
        set => _repo.Set("snap.edge", value);
    }

    public bool ShowCompleted
    {
        get => _repo.Get("show.completed") == "1";
        set => _repo.Set("show.completed", value ? "1" : "0");
    }

    private double GetDouble(string key, double defaultValue)
    {
        var v = _repo.Get(key);
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : defaultValue;
    }

    private void SetDouble(string key, double value)
    {
        _repo.Set(key, value.ToString("R", CultureInfo.InvariantCulture));
    }
}
