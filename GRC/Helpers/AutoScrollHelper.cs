using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace GRC.Helpers;

public static class AutoScrollHelper
{
    public static readonly DependencyProperty AutoScrollProperty =
        DependencyProperty.RegisterAttached(
            "AutoScroll",
            typeof(bool),
            typeof(AutoScrollHelper),
            new PropertyMetadata(false, OnAutoScrollChanged));

    public static bool GetAutoScroll(DependencyObject obj) => (bool)obj.GetValue(AutoScrollProperty);

    public static void SetAutoScroll(DependencyObject obj, bool value) => obj.SetValue(AutoScrollProperty, value);

    private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer)
        {
            if ((bool)e.NewValue)
            {
                scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
                // 💡 마우스 휠 이벤트 추가
                scrollViewer.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
                scrollViewer.Dispatcher.InvokeAsync(() => scrollViewer.ScrollToEnd());
            }
            else
            {
                scrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;
                // 💡 마우스 휠 이벤트 해제
                scrollViewer.PreviewMouseWheel -= ScrollViewer_PreviewMouseWheel;
            }
        }
    }

    // 2. 💡 사용자의 순수 마우스 휠 굴림을 감지하는 전용 메서드 추가
    private static void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            // e.Delta가 양수(+)이면 마우스 휠을 위로 올렸다는 뜻입니다.
            if (e.Delta > 0)
            {
                // WPF의 보정이 아닌 순수 사용자의 물리적 조작이므로 오토스크롤 차단
                scrollViewer.Tag = false;
              //  System.Diagnostics.Debug.WriteLine("[Scroll-Switch] 🔴 자동 스크롤 OFF (물리적 마우스 휠 UP 감지)");
            }
        }
    }

    // 3. ScrollChanged 로직 다이어트 (오탐지 주범인 VerticalChange 로직 삭제)
    private static void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            // 사용자가 스크롤을 맨 아래로 직접 내리면 오토스크롤 다시 ON
            bool isAtBottom = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 10.0;
            if (isAtBottom)
            {
                scrollViewer.Tag = true;
            }

            // 👇 [핵심 수정 부분] 기존의 if (e.ExtentHeightChange > 0) 를 아래 코드로 교체합니다.
            // 높이가 늘어날 때뿐만 아니라, UI 스와프로 인해 일시적으로 수축(-)할 때도 대응합니다.
            if (Math.Abs(e.ExtentHeightChange) > 0)
            {
                bool isAutoScrollEnabled = scrollViewer.Tag == null || (bool)scrollViewer.Tag;

                if (isAutoScrollEnabled)
                {
                    // 레이아웃이 확정된 후(Loaded 우선순위) 바닥으로 강력하게 이동시킴
                    scrollViewer.Dispatcher.InvokeAsync(() =>
                    {
                        scrollViewer.ScrollToEnd();
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
        }
    }

}