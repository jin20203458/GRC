using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GRC.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace GRC.Views;

public partial class MainWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int HTMAXBUTTON = 9;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public enum DWM_SYSTEMBACKDROP_TYPE
    {
        DWMSBT_AUTO = 0,
        DWMSBT_NONE = 1,
        DWMSBT_MAINWINDOW = 2,      // Mica
        DWMSBT_TRANSIENTWINDOW = 3,  // Acrylic
        DWMSBT_TABBEDWINDOW = 4      // Mica Alt
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly MainViewModel _viewModel;
    private bool _isMaxButtonClicked;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = App.Current.Services.GetRequiredService<MainViewModel>();
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 1. DWM 다크 모드 및 둥근 모서리 설정
        int darkMode = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        int cornerPreference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

        // 2. Windows 11 22H2+ Mica/Backdrop 적용 (Roslyn CA1416 호환)
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            int backdropType = (int)DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

            var source = (HwndSource?)PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
        }

        // 3. Win32 메시지 훅 등록 (Snap Layouts 연동)
        var hwndSource = HwndSource.FromHwnd(hwnd);
        hwndSource?.AddHook(WndProc);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeIcon == null) return;

        if (WindowState == WindowState.Maximized)
        {
            // 복원 상태: 겹친 두 사각형
            MaximizeIcon.Data = Geometry.Parse("M3 1h6v6H9 M1 3h6v6H1z");
            MaximizeButton.ToolTip = "이전 크기로 복원";
        }
        else
        {
            // 최대화 상태: 단일 사각형
            MaximizeIcon.Data = Geometry.Parse("M1 1h8v8H1V1z");
            MaximizeButton.ToolTip = "최대화";
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (MaximizeButton != null && MaximizeButton.IsLoaded)
                {
                    int x = unchecked((short)(lParam.ToInt32() & 0xFFFF));
                    int y = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));
                    Point localPoint = MaximizeButton.PointFromScreen(new Point(x, y));

                    if (new Rect(default, MaximizeButton.RenderSize).Contains(localPoint))
                    {
                        handled = true;
                        return new IntPtr(HTMAXBUTTON); // Windows 11 Snap Layouts 플라이아웃 활성화
                    }
                }
                break;

            case WM_NCLBUTTONDOWN:
                if (wParam.ToInt32() == HTMAXBUTTON)
                {
                    _isMaxButtonClicked = true;
                    handled = true;
                    return IntPtr.Zero;
                }
                break;

            case WM_NCLBUTTONUP:
                if (wParam.ToInt32() == HTMAXBUTTON && _isMaxButtonClicked)
                {
                    _isMaxButtonClicked = false;
                    ToggleMaximize();
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
        }

        return IntPtr.Zero;
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void TitleBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        Point screenPoint = PointToScreen(e.GetPosition(this));
        SystemCommands.ShowSystemMenu(this, screenPoint);
    }
}