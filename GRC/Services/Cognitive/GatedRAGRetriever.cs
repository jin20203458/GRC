using GRC.Helpers;
using GRC.Models;
using System.Collections.Generic;
using System.Linq;

namespace GRC.Services;

/// <summary>
/// 임베딩 시맨틱 검색 기반 과거 챕터 아카이브 렛저 회상 RAG 구현체
/// 실측 Ground Truth 기준 유사도 임계치 0.71f 및 단일 원장 인덱스 시간순 인과 정렬 보장
/// </summary>
public class GatedRAGRetriever : IGatedRAGRetriever
{
    public const float SemanticSimilarityThreshold = 0.71f;

    public List<EventLedgerItem> RetrieveRecalledHistory(
        float[]? queryEmbedding,
        IEnumerable<EventLedgerItem>? archivedLedgers,
        int maxResults = 2)
    {
        if (queryEmbedding == null || queryEmbedding.Length != 768 || archivedLedgers == null)
            return [];

        var ledgerList = archivedLedgers.ToList();
        if (ledgerList.Count == 0) return [];

        var scored = new List<(EventLedgerItem Item, float Similarity, int Index)>();
        for (int i = 0; i < ledgerList.Count; i++)
        {
            var ledger = ledgerList[i];
            if (ledger.Embedding == null || ledger.Embedding.Length != 768)
                continue;

            float sim = VectorMathHelper.CosineSimilarity(queryEmbedding, ledger.Embedding);
            if (sim >= SemanticSimilarityThreshold)
            {
                scored.Add((ledger, sim, i));
            }
        }

        return scored
            .OrderByDescending(s => s.Similarity)
            .Take(maxResults)
            .OrderBy(s => s.Index)
            .Select(s => s.Item)
            .ToList();
    }
}
