using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GRC.Helpers;

public static class WindowTitleBarBehavior
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_CAPTION_COLOR = 35;

    // 비활성 상태(클릭 해제 시)에서도 다크 테마를 유지하는 속성
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static readonly DependencyProperty EnableDarkTitleBarProperty =
        DependencyProperty.RegisterAttached(
            "EnableDarkTitleBar",
            typeof(bool),
            typeof(WindowTitleBarBehavior),
            new PropertyMetadata(false, OnEnableDarkTitleBarChanged));

    public static bool GetEnableDarkTitleBar(DependencyObject obj) => (bool)obj.GetValue(EnableDarkTitleBarProperty);
    public static void SetEnableDarkTitleBar(DependencyObject obj, bool value) => obj.SetValue(EnableDarkTitleBarProperty, value);

    private static void OnEnableDarkTitleBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Window window && (bool)e.NewValue)
        {
            window.SourceInitialized += (s, ev) => ApplyDarkThemeTitleBar(window);
        }
    }

    public static void ApplyDarkThemeTitleBar(Window window)
    {
        try
        {
            var helper = new WindowInteropHelper(window);
            var hwnd = helper.Handle;

            if (hwnd != IntPtr.Zero)
            {
                // 1. 창 전체를 다크 모드로 강제 설정 (비활성 시 흰색으로 풀리는 현상 방지)
                int useImmersiveDarkMode = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int));

                // 2. 상단 헤더 배경색(#202123)과 DWM 캡션 색상 동기화
                int darkColorValue = 0x00232120;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref darkColorValue, sizeof(int));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"타이틀바 색상 변경 실패 (Windows 10 이하일 수 있음): {ex.Message}");
        }
    }
}