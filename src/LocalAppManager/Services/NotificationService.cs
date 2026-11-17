using LocalAppManager.Core.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace LocalAppManager.Services;

/// <summary>Toast notifications through the Windows App SDK (works unpackaged).</summary>
public sealed class NotificationService
{
    private bool _registered;

    /// <summary>Raised on a background thread when a toast (or one of its buttons) is clicked. Args: action, projectId.</summary>
    public event Action<string, string?>? Invoked;

    public void Register()
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += (_, e) => HandleArguments(e.Arguments);
            manager.Register();
            _registered = true;
        }
        catch (Exception e)
        {
            // Notifications are a nicety; the app works without them.
            AppLog.Warn("toast registration failed", e);
        }
    }

    /// <summary>Handles a launch that came from clicking a toast while the app wasn't running.</summary>
    public void HandleArguments(IDictionary<string, string> args)
    {
        args.TryGetValue("action", out var action);
        args.TryGetValue("project", out var project);
        Invoked?.Invoke(action ?? "show", project);
    }

    public void ShowCrash(string projectId, string projectName, string detail, bool canRestart)
    {
        if (!_registered) return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "show")
                .AddArgument("project", projectId)
                .AddText($"{projectName} 비정상 종료")
                .AddText(detail);
            if (canRestart)
                builder.AddButton(new AppNotificationButton("다시 시작").AddArgument("action", "restart").AddArgument("project", projectId));
            builder.AddButton(new AppNotificationButton("로그 보기").AddArgument("action", "show").AddArgument("project", projectId));
            var notification = builder.BuildNotification();
            notification.Tag = projectId;
            notification.Group = "crash";
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e)
        {
            AppLog.Warn("failed to show toast", e);
        }
    }

    public void ShowUpdateReady(string version)
    {
        if (!_registered) return;
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("action", "show")
                .AddText($"새 버전 {version} 준비됨")
                .AddText("앱을 종료하면 설치됩니다. 지금 재시작하면 실행 중인 프로젝트가 모두 정지됩니다.")
                .AddButton(new AppNotificationButton("지금 재시작").AddArgument("action", "update"))
                .BuildNotification();
            notification.Tag = "update";
            notification.Group = "update";
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e)
        {
            AppLog.Warn("failed to show toast", e);
        }
    }

    public void ShowInfo(string title, string text)
    {
        if (!_registered) return;
        try
        {
            AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText(title).AddText(text).BuildNotification());
        }
        catch (Exception e)
        {
            AppLog.Warn("failed to show toast", e);
        }
    }
}
