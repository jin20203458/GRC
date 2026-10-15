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
        if (string.IsNullOrWhiteSpace(userInput))
            return [];

        lock (_syncRoot)
        {
            if (_activeClues.Count == 0 && _archivedClues.Count == 0) return [];

            var matched = new List<ClueSlotItem>();

            // 활성 단서(Active)를 우선 탐색하고, 필요 시 콜드 아카이브(Archived)까지 전수 핀포인트 탐색
            var candidatePool = _activeClues.Concat(_archivedClues);

            foreach (var clue in candidatePool)
            {
                // 1. 단서 본문에서 2글자 이상 핵심 명사/단어가 유저 입력에 직접 일치하는지 검사 (Fast-Path)
                // 메타 수식어(단서, 비밀, 정보 등)는 불용어 제외하여 위양성 차단
                var tokens = clue.Clue.Split([' ', ',', '.', '\'', '\"', ':', ';', '!', '?', '-', '_', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
                bool tokenMatched = tokens.Any(t => t.Length >= 2 && !MetaStopWords.Contains(t) && userInput.Contains(t, StringComparison.OrdinalIgnoreCase));

                if (tokenMatched)
                {
                    matched.Add(clue);
                    continue;
                }

                // 2. 시맨틱 임베딩 코사인 유사도 검사 (동의어/이음동의어 Semantic Path, >= 0.80)
                if (userQueryEmbedding != null && userQueryEmbedding.Length == 768 &&
                    clue.Embedding != null && clue.Embedding.Length == 768)
                {
                    float similarity = VectorMathHelper.CosineSimilarity(userQueryEmbedding, clue.Embedding);
                    if (similarity >= SemanticSimilarityThreshold)
                    {
                        matched.Add(clue);
                    }
                }
            }

            // 중복 방지 및 최대 MaxActiveSlots(3개)까지만 인출
            return matched
                .DistinctBy(c => c.Id)
                .Take(MaxActiveSlots)
                .ToList();
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
