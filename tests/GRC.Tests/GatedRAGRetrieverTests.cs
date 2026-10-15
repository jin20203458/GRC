using GRC.Models;
using GRC.Services;
using System.Collections.Generic;
using Xunit;

namespace GRC.Tests;

public class GatedRAGRetrieverTests
{
    private static float[] CreateDummyVector(float fillVal)
    {
        var v = new float[768];
        for (int i = 0; i < 768; i++) v[i] = fillVal;
        return v;
    }

    [Fact]
    public void CalculateRecallScore_WhenDailyChat_ReturnsZeroOrLowScore()
    {
        // Arrange
        var retriever = new GatedRAGRetriever();
        string dailyChat = "오늘 날씨가 참 좋다. 맛있는 저녁이나 먹으러 가자.";

        // Act
        int score = retriever.CalculateRecallScore(dailyChat, ["안개 숲", "흑철검"]);
        bool shouldTrigger = retriever.ShouldTriggerRAG(dailyChat, ["안개 숲", "흑철검"]);

        // Assert - 0점, RAG 바이패스(false)
        Assert.Equal(0, score);
        Assert.False(shouldTrigger);
    }

    [Fact]
    public void CalculateRecallScore_WhenTemporalAnchorAndEntityPresent_ExceedsThreshold()
    {
        // Arrange
        var retriever = new GatedRAGRetriever();
        // "예전에"(+3) + "안개 숲"(+2) + "기억나"(+3, 부사중복제어) + "?"(+1) => 최소 6점 이상
        string recallChat = "예전에 안개 숲에서 일어났던 일 기억나?";

        // Act
        int score = retriever.CalculateRecallScore(recallChat, ["안개 숲", "흑철검"]);
        bool shouldTrigger = retriever.ShouldTriggerRAG(recallChat, ["안개 숲", "흑철검"]);

        // Assert
        Assert.True(score >= 3);
        Assert.True(shouldTrigger);
    }

    [Fact]
    public void RetrieveRecalledHistory_ReturnsOnlyAboveSemanticThreshold()
    {
        // Arrange
        var retriever = new GatedRAGRetriever();
        var queryVec = CreateDummyVector(0.036f);

        // 일치 벡터 (유사도 1.0 >= 0.82)
        var hitVec = CreateDummyVector(0.036f);

        // 직교에 가까운 벡터 (유사도 약 0.0 < 0.82)
        var missVec = new float[768];
        for (int i = 0; i < 768; i++) missVec[i] = (i % 2 == 0) ? 0.036f : -0.036f;

        var ledgers = new List<EventLedgerItem>
        {
            new(Turn: 10, FactBullet: "적중 사건: 안개 숲에서 은반지 습득", Embedding: hitVec),
            new(Turn: 20, FactBullet: "미적중 사건: 다른 곳에서 휴식", Embedding: missVec)
        };

        // Act
        var result = retriever.RetrieveRecalledHistory(queryVec, ledgers, maxResults: 2);

        // Assert - 0.82 이상인 적중 사건 1개만 인출
        Assert.Single(result);
        Assert.Equal(10, result[0].Turn);
    }

    [Fact]
    public void RetrieveRecalledHistory_WhenQueryEmbeddingNull_ReturnsEmpty()
    {
        // Arrange
        var retriever = new GatedRAGRetriever();
        var ledgers = new List<EventLedgerItem>
        {
            new(Turn: 10, FactBullet: "사건 1", Embedding: CreateDummyVector(0.036f))
        };

        // Act
        var result = retriever.RetrieveRecalledHistory(null, ledgers);

        // Assert
        Assert.Empty(result);
    }
}
