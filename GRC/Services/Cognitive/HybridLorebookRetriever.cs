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
    public const int DefaultMaxTokenBudget = 2000;
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    public List<LorebookEntry> Retrieve(
        string userQuery,
        float[]? queryEmbedding,
        IEnumerable<LorebookEntry> allLorebooks,
        int maxTokenBudget = DefaultMaxTokenBudget)
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

        // 3. 병렬 합집합 & Name 기준 중복 제거 & Priority 내림차순 정렬
        var sortedEntries = matchedEntries
            .DistinctBy(e => e.Name)
            .OrderByDescending(e => e.Priority)
            .ToList();

        // 4. 토큰 예산(기본 2000 토큰) 기반 동적 패킹
        var selected = new List<LorebookEntry>();
        int accumulatedTokens = 0;

        foreach (var entry in sortedEntries)
        {
            // 항목 이름 + 본문 + 마크다운 태그 오버헤드(약 10토큰)
            int entryTokens = EstimateTokens(entry.Name) + EstimateTokens(entry.Content) + 10;

            // 첫 번째 항목은 2000토큰을 초과하더라도 최소 1개는 포함 보장 (Zero Starvation)
            if (selected.Count > 0 && accumulatedTokens + entryTokens > maxTokenBudget)
            {
                break;
            }

            selected.Add(entry);
            accumulatedTokens += entryTokens;
        }

        return selected;
    }

    /// <summary>
    /// 한국어/영문 혼합 텍스트의 토큰 수를 근사 계산합니다.
    /// (한글: ~1.5자당 1토큰, 영문/기호: ~4자당 1토큰)
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int koreanChars = 0;
        int otherChars = 0;
        foreach (char c in text)
        {
            if ((c >= 0xAC00 && c <= 0xD7A3) || (c >= 0x3131 && c <= 0x318E))
                koreanChars++;
            else
                otherChars++;
        }
        return (int)Math.Ceiling(koreanChars * 0.67f + otherChars * 0.25f);
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
