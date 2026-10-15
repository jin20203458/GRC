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

    [Fact]
    public void GetGatedClues_WhenSynonymInputWithHighSemanticSimilarity_ReturnsGatedClues()
    {
        // Arrange - '흑철검' 단서와 '검은 쇠검' 유저 입력 (공통 어휘 0건, 코사인 유사도 1.0 모의 벡터)
        var tracker = new ClueTracker();
        var clueEmb = new float[768];
        var queryEmb = new float[768];
        for (int i = 0; i < 768; i++)
        {
            clueEmb[i] = 1.0f / MathF.Sqrt(768);
            queryEmb[i] = 1.0f / MathF.Sqrt(768); // 동일 방향 (유사도 1.0)
        }

        tracker.AddOrUpdateClue("루시엘의 품에서 발견된 흑철검", 1, "c1", clueEmb);

        string synonymUserInput = "바닥에 뒹구는 검은 쇠검을 천천히 들어올린다.";

        // Act 1: 임베딩 없이 텍스트만 넣었을 때 - Fast-Path 1, 2 모두 차단되어 빈 리스트 반환 (위양성 방지 검증)
        var resultWithoutEmbedding = tracker.GetGatedClues(synonymUserInput, null);
        Assert.Empty(resultWithoutEmbedding);

        // Act 2: 유사도 0.80 이상 시맨틱 임베딩 주입 시 - 3단계 시맨틱 경로로 정상 인출 증명
        var resultWithEmbedding = tracker.GetGatedClues(synonymUserInput, queryEmb);

        // Assert - 순수 시맨틱 게이팅으로 단서가 정상 인출됨
        Assert.Single(resultWithEmbedding);
        Assert.Equal("c1", resultWithEmbedding[0].Id);
    }

    [Fact]
    public void GetGatedClues_WhenLowSimilarityEmbedding_BlocksRetrieval()
    {
        // Arrange - 무관한 벡터 (직교 벡터, 유사도 0.0)
        var tracker = new ClueTracker();
        var clueEmb = new float[768];
        var queryEmb = new float[768];
        clueEmb[0] = 1.0f;
        queryEmb[1] = 1.0f; // 직교 (유사도 0.0)

        tracker.AddOrUpdateClue("루시엘의 품에서 떨어진 흑철검", 1, "c1", clueEmb);

        // Act - 텍스트 매칭(품에서, 떨어진, 흑철검 등)도 없고 임베딩 유사도도 0.0인 무관한 입력
        var result = tracker.GetGatedClues("숲속 오솔길을 조용히 걸어가며 밤하늘을 올려다본다.", queryEmb);

        // Assert - 텍스트 불일치 및 임계치(0.80) 미달로 차단됨
        Assert.Empty(result);
    }

    [Fact]
    public void UpdateClueEmbedding_UpdatesEmbeddingSafely()
    {
        // Arrange
        var tracker = new ClueTracker();
        string clueId = tracker.AddOrUpdateClue("비밀 쪽지", 1);
        Assert.Null(tracker.ActiveClues[0].Embedding);

        var newEmb = new float[768];
        newEmb[0] = 1.0f;

        // Act
        tracker.UpdateClueEmbedding(clueId, newEmb);

        // Assert
        Assert.NotNull(tracker.ActiveClues[0].Embedding);
        Assert.Equal(768, tracker.ActiveClues[0].Embedding!.Length);
        Assert.Equal(1.0f, tracker.ActiveClues[0].Embedding![0]);
    }

    [Fact]
    public void ConcurrentAccess_IsThreadSafe()
    {
        // Arrange
        var tracker = new ClueTracker();
        for (int i = 0; i < 3; i++)
        {
            tracker.AddOrUpdateClue($"단서 {i}", i);
        }

        var dummyEmb = new float[768];

        // Act & Assert - 10개 병렬 태스크에서 동시에 Add, Update, Read 호출 시 InvalidOperationException 미발생
        System.Threading.Tasks.Parallel.For(0, 100, i =>
        {
            if (i % 3 == 0)
            {
                tracker.AddOrUpdateClue($"병렬 단서 {i}", i);
            }
            else if (i % 3 == 1)
            {
                var clues = tracker.ActiveClues;
                if (clues.Count > 0)
                {
                    tracker.UpdateClueEmbedding(clues[0].Id, dummyEmb);
                }
            }
            else
            {
                tracker.GetGatedClues("아무 텍스트", dummyEmb);
            }
        });

        Assert.True(tracker.ActiveClues.Count <= 3);
    }

    [Fact]
    public void GetGatedClues_WhenGeneralActionKeywordOnly_WithoutTargetMatch_BlocksRetrieval()
    {
        // Arrange - "외투 주머니 속 붉은 밀랍 쪽지" 단서가 활성화되어 있음
        var tracker = new ClueTracker();
        tracker.AddOrUpdateClue("외투 주머니 속 붉은 밀랍 쪽지", 1, "c1");

        // Act - 과거에는 "수색", "조사" 키워드로 단서가 누출되었으나, 100% 타겟 RAG에서는 대상 일치 없으므로 완전 차단
        var result1 = tracker.GetGatedClues("방 안을 조심스럽게 수색하고 구석구석 조사한다.");
        var result2 = tracker.GetGatedClues("주변에 숨겨진 비밀이나 수상한 단서가 있는지 살핀다.");

        // Assert - 스포일러 누출 0건 (빈 리스트 차단 증명)
        Assert.Empty(result1);
        Assert.Empty(result2);
    }

    [Fact]
    public void GetGatedClues_WhenMultipleCluesExist_OnlyTargetedClueIsRetrieved()
    {
        // Arrange - 검은 칼 단서와 붉은 구두 단서가 공존
        var tracker = new ClueTracker();
        tracker.AddOrUpdateClue("검은 칼의 자루에 치명적인 독침이 숨겨져 있다.", 1, "c_sword");
        tracker.AddOrUpdateClue("하녀의 붉은 구두 밑창에 진흙이 묻어있다.", 1, "c_shoes");

        // Act - 유저가 "검은 칼"만 지목하여 조사
        var result = tracker.GetGatedClues("탁자 위에 놓인 검은 칼을 집어 들고 자세히 살펴본다.");

        // Assert - 검은 칼 단서만 단독 인출되고, 붉은 구두는 절대 인출되지 않음 (어텐션 오염 0% 증명)
        Assert.Single(result);
        Assert.Equal("c_sword", result[0].Id);
    }

    [Fact]
    public void GetGatedClues_WhenArchivedClueTargeted_RecallsDormantClue()
    {
        // Arrange - 단서가 슬롯 상한(Max 3)으로 밀려나 L2 Dormant 아카이브로 이관된 상태
        var tracker = new ClueTracker();
        tracker.AddOrUpdateClue("고대 사원의 황금 열쇠", 1, "c_key"); // 1번 단서
        tracker.AddOrUpdateClue("기사단장의 비밀 서신", 2, "c_letter");
        tracker.AddOrUpdateClue("마녀의 은색 방울", 3, "c_bell");
        tracker.AddOrUpdateClue("용병의 부러진 검", 4, "c_sword"); // 4번 단서가 추가되면서 c_key가 Dormant로 이관

        Assert.Equal(3, tracker.ActiveClues.Count);
        Assert.DoesNotContain(tracker.ActiveClues, c => c.Id == "c_key");
        Assert.Contains(tracker.ArchivedClues, c => c.Id == "c_key");

        // Act - 유저가 50턴 뒤에 과거 잊힌 "황금 열쇠"를 다시 언급
        var result = tracker.GetGatedClues("인벤토리에서 먼지 쌓인 황금 열쇠를 꺼내 만져본다.");

        // Assert - L2 아카이브에 잠복 중이던 c_key 단서가 핀포인트로 정상 회수됨
        Assert.Single(result);
        Assert.Equal("c_key", result[0].Id);
    }

    [Fact]
    public void GetGatedClues_StickyRetention_PersistsForThreeTurnsTotal()
    {
        // Arrange
        var tracker = new ClueTracker();
        tracker.AddOrUpdateClue("검은 칼의 자루에 치명적인 독침이 숨겨져 있다.", 1, "c_sword");

        // Turn 1 (트리거 턴): 유저가 "검은 칼" 직접 지목 -> 1순위 직접 매칭 (RemainingStickyTurns = 2 충전)
        var turn1 = tracker.GetGatedClues("탁자 위에 놓인 검은 칼을 집어 들고 살펴본다.");
        Assert.Single(turn1);
        Assert.Equal("c_sword", turn1[0].Id);

        // Turn 2 (다음 턴): 유저가 대명사("그것")를 써서 키워드 미매칭 -> Sticky 잔존으로 인출됨 (1턴 차감)
        var turn2 = tracker.GetGatedClues("칼집에서 그것을 뽑아 칼날을 비춰본다.");
        Assert.Single(turn2);
        Assert.Equal("c_sword", turn2[0].Id);

        // Turn 3 (다다음 턴): 유저가 키워드 없는 다른 행동 -> Sticky 잔존으로 인출됨 (2턴 차감)
        var turn3 = tracker.GetGatedClues("손잡이를 단단히 쥐고 자세를 고쳐잡는다.");
        Assert.Single(turn3);
        Assert.Equal("c_sword", turn3[0].Id);

        // Turn 4 (4턴째): 추가 언급 없이 3턴 경과 만료 -> 프롬프트에서 자동 제외(소멸)
        var turn4 = tracker.GetGatedClues("여관 문을 열고 밖으로 나간다.");
        Assert.Empty(turn4);
    }

    [Fact]
    public void ResolveClue_DuringSticky_ImmediatelyRemovesFromRetrieval()
    {
        // Arrange
        var tracker = new ClueTracker();
        tracker.AddOrUpdateClue("검은 칼의 자루에 치명적인 독침이 숨겨져 있다.", 1, "c_sword");

        // Turn 1: 직접 매칭되어 잔존 턴이 부여됨
        var turn1 = tracker.GetGatedClues("검은 칼을 살펴본다.");
        Assert.Single(turn1);

        // Act - 사건 해결(독침 해제 및 진실 규명)로 ResolveClue 호출
        tracker.ResolveClue("c_sword");

        // Assert - 잔존 턴이 남아있었더라도 해결 즉시 영구 삭제되어 다음 턴에 0건 인출
        var turn2 = tracker.GetGatedClues("그것을 다시 확인한다.");
        Assert.Empty(turn2);
    }
}
