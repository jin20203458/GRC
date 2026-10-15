using GRC.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GRC.Services;

/// <summary>
/// 2계층 미해결 단서 추적기 구현체 (3중 결정론적 안전망 탑재)
/// </summary>
public class ClueTracker : IClueTracker
{
    public const int MaxActiveSlots = 3;
    public const int TtlTurns = 20;

    private readonly List<ClueSlotItem> _activeClues = new(MaxActiveSlots);
    private readonly List<ClueSlotItem> _archivedClues = new();

    private static readonly string[] DefaultGatingKeywords =
    [
        "주머니", "쪽지", "편지", "비밀", "숨기", "숨긴", "추궁", "의심",
        "단서", "의문", "열쇠", "인장", "목걸이", "반지", "털어놓", "진실"
    ];

    public IReadOnlyList<ClueSlotItem> ActiveClues => _activeClues.AsReadOnly();
    public IReadOnlyList<ClueSlotItem> ArchivedClues => _archivedClues.AsReadOnly();

    public void AddOrUpdateClue(string clueText, int currentTurn, string? id = null, float[]? embedding = null)
    {
        if (string.IsNullOrWhiteSpace(clueText)) return;

        string targetId = string.IsNullOrWhiteSpace(id) ? $"clue_{Guid.NewGuid():N}" : id;

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
            return;
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
    }

    public void ResolveClue(string clueId)
    {
        if (string.IsNullOrWhiteSpace(clueId)) return;

        _activeClues.RemoveAll(c => c.Id == clueId);
        _archivedClues.RemoveAll(c => c.Id == clueId);
    }

    public void TickTurn(int currentTurn)
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

    public List<ClueSlotItem> GetGatedClues(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput) || _activeClues.Count == 0)
            return [];

        // 유저 입력에 추궁/수색 키워드가 포함되었거나, 특정 단서의 단어가 부분 일치하는지 검사
        bool containsGeneralGatingKeyword = DefaultGatingKeywords.Any(k =>
            userInput.Contains(k, StringComparison.OrdinalIgnoreCase));

        var matched = new List<ClueSlotItem>();
        foreach (var clue in _activeClues)
        {
            if (containsGeneralGatingKeyword)
            {
                matched.Add(clue);
                continue;
            }

            // 단서 본문에서 2글자 이상 핵심 명사/단어가 유저 입력에 있는지 검사
            var tokens = clue.Clue.Split([' ', ',', '.', '\'', '\"'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Any(t => t.Length >= 2 && userInput.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                matched.Add(clue);
            }
        }

        return matched;
    }

    public void Clear()
    {
        _activeClues.Clear();
        _archivedClues.Clear();
    }

    public void RestoreClues(IEnumerable<ClueSlotItem>? activeClues, IEnumerable<ClueSlotItem>? archivedClues)
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
