using GRC.Helpers;
using GRC.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GRC.Services;

/// <summary>
/// 병렬 합집합(Sparse OR Dense 0.85) 하이브리드 로어북 검색기 구현체
/// </summary>
public class HybridLorebookRetriever : IHybridLorebookRetriever
{
    public const float DenseThreshold = 0.85f;
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    public List<LorebookEntry> Retrieve(
        string userQuery,
        float[]? queryEmbedding,
        IEnumerable<LorebookEntry> allLorebooks,
        int maxResults = 3)
    {
        if (allLorebooks == null) return [];
        var lorebookList = allLorebooks.ToList();
        if (lorebookList.Count == 0) return [];

        var matchedEntries = new List<LorebookEntry>();

        // 1. Sparse 검색: 키워드 정규식 직접 매칭
        if (!string.IsNullOrWhiteSpace(userQuery))
        {
            foreach (var entry in lorebookList)
            {
                if (IsSparseMatched(userQuery, entry.Keywords))
                {
                    matchedEntries.Add(entry);
                }
            }
        }

        // 2. Dense 검색: 시맨틱 임베딩 코사인 유사도 >= 0.85 (queryEmbedding != null 일 때만)
        if (queryEmbedding != null && queryEmbedding.Length == 768)
        {
            foreach (var entry in lorebookList)
            {
                if (entry.Embedding == null || entry.Embedding.Length != 768)
                    continue;

                float similarity = VectorMathHelper.CosineSimilarity(queryEmbedding, entry.Embedding);
                if (similarity >= DenseThreshold)
                {
                    matchedEntries.Add(entry);
                }
            }
        }

        // 3. 병렬 합집합 & Name 기준 중복 제거 & Priority 내림차순 정렬 후 상위 maxResults개 반환
        return matchedEntries
            .DistinctBy(e => e.Name)
            .OrderByDescending(e => e.Priority)
            .Take(maxResults)
            .ToList();
    }

    private static bool IsSparseMatched(string text, List<string>? keywords)
    {
        if (keywords == null || keywords.Count == 0) return false;

        var validKeywords = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .OrderByDescending(k => k.Length)
            .ToList();

        if (validKeywords.Count == 0) return false;

        string cacheKey = string.Join("|", validKeywords);
        var regex = RegexCache.GetOrAdd(cacheKey, k =>
        {
            string escaped = string.Join("|", validKeywords.Select(Regex.Escape));
            // 한국어 조사(은/는/이/가/을/를/에게/에서 등) 최대 3글자 허용 패턴
            return new Regex($@"(?<![가-힣a-zA-Z0-9])({escaped})[가-힣]{{0,3}}(?![가-힣a-zA-Z0-9])",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        });

        return regex.IsMatch(text);
    }
}
