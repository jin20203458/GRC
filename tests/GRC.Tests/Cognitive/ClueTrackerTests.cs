using GRC.Models;
using GRC.Services;
using System.Linq;
using Xunit;

namespace GRC.Tests;

public class ClueTrackerTests
{
    [Fact]
    public void AddOrUpdateClue_WhenExceedsMaxSlots_EvictsOldestToArchiveWithDormantStatus()
    {
        // Arrange
        var tracker = new ClueTracker();

        // Act - 4개 단서 순차 등록
        tracker.AddOrUpdateClue("단서 1: 낡은 열쇠", 1, "c1");
        tracker.AddOrUpdateClue("단서 2: 비밀 지도", 2, "c2");
        tracker.AddOrUpdateClue("단서 3: 붉은 쪽지", 3, "c3");
        tracker.AddOrUpdateClue("단서 4: 부러진 반지", 4, "c4");

        // Assert - 활성 슬롯은 3개(c2, c3, c4), 아카이브에 c1(Dormant) 이관
        Assert.Equal(3, tracker.ActiveClues.Count);
        Assert.Equal(["c2", "c3", "c4"], tracker.ActiveClues.Select(c => c.Id));
        Assert.Single(tracker.ArchivedClues);
        Assert.Equal("c1", tracker.ArchivedClues[0].Id);
        Assert.Equal(ClueStatus.Dormant, tracker.ArchivedClues[0].Status);
    }

    [Fact]
    public void TickTurn_When20TurnsPassedWithoutReference_EvictsToArchive()
    {
        // Arrange
        var tracker = new ClueTracker();
        tracker.AddOrUpdateClue("단서 1: 붉은 쪽지", 5, "c1");
        tracker.AddOrUpdateClue("단서 2: 신비한 보석", 10, "c2");

        // Act - 25턴으로 진행 (c1은 20턴 경과, c2는 15턴 경과)
        tracker.TickTurn(25);

        // Assert - c1은 Dormant 아카이브 이관, c2는 여전히 Active 유지
        Assert.Single(tracker.ActiveClues);
        Assert.Equal("c2", tracker.ActiveClues[0].Id);
        Assert.Single(tracker.ArchivedClues);
        Assert.Equal("c1", tracker.ArchivedClues[0].Id);
        Assert.Equal(ClueStatus.Dormant, tracker.ArchivedClues[0].Status);
    }

    [Fact]
    public void ResolveClue_RemovesClueFromBothActiveAndArchived()
    {
        // Arrange
        var tracker = new ClueTracker();
        tracker.AddOrUpdateClue("단서 1", 1, "c1");
        tracker.AddOrUpdateClue("단서 2", 2, "c2");
        tracker.AddOrUpdateClue("단서 3", 3, "c3");
        tracker.AddOrUpdateClue("단서 4", 4, "c4"); // c1이 아카이브로 이관됨

        // Act - c2(Active) 및 c1(Archived) 해결 처리
        tracker.ResolveClue("c2");
        tracker.ResolveClue("c1");

        // Assert
        Assert.DoesNotContain(tracker.ActiveClues, c => c.Id == "c2");
        Assert.DoesNotContain(tracker.ArchivedClues, c => c.Id == "c1");
    }

    [Fact]
    public void GetGatedClues_WhenDailyChat_ReturnsEmptyList()
    {
        // Arrange
        var tracker = new ClueTracker();
        tracker.AddOrUpdateClue("외투 주머니 속 붉은 밀랍 쪽지", 1, "c1");

        // Act - 무관한 일상 대화
        var result = tracker.GetGatedClues("오늘 날씨가 참 맑고 화창하네. 산책이나 갈까?");

        // Assert - 조기 누설 차단(빈 리스트)
        Assert.Empty(result);
    }

    [Fact]
    public void GetGatedClues_WhenInterrogationOrTokenMatched_ReturnsGatedClues()
    {
        // Arrange
        var tracker = new ClueTracker();
        tracker.AddOrUpdateClue("외투 주머니 속 붉은 밀랍 쪽지", 1, "c1");

        // Act 1 - 일반 추궁 키워드("주머니") 포함
        var result1 = tracker.GetGatedClues("레이나, 네 외투 주머니에 들어있는 거 뭐야?");
        // Act 2 - 단서 고유 키워드("밀랍") 포함
        var result2 = tracker.GetGatedClues("붉은 밀랍이 떨어진 것 같은데?");

        // Assert
        Assert.Single(result1);
        Assert.Equal("c1", result1[0].Id);
        Assert.Single(result2);
        Assert.Equal("c1", result2[0].Id);
    }
}
