using GRC.Helpers;
using GRC.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GRC.Services;

/// <summary>
/// 2계층 미해결 단서 추적기 구현체 (멀티스레드 동시성 안전 및 하이브리드 시맨틱 게이팅 탑재)
/// </summary>
public class ClueTracker : IClueTracker
{
    public const int MaxActiveSlots = 3;
    public const int TtlTurns = 20;
    public const float SemanticSimilarityThreshold = 0.80f;
    public const int DefaultStickyTurns = 2; // 현재 턴 포함 총 3턴 활성 잔존 (트리거 턴 1 + 추가 2턴)

    private readonly object _syncRoot = new();
    private readonly List<ClueSlotItem> _activeClues = new(MaxActiveSlots);
    private readonly List<ClueSlotItem> _archivedClues = new();


    public IReadOnlyList<ClueSlotItem> ActiveClues
    {
        get
        {
            lock (_syncRoot)
            {
                return _activeClues.ToList().AsReadOnly();
            }
        }
    }

    public IReadOnlyList<ClueSlotItem> ArchivedClues
    {
        get
        {
            lock (_syncRoot)
            {
                return _archivedClues.ToList().AsReadOnly();
            }
        }
    }

    public string AddOrUpdateClue(string clueText, int currentTurn, string? id = null, float[]? embedding = null)
    {
        if (string.IsNullOrWhiteSpace(clueText)) return string.Empty;

        string targetId = string.IsNullOrWhiteSpace(id) ? $"clue_{Guid.NewGuid():N}" : id;

        lock (_syncRoot)
        {
            // 기존에 동일 ID가 있는지 검사
            int existingIndex = _activeClues.FindIndex(c => c.Id == targetId);
            if (existingIndex >= 0)
            {
                var existing = _activeClues[existingIndex];
                _activeClues[existingIndex] = existing with
                {
                    Clue = clueText,
                    LastReferencedTurn = currentTurn,
                    Embedding = embedding ?? existing.Embedding
                };
                return targetId;
            }

            // 슬롯 상한(Max 3) 도달 시 가장 오래된 1번 단서를 버리지 않고 Dormant 아카이브로 무손실 이관
            if (_activeClues.Count >= MaxActiveSlots)
            {
                var oldest = _activeClues[0];
                _activeClues.RemoveAt(0);
                _archivedClues.Add(oldest with { Status = ClueStatus.Dormant });
            }

            // 신규 단서 추가
            _activeClues.Add(new ClueSlotItem(
                Id: targetId,
                Clue: clueText,
                CreatedTurn: currentTurn,
                LastReferencedTurn: currentTurn,
                Status: ClueStatus.Active,
                Embedding: embedding
            ));

            return targetId;
        }
    }

    public void UpdateClueEmbedding(string clueId, float[] embedding)
    {
        if (string.IsNullOrWhiteSpace(clueId) || embedding == null || embedding.Length != 768)
            return;

        lock (_syncRoot)
        {
            int activeIndex = _activeClues.FindIndex(c => c.Id == clueId);
            if (activeIndex >= 0)
            {
                _activeClues[activeIndex] = _activeClues[activeIndex] with { Embedding = embedding };
                return;
            }

            int archivedIndex = _archivedClues.FindIndex(c => c.Id == clueId);
            if (archivedIndex >= 0)
            {
                _archivedClues[archivedIndex] = _archivedClues[archivedIndex] with { Embedding = embedding };
            }
        }
    }

    public void ResolveClue(string clueId)
    {
        if (string.IsNullOrWhiteSpace(clueId)) return;

        lock (_syncRoot)
        {
            _activeClues.RemoveAll(c => c.Id == clueId);
            _archivedClues.RemoveAll(c => c.Id == clueId);
        }
    }

    public void TickTurn(int currentTurn)
    {
        lock (_syncRoot)
        {
            for (int i = _activeClues.Count - 1; i >= 0; i--)
            {
                var clue = _activeClues[i];
                if (currentTurn - clue.LastReferencedTurn >= TtlTurns)
                {
                    // 20턴 동안 미언급 시 Dormant 전환 후 L2 아카이브로 이관
                    _activeClues.RemoveAt(i);
                    _archivedClues.Add(clue with { Status = ClueStatus.Dormant });
                }
            }
        }
    }

