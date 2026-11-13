using LocalAppManager.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace LocalAppManager.Services;

/// <summary>
/// The one way background code touches the UI: everything goes through the UI thread's DispatcherQueue.
/// </summary>
public static class Ui
{
    private static DispatcherQueue? _queue;

    public static DispatcherQueue Queue => _queue ?? throw new InvalidOperationException("Ui.Initialize not called");

    public static void Initialize(DispatcherQueue queue) => _queue = queue;

    public static bool HasAccess => _queue?.HasThreadAccess ?? false;

    /// <summary>Runs <paramref name="action"/> on the UI thread (inline if already there).</summary>
    public static void Run(Action action)
    {
        if (HasAccess)
        {
            Guard(action);
            return;
        }
        _queue?.TryEnqueue(() => Guard(action));
    }

    /// <summary>Always queues, even from the UI thread (lets the current layout pass finish first).</summary>
    public static void Post(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal) =>
        _queue?.TryEnqueue(priority, () => Guard(action));

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            AppLog.Error("UI callback failed", e);
        }
    }

    /// <summary>Fire-and-forget for async UI handlers, with errors logged instead of lost.</summary>
    public static async void Forget(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            AppLog.Error(what + " failed", e);
        }
    }
}
