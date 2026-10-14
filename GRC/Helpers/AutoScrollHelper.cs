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

    public static readonly DependencyProperty IsAutoScrollActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsAutoScrollActive",
            typeof(bool),
            typeof(AutoScrollHelper),
            new PropertyMetadata(true));

    public static bool GetIsAutoScrollActive(DependencyObject obj) => (bool)obj.GetValue(IsAutoScrollActiveProperty);
    public static void SetIsAutoScrollActive(DependencyObject obj, bool value) => obj.SetValue(IsAutoScrollActiveProperty, value);

    public const double BottomTolerancePixels = 24.0;

    /// <summary>
    /// 스마트 오토스크롤 판정 순수 함수 (Modern_AI_Desktop_UI_Guidelines §6 규격)
    /// </summary>
    public static bool CalculateIsAtBottom(double verticalOffset, double scrollableHeight, double extentHeightChange, double tolerance = BottomTolerancePixels)
    {
        if (scrollableHeight <= 0) return true;

        // 컨텐츠가 늘어났을 때만 변경 전 높이(scrollableHeight - extentHeightChange)를 소급 기준으로 계산.
        // 컨텐츠가 줄어들었거나 불변일 때는 현재 scrollableHeight 기준으로 허용오차만 판정하여 상단 유저 하이재킹 차단.
        double referenceHeight = extentHeightChange > 0
            ? scrollableHeight - extentHeightChange
            : scrollableHeight;

        return verticalOffset >= (referenceHeight - tolerance);
    }

    private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
        {
            sv.ScrollChanged -= ScrollViewer_ScrollChanged;
            sv.PreviewMouseWheel -= ScrollViewer_PreviewMouseWheel;

            if ((bool)e.NewValue)
            {
                SetIsAutoScrollActive(sv, true);
                sv.ScrollChanged += ScrollViewer_ScrollChanged;
                sv.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
                sv.Dispatcher.InvokeAsync(() =>
                {
                    if (GetIsAutoScrollActive(sv))
                    {
                        sv.ScrollToEnd();
                    }
                }, DispatcherPriority.Loaded);
            }
            else
            {
                SetIsAutoScrollActive(sv, false);
            }
        }
    }

    private static void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        if (e.Delta > 0)
        {
            // 사용자가 마우스 휠을 위로 굴리는 순간 즉시 오토스크롤 비활성화
            SetIsAutoScrollActive(sv, false);
        }
        else if (e.Delta < 0)
        {
            // 최하단 부근에서 휠을 아래로 굴릴 때 오토스크롤 재활성화 보장
            if (sv.ScrollableHeight <= 0 || sv.VerticalOffset >= (sv.ScrollableHeight - BottomTolerancePixels))
            {
                SetIsAutoScrollActive(sv, true);
            }
        }
    }

    private static void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        bool isPureUserScroll = e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0;

        // 1. 순수 사용자 조작으로 뷰포트가 위로 이동한 경우 (드래그, PageUp 등):
        // 즉시 비활성화하고 리턴 (토큰 유입/리사이징에 의한 e.VerticalChange < 0은 제외)
        if (isPureUserScroll && e.VerticalChange < 0)
        {
            SetIsAutoScrollActive(sv, false);
            return;
        }

        // 2. 사용자가 능동적으로 아래로 스크롤하여 바닥 부근(24px 이내)에 도달한 경우,
        // 또는 컨텐츠가 창에 다 들어와서 스크롤바가 없는 경우에만 활성화:
        bool isAtBottomNow = sv.ScrollableHeight <= 0 || sv.VerticalOffset >= (sv.ScrollableHeight - BottomTolerancePixels);
        if (isAtBottomNow && (e.VerticalChange > 0 || sv.ScrollableHeight <= 0))
        {
            SetIsAutoScrollActive(sv, true);
        }

        // 3. 컨텐츠 크기가 변경된 경우 (스트리밍 텍스트 추가 등):
        if (Math.Abs(e.ExtentHeightChange) > 0)
        {
            // 활성 상태일 때만 바닥 판정 및 스크롤 실행
            if (GetIsAutoScrollActive(sv))
            {
                bool isAtBottom = CalculateIsAtBottom(sv.VerticalOffset, sv.ScrollableHeight, e.ExtentHeightChange);
                if (isAtBottom)
                {
                    sv.Dispatcher.InvokeAsync(() =>
                    {
                        // 큐잉 지연 중 휠 상향 조작 발생 여부 지연 재검증 (Lazy Guard)
                        if (GetIsAutoScrollActive(sv))
                        {
                            sv.ScrollToEnd();
                        }
                    }, DispatcherPriority.Loaded);
                }
            }
        }
    }

    /// <summary>
    /// 외부(유저 전송 등)에서 스크롤을 최하단으로 즉시 강제 이동할 때 사용하는 안전한 헬퍼
    /// </summary>
    public static void ScrollToEnd(ScrollViewer sv)
    {
        if (sv == null) return;
        SetIsAutoScrollActive(sv, true);
        sv.Dispatcher.InvokeAsync(() =>
        {
            if (GetIsAutoScrollActive(sv))
            {
                sv.ScrollToEnd();
            }
        }, DispatcherPriority.Loaded);
    }
}