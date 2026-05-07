using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using TodoFloat.Data;

namespace TodoFloat.Services;

public class ReminderService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly TaskRepository _repo;
    private readonly HashSet<long> _firedThisSession = new();

    public ReminderService(TaskRepository repo)
    {
        _repo = repo;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Check();
    }

    public void Start()
    {
        _timer.Start();
        Check();
    }

    private void Check()
    {
        var now = DateTime.UtcNow;
        foreach (var t in _repo.GetAll(includeCompleted: false))
        {
            if (t.RemindAt is null) continue;
            if (_firedThisSession.Contains(t.Id)) continue;
            if (t.RemindAt > now) continue;

            try
            {
                new ToastContentBuilder()
                    .AddText("⏰ " + t.Title)
                    .AddText(t.Notes ?? (t.DueAt is { } d ? $"截止：{d.ToLocalTime():g}" : ""))
                    .Show();
            }
            catch { /* ignore notification platform errors */ }

            _firedThisSession.Add(t.Id);
            t.RemindAt = null;
            _repo.Update(t);
        }
    }

    public void Dispose() => _timer.Stop();
}
