using GRC.Helpers;
using GRC.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GRC.Services;

/// <summary>
/// 회상 살리언스 스코어링 기반 휴리스틱 게이팅 RAG 검색기 구현체
/// </summary>
public class GatedRAGRetriever : IGatedRAGRetriever
{
    public const int GateThreshold = 3;
    public const float SemanticSimilarityThreshold = 0.82f;

    private static readonly string[] TemporalAnchors =
    [
        "예전", "과거", "그때", "옛날", "기억", "지난번", "아까", "전에", "약속"
    ];

    private static readonly string[] InterrogativeEndings =
    [
        "?", "있어", "였지", "인가", "했잖아", "어디", "언제", "누구", "기억나"
    ];

    public int CalculateRecallScore(string userInput, IEnumerable<string>? pastEntities)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return 0;

        int score = 0;

        // 1. 시간 부사 / 회상 앵커 (+3점)
        foreach (var anchor in TemporalAnchors)
        {
            if (userInput.Contains(anchor, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
                break; // 부사 중복 점수 과다 부여 방지 (1회 3점)
            }
        }

        // 2. 과거 등록 엔티티/고유명사 일치 (+2점)
        if (pastEntities != null)
        {
            foreach (var entity in pastEntities)
            {
                if (!string.IsNullOrWhiteSpace(entity) &&
                    entity.Length >= 2 &&
                    userInput.Contains(entity, StringComparison.OrdinalIgnoreCase))
                {
                    score += 2;
                    break;
                }
            }
        }

        // 3. 의문형 / 물음표 종결 어미 (+1점)
        foreach (var ending in InterrogativeEndings)
        {
            if (userInput.Contains(ending, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
                break;
            }
        }

        return score;
    }

    public bool ShouldTriggerRAG(string userInput, IEnumerable<string>? pastEntities)
    {
        return CalculateRecallScore(userInput, pastEntities) >= GateThreshold;
    }

    public List<EventLedgerItem> RetrieveRecalledHistory(
        float[]? queryEmbedding,
        IEnumerable<EventLedgerItem>? archivedLedgers,
        int maxResults = 2)
    {
        if (queryEmbedding == null || queryEmbedding.Length != 768 || archivedLedgers == null)
            return [];

        var ledgerList = archivedLedgers.ToList();
        if (ledgerList.Count == 0) return [];

        var scored = new List<(EventLedgerItem Item, float Similarity)>();
        foreach (var ledger in ledgerList)
        {
            if (ledger.Embedding == null || ledger.Embedding.Length != 768)
                continue;

            float sim = VectorMathHelper.CosineSimilarity(queryEmbedding, ledger.Embedding);
            if (sim >= SemanticSimilarityThreshold)
            {
                scored.Add((ledger, sim));
            }
        }

        return scored
            .OrderByDescending(s => s.Similarity)
            .Take(maxResults)
            .Select(s => s.Item)
            .ToList();
    }
}