    private static readonly HashSet<string> MetaStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "단서", "비밀", "정보", "기록", "사건", "상태", "내용", "아이템", "물건", "존재", "의문", "의혹"
    };

    public List<ClueSlotItem> GetGatedClues(string userInput, float[]? userQueryEmbedding = null)
    {
        lock (_syncRoot)
        {
            if (_activeClues.Count == 0 && _archivedClues.Count == 0) return [];

            var matched = new List<ClueSlotItem>();
            var directMatchedIds = new HashSet<string>();

            // 활성 단서(Active) 및 콜드 아카이브(Archived) 대상 전수 핀포인트 검사
            var allClues = _activeClues.Concat(_archivedClues).DistinctBy(c => c.Id).ToList();

            if (!string.IsNullOrWhiteSpace(userInput))
            {
                foreach (var clue in allClues)
                {
                    // 1. 단서 본문에서 2글자 이상 핵심 명사/단어가 유저 입력에 직접 일치하는지 검사 (Fast-Path)
                    var tokens = clue.Clue.Split([' ', ',', '.', '\'', '\"', ':', ';', '!', '?', '-', '_', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
                    bool tokenMatched = tokens.Any(t => t.Length >= 2 && !MetaStopWords.Contains(t) && userInput.Contains(t, StringComparison.OrdinalIgnoreCase));

                    bool semanticMatched = false;
                    // 2. 시맨틱 임베딩 코사인 유사도 검사 (Semantic Path, >= 0.80)
                    if (!tokenMatched && userQueryEmbedding != null && userQueryEmbedding.Length == 768 &&
                        clue.Embedding != null && clue.Embedding.Length == 768)
                    {
                        float similarity = VectorMathHelper.CosineSimilarity(userQueryEmbedding, clue.Embedding);
                        if (similarity >= SemanticSimilarityThreshold)
                        {
                            semanticMatched = true;
                        }
                    }

                    if (tokenMatched || semanticMatched)
                    {
                        directMatchedIds.Add(clue.Id);
                        // 직접 매칭된 단서는 잔존 턴을 2로 충전 (현재 턴 포함 총 3턴 활성 잔존)
                        var refreshed = clue with { RemainingStickyTurns = DefaultStickyTurns };
                        UpdateClueInternal(refreshed);
                        matched.Add(refreshed);
                    }
                }
            }

            // 3. 이번 턴에 직접 매칭되지 않았지만, 이전 턴에 인출되어 잔존 중(RemainingStickyTurns > 0)인 단서 유지
            foreach (var clue in allClues)
            {
                if (!directMatchedIds.Contains(clue.Id) && clue.RemainingStickyTurns > 0)
                {
                    // 1턴 소진 (Sticky 카운트다운)
                    var decayed = clue with { RemainingStickyTurns = clue.RemainingStickyTurns - 1 };
                    UpdateClueInternal(decayed);
                    matched.Add(decayed);
                }
            }

            // 중복 방지 및 최대 MaxActiveSlots(3개)까지만 인출
            return matched
                .DistinctBy(c => c.Id)
                .Take(MaxActiveSlots)
                .ToList();
        }
    }

    private void UpdateClueInternal(ClueSlotItem item)
    {
        int activeIdx = _activeClues.FindIndex(c => c.Id == item.Id);
        if (activeIdx >= 0)
        {
            _activeClues[activeIdx] = item;
            return;
        }

        int archivedIdx = _archivedClues.FindIndex(c => c.Id == item.Id);
        if (archivedIdx >= 0)
        {
            _archivedClues[archivedIdx] = item;
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _activeClues.Clear();
            _archivedClues.Clear();
        }
    }

    public void RestoreClues(IEnumerable<ClueSlotItem>? activeClues, IEnumerable<ClueSlotItem>? archivedClues)
    {
        lock (_syncRoot)
        {
            _activeClues.Clear();
            _archivedClues.Clear();

            if (activeClues != null)
            {
                _activeClues.AddRange(activeClues);
            }

            if (archivedClues != null)
            {
                _archivedClues.AddRange(archivedClues);
            }
        }
    }
}
