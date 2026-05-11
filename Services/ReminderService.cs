using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using TodoFloat.Application;

namespace TodoFloat.Services;

public class ReminderService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly ITodoApi _todoApi;
    private readonly HashSet<long> _firedThisSession = new();

    public ReminderService(ITodoApi todoApi)
    {
        _todoApi = todoApi;
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
        foreach (var t in _todoApi.ListTasks(new TodoTaskQuery(IncludeCompleted: false)))
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
            _todoApi.UpdateTask(new UpdateTaskRequest(
                t.Id,
                t.Title,
                t.Notes,
                t.ParentId,
                t.CategoryId,
                t.Priority,
                t.DueAt,
                null,
                t.Completed,
                t.CompletedAt,
                t.SortOrder));
        }
    }

    public void Dispose() => _timer.Stop();
}
