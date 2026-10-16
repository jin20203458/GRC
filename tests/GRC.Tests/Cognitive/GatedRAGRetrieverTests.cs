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
    public void RetrieveRecalledHistory_ReturnsOnlyAboveSemanticThreshold()
    {
        // Arrange
        var retriever = new GatedRAGRetriever();
        var queryVec = CreateDummyVector(0.036f);

        // 일치 벡터 (유사도 1.0 >= 0.71)
        var hitVec = CreateDummyVector(0.036f);

        // 직교에 가까운 벡터 (유사도 약 0.0 < 0.71)
        var missVec = new float[768];
        for (int i = 0; i < 768; i++) missVec[i] = (i % 2 == 0) ? 0.036f : -0.036f;

        var ledgers = new List<EventLedgerItem>
        {
            new(FactBullet: "적중 사건: 안개 숲에서 은반지 습득", Embedding: hitVec, Chapter: 1),
            new(FactBullet: "미적중 사건: 다른 곳에서 휴식", Embedding: missVec, Chapter: 1)
        };

        // Act
        var result = retriever.RetrieveRecalledHistory(queryVec, ledgers, maxResults: 2);

        // Assert - 0.71 이상인 적중 사건 1개만 인출
        Assert.Single(result);
        Assert.Equal("적중 사건: 안개 숲에서 은반지 습득", result[0].FactBullet);
    }

    [Fact]
    public void RetrieveRecalledHistory_WhenQueryEmbeddingNull_ReturnsEmpty()
    {
        // Arrange
        var retriever = new GatedRAGRetriever();
        var ledgers = new List<EventLedgerItem>
        {
            new(FactBullet: "사건 1", Embedding: CreateDummyVector(0.036f), Chapter: 1)
        };

        // Act
        var result = retriever.RetrieveRecalledHistory(null, ledgers);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void RetrieveRecalledHistory_ReturnsItemsSortedByIndexAscending_PreservingTemporalCausality()
    {
        // Arrange
        var retriever = new GatedRAGRetriever();
        var queryVec = CreateDummyVector(0.036f);

        // 원장 인덱스 0(과거 사건)의 유사도를 0.95, 인덱스 1(최신 사건)의 유사도를 1.0으로 설정
        var midSimVec = CreateDummyVector(0.036f);
        midSimVec[0] = 0.0f; // 약간 다르게 하여 유사도 살짝 낮춤 (~0.99)
        var highSimVec = CreateDummyVector(0.036f); // sim 1.0

        var ledgers = new List<EventLedgerItem>
        {
            new(FactBullet: "먼저 일어난 사건: 독화살 피격", Embedding: midSimVec, Chapter: 1), // Index 0
            new(FactBullet: "나중에 일어난 사건: 해독제 복용", Embedding: highSimVec, Chapter: 2) // Index 1
        };

        // Act - 유사도가 높은 것은 뒤의 사건(인덱스 1)이지만, 인출 결과는 단일 원장의 인덱스 순(0 -> 1)으로 반환되어야 함
        var result = retriever.RetrieveRecalledHistory(queryVec, ledgers, maxResults: 2);

        // Assert - 인과 역전 방지 검증
        Assert.Equal(2, result.Count);
        Assert.Equal("먼저 일어난 사건: 독화살 피격", result[0].FactBullet);
        Assert.Equal("나중에 일어난 사건: 해독제 복용", result[1].FactBullet);
    }

    [Fact]
    public void RetrieveRecalledHistory_CapsAtMaxResults()
    {
        // Arrange
        var retriever = new GatedRAGRetriever();
        var queryVec = CreateDummyVector(0.036f);
        var hitVec = CreateDummyVector(0.036f);

        var ledgers = new List<EventLedgerItem>
        {
            new(FactBullet: "사건 1", Embedding: hitVec, Chapter: 1),
            new(FactBullet: "사건 2", Embedding: hitVec, Chapter: 1),
            new(FactBullet: "사건 3", Embedding: hitVec, Chapter: 1)
        };

        // Act
        var result = retriever.RetrieveRecalledHistory(queryVec, ledgers, maxResults: 2);

        // Assert
        Assert.Equal(2, result.Count);
    }
}
