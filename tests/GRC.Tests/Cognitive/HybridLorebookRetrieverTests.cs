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
        var result2 = retriever.Retrieve("아무 이야기", null, lorebooks);

        // Assert
        Assert.Single(result1);
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
}
