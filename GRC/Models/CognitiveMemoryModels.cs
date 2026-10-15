using System.Collections.Generic;

namespace GRC.Models;

/// <summary>
/// 8턴 주기로 추출되는 불변 사건 렛저 항목 (Append-Only 기록)
/// </summary>
/// <param name="Turn">사건 발생 턴 번호</param>
/// <param name="FactBullet">1~2문장의 객관적 사실 불릿</param>
/// <param name="Embedding">선택적 시맨틱 검색용 임베딩 벡터 (768차원)</param>
public record EventLedgerItem(
    int Turn,
    string FactBullet,
    float[]? Embedding = null
);

/// <summary>
/// 인물/환경의 수치 및 심리 상태를 추적하는 엔티티 슬롯 항목
/// </summary>
/// <param name="Name">엔티티 명칭 (예: 엘리아스)</param>
/// <param name="StatValue">추적 수치 (예: 45%)</param>
/// <param name="MentalStage">심리/관계 단계 (예: 신뢰 형성 단계)</param>
/// <param name="ConditionDesc">신체/마력 컨디션 묘사 (예: 마력 고갈로 쇠약)</param>
/// <param name="InitialAnchor">원초적 과거 시작점 (불변 앵커)</param>
public record EntitySlotItem(
    string Name,
    string StatValue,
    string MentalStage,
    string ConditionDesc,
    string InitialAnchor = ""
);

/// <summary>
/// 단서 및 복선의 라이프사이클 상태
/// </summary>
public enum ClueStatus
{
    Active,    // 1계층 슬롯에서 활성 추적 중
    Resolved,  // 진실이 밝혀지거나 해결됨 (삭제 대상)
    Dormant    // 20턴 TTL 만료 또는 슬롯 상한으로 2계층 콜드 아카이브로 이관됨
}

/// <summary>
/// 미해결 떡밥 및 단서를 추적하는 슬롯 항목
/// </summary>
/// <param name="Id">단서 고유 ID</param>
/// <param name="Clue">단서 내용</param>
/// <param name="CreatedTurn">발생 턴</param>
/// <param name="LastReferencedTurn">마지막으로 언급/참조된 턴</param>
/// <param name="Status">현재 라이프사이클 상태</param>
/// <param name="Embedding">선택적 시맨틱 검색용 벡터</param>
/// <param name="RemainingStickyTurns">인출 후 추가 잔존 턴 수 (0: 즉시 휘발, 2: 2턴 더 잔존)</param>
public record ClueSlotItem(
    string Id,
    string Clue,
    int CreatedTurn,
    int LastReferencedTurn,
    ClueStatus Status = ClueStatus.Active,
    float[]? Embedding = null,
    int RemainingStickyTurns = 0
);

/// <summary>
/// Cognitive Core 3.0 프롬프트 조립을 위한 정형 컨텍스트 DTO
/// </summary>
public record CognitiveContext(
    string MacroChronicle,
    List<EventLedgerItem> LedgerFacts,
    List<EntitySlotItem> Entities,
    List<ClueSlotItem> ActiveClues,
    List<LorebookEntry> RetrievedLores,
    List<EventLedgerItem> RecalledHistory,
    string? PlotCatalyst = null // Phase 5 확장 훅
);
