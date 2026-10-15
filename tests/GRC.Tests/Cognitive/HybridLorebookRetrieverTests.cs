using GRC.Models;
using GRC.Services;
using System.Collections.Generic;
using Xunit;

namespace GRC.Tests;

public class HybridLorebookRetrieverTests
{
    private static float[] CreateDummyVector(float fillVal)
    {
        var v = new float[768];
        for (int i = 0; i < 768; i++) v[i] = fillVal;
        return v;
    }

    [Fact]
    public void Retrieve_WhenSparseKeywordMatchesWithKoreanParticles_ReturnsEntry()
    {
        // Arrange
        var retriever = new HybridLorebookRetriever();
        var lorebooks = new List<LorebookEntry>
        {
            new("흑철검", ["흑철검"], "고대 드워프가 제련한 검", "아이템", Priority: 10),
            new("안개 숲", ["안개 숲", "칼리고"], "위험한 마수 서식지", "장소", Priority: 5)
        };

        // Act - 한국어 조사("흑철검을") 결합 입력
        var result = retriever.Retrieve("그는 흑철검을 뽑아 들었다.", null, lorebooks);

        // Assert
        Assert.Single(result);
        Assert.Equal("흑철검", result[0].Name);
    }

    [Fact]
    public void Retrieve_WhenSparseFails_RecoversEntryViaDenseEmbeddingAboveThreshold()
    {
        // Arrange
        var retriever = new HybridLorebookRetriever();

        // 768차원 동일 방향 벡터(유사도 1.0)
        var queryEmbedding = CreateDummyVector(0.036f);
        var entryEmbedding = CreateDummyVector(0.036f);

        var lorebooks = new List<LorebookEntry>
        {
            new("흑철검", ["흑철검"], "고대 드워프가 제련한 검", "아이템", Priority: 10, Embedding: entryEmbedding)
        };

        // Act - 유저는 "검은색 철검"이라고 입력하여 키워드 매칭 실패하지만, Dense 유사도 1.0 >= 0.85 만족
        var result = retriever.Retrieve("그는 검은색 철검을 바라보았다.", queryEmbedding, lorebooks);

        // Assert
        Assert.Single(result);
        Assert.Equal("흑철검", result[0].Name);
    }

    [Fact]
    public void Retrieve_WhenEmbeddingsAreNull_GracefullyFallsBackToSparseOnly()
    {
        // Arrange
        var retriever = new HybridLorebookRetriever();
        var lorebooks = new List<LorebookEntry>
        {
            new("흑철검", ["흑철검"], "고대 드워프 검", "아이템", Priority: 10, Embedding: null)
        };

        // Act - queryEmbedding = null
        var result1 = retriever.Retrieve("흑철검 이야기", null, lorebooks);
        Assert.Single(result1);

        // 독립된 인스턴스에서 무관한 입력 시 빈 리스트 반환 확인
        var freshRetriever = new HybridLorebookRetriever();
        var result2 = freshRetriever.Retrieve("아무 이야기", null, lorebooks);

        // Assert
        Assert.Empty(result2);
    }

    [Fact]
    public void Retrieve_WhenMatchedInBothSparseAndDense_DeduplicatesByName()
    {
        // Arrange
        var retriever = new HybridLorebookRetriever();
        var queryEmbedding = CreateDummyVector(0.036f);
        var entryEmbedding = CreateDummyVector(0.036f);

        var lorebooks = new List<LorebookEntry>
        {
            new("흑철검", ["흑철검"], "고대 드워프 검", "아이템", Priority: 10, Embedding: entryEmbedding)
        };

        // Act - 키워드("흑철검")도 일치하고, 임베딩 유사도(1.0)도 충족
        var result = retriever.Retrieve("흑철검을 들었다.", queryEmbedding, lorebooks);

        // Assert - 중복 없이 단 1개만 반환
        Assert.Single(result);
        Assert.Equal("흑철검", result[0].Name);
    }

