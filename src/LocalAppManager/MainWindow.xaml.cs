using System.Runtime.InteropServices;
using LocalAppManager.Core.Diagnostics;
using LocalAppManager.Core.Storage;
using LocalAppManager.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace LocalAppManager;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly nint _hwnd;
    private bool _closingForReal;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Title = AppPaths.AppName;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 480;
        }

        RestorePlacement();
        AppWindow.Closing += OnClosing;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) _vm.Log.SetActive(true);
        };
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidVisibilityChange) _vm.Log.SetActive(AppWindow.IsVisible);
        };
        Page.Initialize(vm, this);
    }

    public nint Hwnd => _hwnd;

    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        SetForegroundWindow(_hwnd);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingForReal || App.Current.IsExiting) return;
        SavePlacement();
        if (_vm.Manager.Settings.CloseToTray)
        {
            args.Cancel = true;
            AppWindow.Hide();
            if (!_vm.Manager.Settings.TrayHintShown)
            {
                _vm.Manager.Settings.TrayHintShown = true;
                _vm.Manager.SaveSettings();
                App.Current.Notifications.ShowInfo("트레이에서 계속 실행 중", "프로젝트는 계속 돌아갑니다. 트레이 아이콘을 누르면 창이 다시 열리고, 완전히 끄려면 트레이 메뉴의 \"종료\"를 누르세요.");
            }
            return;
        }
        args.Cancel = true;
        App.Current.Quit();
    }

    public void CloseForReal()
    {
        _closingForReal = true;
        try { Close(); } catch { }
    }

    // ---- placement (Win32 so the restored size is kept even when maximized)

    public void SavePlacement()
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(_hwnd, ref wp)) return;
        var r = wp.rcNormalPosition;
        if (r.Right - r.Left < 200 || r.Bottom - r.Top < 200) return;
        _vm.Manager.Settings.Window = new WindowPlacement
        {
            X = r.Left,
            Y = r.Top,
            Width = r.Right - r.Left,
            Height = r.Bottom - r.Top,
            Maximized = wp.showCmd == SW_SHOWMAXIMIZED,
        };
        _vm.Manager.SaveSettings();
    }

    private void RestorePlacement()
    {
        var saved = _vm.Manager.Settings.Window;
        if (saved is null)
        {
            // First run: a comfortable size (in DIPs, scaled to the monitor), centered on the primary work area.
            var area = DisplayArea.Primary.WorkArea;
            double scale = GetDpiForWindow(_hwnd) / 96.0;
            int w = Math.Min((int)(1180 * scale), area.Width - 80), h = Math.Min((int)(780 * scale), area.Height - 80);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
            return;
        }
        // Only restore onto a monitor that still exists.
        var rect = new Windows.Graphics.RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
        var display = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
        if (display is null)
        {
            var area = DisplayArea.Primary.WorkArea;
            rect = new Windows.Graphics.RectInt32(area.X + 40, area.Y + 40, Math.Min(saved.Width, area.Width - 80), Math.Min(saved.Height, area.Height - 80));
        }
        var wp = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            showCmd = SW_HIDE,
            rcNormalPosition = new RECT { Left = rect.X, Top = rect.Y, Right = rect.X + rect.Width, Bottom = rect.Y + rect.Height },
        };
        SetWindowPlacement(_hwnd, ref wp);
        _maximizeOnShow = saved.Maximized;
        AppWindow.Changed += MaximizeOnFirstShow;
    }

    private bool _maximizeOnShow;

    private void MaximizeOnFirstShow(AppWindow sender, AppWindowChangedEventArgs e)
    {
        if (!e.DidVisibilityChange || !sender.IsVisible) return;
        sender.Changed -= MaximizeOnFirstShow;
        if (_maximizeOnShow && sender.Presenter is OverlappedPresenter p) p.Maximize();
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOWMAXIMIZED = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);
}
