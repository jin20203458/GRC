using System.Collections.Generic;

namespace GRC.Models;

/// <summary>
/// 4턴(8메시지) 주기로 추출되는 불변 사건 렛저 항목 (단일 원장 Append-Only 기록)
/// </summary>
/// <param name="FactBullet">1~2문장의 객관적 사실 불릿</param>
/// <param name="Embedding">선택적 시맨틱 검색용 임베딩 벡터 (768차원)</param>
/// <param name="Chapter">사건 발생 챕터 번호 (기본값 1)</param>
public record EventLedgerItem(
    string FactBullet,
    float[]? Embedding = null,
    int Chapter = 1
);

/// <summary>
/// 인물/환경의 상태 및 추적 수치를 표현하는 엔티티 슬롯 항목
/// </summary>
/// <param name="Name">엔티티 명칭 (예: 엘리아스)</param>
/// <param name="ConditionDesc">신체/마력/심리 상태 묘사 (예: 마력 고갈로 쇠약)</param>
/// <param name="StatValue">선택적 추적 수치 (예: 45%)</param>
public record EntitySlotItem(
    string Name,
    string ConditionDesc,
    string StatValue = ""
);

/// <summary>
/// Cognitive Core 3.0 프롬프트 조립을 위한 정형 컨텍스트 DTO
/// </summary>
public record CognitiveContext(
    string MacroChronicle,
    List<EventLedgerItem> LedgerFacts,
    List<EntitySlotItem> Entities,
    List<LorebookEntry> RetrievedLores,
    List<EventLedgerItem> RecalledHistory,
    int CurrentChapter = 1
);
