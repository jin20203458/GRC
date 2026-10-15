using GRC.Models;
using System.Collections.Generic;

namespace GRC.Services;

/// <summary>
/// 2계층 미해결 단서 추적기 인터페이스 (L1 Active Clues -> L2 Cold Archive)
/// </summary>
public interface IClueTracker
{
    /// <summary>
    /// 현재 활성 추적 중인 1계층 단서 목록 (최대 3개 고정)
    /// </summary>
    IReadOnlyList<ClueSlotItem> ActiveClues { get; }

    /// <summary>
    /// 20턴 TTL 만료 또는 슬롯 상한으로 2계층으로 이관된 콜드 아카이브 목록
    /// </summary>
    IReadOnlyList<ClueSlotItem> ArchivedClues { get; }

    /// <summary>
    /// 새로운 단서를 등록하거나 기존 단서를 갱신합니다.
    /// 슬롯 상한(Max 3) 도달 시 가장 오래된 1번 단서를 Dormant 상태로 아카이브에 무손실 이관합니다.
    /// </summary>
    void AddOrUpdateClue(string clueText, int currentTurn, string? id = null, float[]? embedding = null);

    /// <summary>
    /// 단서의 진실이 밝혀지거나 해결되었을 때 메모리에서 완전 삭제합니다.
    /// </summary>
    void ResolveClue(string clueId);

    /// <summary>
    /// 턴 진행에 따른 TTL(Time-To-Live, 20턴) 만료를 검사하고 Dormant 아카이브로 자동 이관합니다.
    /// </summary>
    void TickTurn(int currentTurn);

    /// <summary>
    /// 유저 입력에 추궁/수색/의심 키워드가 포함된 경우에만 활성 단서를 조건부 반환합니다. (Chekhov's Gun 방어)
    /// </summary>
    List<ClueSlotItem> GetGatedClues(string userInput);

    /// <summary>
    /// 활성 단서 및 아카이브 단서를 모두 초기화합니다.
    /// </summary>
    void Clear();

    /// <summary>
    /// 세션 복원 시 활성 단서 및 아카이브 단서를 주입합니다.
    /// </summary>
    void RestoreClues(IEnumerable<ClueSlotItem>? activeClues, IEnumerable<ClueSlotItem>? archivedClues);
}