    [Fact]
    public void Retrieve_WhenMultipleShortEntriesMatch_PacksMoreThanThreeWithinBudget()
    {
        // Arrange - 짧은 마이크로 로어북 6개 (키워드 일치, 각 약 30토큰)
        var retriever = new HybridLorebookRetriever();
        var lorebooks = new List<LorebookEntry>();
        for (int i = 1; i <= 6; i++)
        {
            lorebooks.Add(new LorebookEntry($"설정{i}", ["공통키워드"], $"짧은 설정 본문 {i}", "일반", Priority: 10 - i));
        }

        // Act - 유저 입력에 '공통키워드'가 있어 6개 모두 매칭됨 (총 토큰 약 240T <= 2000T)
        var result = retriever.Retrieve("공통키워드에 대해 이야기한다.", null, lorebooks, maxTokenBudget: 2000);

        // Assert - 기존 3개 고정 상한을 넘어 6개 모두 안전하게 패킹됨
        Assert.Equal(6, result.Count);
        Assert.Equal("설정1", result[0].Name);
        Assert.Equal("설정6", result[5].Name);
    }

    [Fact]
    public void Retrieve_WhenEntriesExceedBudget_CapsAtBudgetLimit()
    {
        // Arrange - 약 900토큰짜리 긴 로어북 3개 (한글 약 1300자)
        var retriever = new HybridLorebookRetriever();
        string longContent = new string('가', 1300); // 약 871 토큰

        var lorebooks = new List<LorebookEntry>
        {
            new("긴설정1", ["마법"], longContent, "일반", Priority: 10),
            new("긴설정2", ["마법"], longContent, "일반", Priority: 9),
            new("긴설정3", ["마법"], longContent, "일반", Priority: 8)
        };

        // Act - 1번(약 880T) + 2번(약 880T) = 약 1760T. 3번째가 추가되면 2000T를 초과하므로 2개에서 커트되어야 함
        var result = retriever.Retrieve("마법에 대해 묻는다.", null, lorebooks, maxTokenBudget: 2000);

        // Assert - 예산(2000T) 한도 내에서 정확히 2개만 인출됨
        Assert.Equal(2, result.Count);
        Assert.Equal("긴설정1", result[0].Name);
        Assert.Equal("긴설정2", result[1].Name);
    }

    [Fact]
    public void EstimateTokens_ReturnsReasonableEstimates()
    {
        // 한글 10자 -> 약 7토큰
        int koreanTokens = HybridLorebookRetriever.EstimateTokens("가나다라마바사아자차");
        Assert.True(koreanTokens >= 6 && koreanTokens <= 10);

        // 영문 20자 -> 약 5토큰
        int englishTokens = HybridLorebookRetriever.EstimateTokens("abcdefghijklmnopqrst");
        Assert.True(englishTokens >= 4 && englishTokens <= 8);
    }

    [Fact]
    public void Retrieve_StickyRetention_KeepsEntryForThreeTurnsTotal()
    {
        // Arrange
        var retriever = new HybridLorebookRetriever();
        var lorebooks = new List<LorebookEntry>
        {
            new("검은 칼", ["검은 칼"], "고대 룬 문자가 새겨진 저주받은 칼", "아이템", Priority: 10)
        };

        // Turn 1 (트리거 턴): 유저가 "검은 칼"을 직접 언급 -> 1순위 직접 매칭 (RemainingStickyTurns = 2 부여)
        var turn1Result = retriever.Retrieve("탁자 위의 검은 칼을 집어든다.", null, lorebooks);
        Assert.Single(turn1Result);
        Assert.Equal("검은 칼", turn1Result[0].Name);

        // Turn 2 (다음 턴): 유저가 대명사("그것")를 써서 키워드 미매칭 -> 2순위 Sticky 잔존으로 인출됨 (1턴 차감)
        var turn2Result = retriever.Retrieve("칼집에서 그것을 뽑아 칼날을 비춰본다.", null, lorebooks);
        Assert.Single(turn2Result);
        Assert.Equal("검은 칼", turn2Result[0].Name);

        // Turn 3 (다다음 턴): 유저가 키워드 없는 다른 행동 -> 2순위 Sticky 잔존으로 인출됨 (2턴 차감)
        var turn3Result = retriever.Retrieve("칼자루의 보석을 어루만지며 생각에 잠긴다.", null, lorebooks);
        Assert.Single(turn3Result);
        Assert.Equal("검은 칼", turn3Result[0].Name);

        // Turn 4 (4턴째): 추가 언급 없이 3턴 경과 만료 -> 드디어 프롬프트에서 자동 제외(소멸)
        var turn4Result = retriever.Retrieve("창밖의 달빛을 바라본다.", null, lorebooks);
        Assert.Empty(turn4Result);
    }

