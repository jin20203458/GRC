using System;
using System.Threading;
using System.Windows.Controls;
using GRC.Helpers;
using Xunit;

namespace GRC.Tests;

public class SmartAutoScrollTests
{
    [Fact]
    public void CalculateIsAtBottom_WhenContentFitsScreen_ReturnsTrue()
    {
        // 스크롤바가 생기지 않는 상태 (scrollableHeight <= 0)
        bool isAtBottom = AutoScrollHelper.CalculateIsAtBottom(0, 0, 0);

        Assert.True(isAtBottom);
    }

    [Fact]
    public void CalculateIsAtBottom_WhenAtAbsoluteBottom_ReturnsTrue()
    {
        // 컨텐츠가 50px 증가하여 현재 스크롤 가능 높이가 1000이고 사용자가 정확히 1000에 위치한 경우
        bool isAtBottom = AutoScrollHelper.CalculateIsAtBottom(1000, 1000, 50);

        Assert.True(isAtBottom);
    }

    [Fact]
    public void CalculateIsAtBottom_WhenWithinTolerance_ReturnsTrue()
    {
        // scrollableHeight = 1000, extentHeightChange = 50 -> 이전 바닥 = 950
        // tolerance = 24 -> 임계값 = 926. 사용자가 930에 위치(허용오차 이내)
        bool isAtBottom = AutoScrollHelper.CalculateIsAtBottom(930, 1000, 50, tolerance: 24.0);

        Assert.True(isAtBottom);
    }

    [Fact]
    public void CalculateIsAtBottom_WhenScrolledUpBeyondTolerance_ReturnsFalse()
    {
        // 사용자가 이전 대사를 읽기 위해 850 위치로 스크롤을 올려둔 상태
        // 임계값 926 미만이므로 새 대사가 출력되어도 스크롤 하이재킹 차단 (false)
        bool isAtBottom = AutoScrollHelper.CalculateIsAtBottom(850, 1000, 50, tolerance: 24.0);

        Assert.False(isAtBottom);
    }

    [Fact]
    public void CalculateIsAtBottom_WhenHeightShrinksAndUserNotAtBottom_ReturnsFalse()
    {
        // 컨텐츠 수축 (UI 스와프 등, scrollableHeight = 500, extentHeightChange = -200)
        // referenceHeight = 500, 임계값 = 476
        // 사용자가 위쪽 대사(300)를 읽고 있었다면 하이재킹 없이 false 유지되어야 함
        bool isAtBottom = AutoScrollHelper.CalculateIsAtBottom(300, 500, -200, tolerance: 24.0);

        Assert.False(isAtBottom);
    }

    [Fact]
    public void CalculateIsAtBottom_WhenHeightShrinksAndUserAtBottom_ReturnsTrue()
    {
        // 컨텐츠 수축 발생 후에도 사용자가 수축된 바닥 근처(490)에 위치한 경우
        bool isAtBottom = AutoScrollHelper.CalculateIsAtBottom(490, 500, -200, tolerance: 24.0);

        Assert.True(isAtBottom);
    }

    [Fact]
    public void AutoScrollHelper_StateTransitions_ExecuteOnStaThread()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var sv = new ScrollViewer();
                // 기본값 검증 (초기 상태 true)
                Assert.True(AutoScrollHelper.GetIsAutoScrollActive(sv));

                // 상태 변경 검증 (휠 업/상향 스크롤 등으로 비활성화)
                AutoScrollHelper.SetIsAutoScrollActive(sv, false);
                Assert.False(AutoScrollHelper.GetIsAutoScrollActive(sv));

                // ScrollToEnd 호출 시 활성화 상태 복귀 검증
                AutoScrollHelper.ScrollToEnd(sv);
                Assert.True(AutoScrollHelper.GetIsAutoScrollActive(sv));
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool finished = thread.Join(5000);

        Assert.True(finished, "STA test thread timed out");
        Assert.Null(threadEx);
    }
}
