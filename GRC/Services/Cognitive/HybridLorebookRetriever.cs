using GRC.Helpers;
using GRC.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GRC.Services;

/// <summary>
/// 병렬 합집합(Sparse 정규식 OR Dense 시맨틱 임베딩) 하이브리드 로어북 검색기 구현체
/// </summary>
public class HybridLorebookRetriever : IHybridLorebookRetriever
{
    public const float DenseThreshold = 0.68f;
    public const int DefaultMaxTokenBudget = 3000;
    public const int DefaultStickyTurns = 2; // 현재 턴 포함 총 3턴 활성 잔존 (트리거 턴 1 + 추가 2턴)

    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, int> _stickyTurns = new(StringComparer.OrdinalIgnoreCase);

    public List<LorebookEntry> Retrieve(
        string userQuery,
        float[]? queryEmbedding,
        IEnumerable<LorebookEntry> allLorebooks,
        IEnumerable<string>? activeCharNames = null,
        int maxTokenBudget = DefaultMaxTokenBudget)
    {
        if (allLorebooks == null) return [];
        var lorebookList = allLorebooks.ToList();
        if (lorebookList.Count == 0) return [];

        lock (_syncRoot)
        {
            // [1단계] 이번 턴 직접 매칭 (Sparse 정규식 OR Dense 시맨틱 임베딩 >= DenseThreshold)
            var directMatched = new List<LorebookEntry>();
            if (!string.IsNullOrWhiteSpace(userQuery))
            {
                foreach (var entry in lorebookList)
                {
                    if (IsSparseMatched(userQuery, entry.Keywords))
                    {
                        directMatched.Add(entry);
                        continue;
                    }

                    if (queryEmbedding != null && queryEmbedding.Length == 768 &&
                        entry.Embedding != null && entry.Embedding.Length == 768)
                    {
                        float similarity = VectorMathHelper.CosineSimilarity(queryEmbedding, entry.Embedding);
                        if (similarity >= DenseThreshold)
                        {
                            directMatched.Add(entry);
                        }
                    }
                }
            }

            var directMatchedNames = new HashSet<string>(directMatched.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

            // 직접 매칭된 로어북은 잔존 턴을 2로 충전 (현재 턴 포함 총 3턴 활성)
            foreach (var name in directMatchedNames)
            {
                _stickyTurns[name] = DefaultStickyTurns;
            }

            // [2단계] 이전 턴에서 인출되어 잔존 중(Sticky)인 로어북
            var stickyMatched = new List<LorebookEntry>();
            var activeStickyNames = _stickyTurns.Keys.ToList();
            foreach (var name in activeStickyNames)
            {
                if (!directMatchedNames.Contains(name))
                {
                    int remaining = _stickyTurns[name];
                    if (remaining > 0)
                    {
                        var entry = lorebookList.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (entry != null)
                        {
                            stickyMatched.Add(entry);
                        }
                        // 1턴 소진 (Sticky 카운트다운)
                        _stickyTurns[name] = remaining - 1;
                    }
                    else
                    {
                        _stickyTurns.Remove(name);
                    }
                }
            }

            // [3단계] 상태창 등장인물(activeCharNames) 로어북 후보
            var charMatched = new List<LorebookEntry>();
            if (activeCharNames != null)
            {
                var charNameSet = new HashSet<string>(activeCharNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
                if (charNameSet.Count > 0)
                {
                    foreach (var entry in lorebookList)
                    {
                        // 이미 1단계(직접)나 2단계(잔존)에 포함된 항목은 중복 제외
                        if (directMatchedNames.Contains(entry.Name) || stickyMatched.Any(s => string.Equals(s.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        // 이름 또는 키워드가 상태창 활성 인물 이름과 일치하는 경우에만 후보로 추가
                        bool isCharEntry = charNameSet.Contains(entry.Name) ||
                                           (entry.Keywords != null && entry.Keywords.Any(k => charNameSet.Contains(k)));

                        if (isCharEntry)
                        {
                            charMatched.Add(entry);
                        }
                    }
                }
            }

            // [4단계] 3단계 우선순위 동적 패킹 (최대 maxTokenBudget)
            // 1순위: 직접 매칭 (Priority 내림차순)
            // 2순위: 이전 잔존 (Priority 내림차순)
            // 3순위: 상태창 인물 (Priority 내림차순)
            var sortedDirect = directMatched.DistinctBy(e => e.Name).OrderByDescending(e => e.Priority).ToList();
            var sortedSticky = stickyMatched.DistinctBy(e => e.Name).OrderByDescending(e => e.Priority).ToList();
            var sortedChar = charMatched.DistinctBy(e => e.Name).OrderByDescending(e => e.Priority).ToList();

            var candidateOrder = sortedDirect
                .Concat(sortedSticky)
                .Concat(sortedChar)
                .DistinctBy(e => e.Name)
                .ToList();

            var selected = new List<LorebookEntry>();
            int accumulatedTokens = 0;

            foreach (var entry in candidateOrder)
            {
                int entryTokens = EstimateTokens(entry.Name) + EstimateTokens(entry.Content) + 10;

                // 첫 번째 항목은 예산을 초과하더라도 최소 1개는 포함 보장 (Zero Starvation)
                if (selected.Count > 0 && accumulatedTokens + entryTokens > maxTokenBudget)
                {
                    continue; // 더 작은 다음 항목이 들어갈 수 있는지 탐색
                }

                selected.Add(entry);
                accumulatedTokens += entryTokens;
            }

            return selected;
        }
    }

    public void ClearSticky()
    {
        lock (_syncRoot)
        {
            _stickyTurns.Clear();
        }
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