    [Fact]
    public void Retrieve_ActiveCharReplenishment_FillsRemainingBudget()
    {
        // Arrange - 직접 매칭 사물 로어북 1개 + 상태창 인물 로어북 2개
        var retriever = new HybridLorebookRetriever();
        var lorebooks = new List<LorebookEntry>
        {
            new("성배", ["성배"], "성스러운 은제 성배", "아이템", Priority: 10),
            new("루시엘", ["루시엘"], "은발 벽안의 마법사", "인물", Priority: 8),
            new("세레나", ["세레나"], "붉은 머리의 여기사", "인물", Priority: 7)
        };

        var activeChars = new List<string> { "루시엘", "세레나" };

        // Act - 유저는 "성배"만 언급함
        var result = retriever.Retrieve("성배에 와인을 가득 채운다.", null, lorebooks, activeCharNames: activeChars, maxTokenBudget: 3000);

        // Assert - 1순위 성배 + 3순위 상태창 인물 2명(루시엘, 세레나) 모두 3000토큰 내에 안전하게 패킹됨
        Assert.Equal(3, result.Count);
        Assert.Equal("성배", result[0].Name);    // 1순위 직접 매칭
        Assert.Equal("루시엘", result[1].Name);  // 3순위 상태창 인물 (Priority 8)
        Assert.Equal("세레나", result[2].Name);  // 3순위 상태창 인물 (Priority 7)
    }

    [Fact]
    public void Retrieve_WhenDirectMatchesExceedBudget_ActiveCharDoesNotEvictDirectMatches()
    {
        // Arrange - 거대한 직접 매칭 사물 로어북 2개 (각 약 1480토큰)
        var retriever = new HybridLorebookRetriever();
        string massiveContent = new string('가', 2200); // 약 1474토큰

        var lorebooks = new List<LorebookEntry>
        {
            new("고대전함", ["고대전함"], massiveContent, "아이템", Priority: 10),
            new("비행마법", ["고대전함"], massiveContent, "마법", Priority: 9),
            new("루시엘", ["루시엘"], new string('나', 100), "인물", Priority: 8) // 약 77토큰
        };

        var activeChars = new List<string> { "루시엘" };

        // Act - 1번(1484T) + 2번(1484T) = 2968T로 3000T 예산 소진, 루시엘(77T)은 예산 초과로 제외됨
        var result = retriever.Retrieve("고대전함에 탑승한다.", null, lorebooks, activeCharNames: activeChars, maxTokenBudget: 3000);

        // Assert - 1순위 직접 매칭 로어북들이 예산을 소진하여, 3순위 상태창 인물은 억지로 끼어들지 못함 (직접 매칭 우선 보장)
        Assert.Equal(2, result.Count);
        Assert.Equal("고대전함", result[0].Name);
        Assert.Equal("비행마법", result[1].Name);
        Assert.DoesNotContain(result, r => r.Name == "루시엘");
    }
}
