using System.Collections.Generic;

namespace GRC.Models;

/// <summary>
/// 현재 진행 중인 전체 대화 세션 정보
/// </summary>
public record ChatSession(
    CharacterPreset Preset,
    List<ChatMessage> History,             // 단기 기억 (최대 20턴)
    ChapterContext CurrentContext,         // 현재 챕터 상태 (중기 기억)
    string LongTermSummary,                // 장기 기억 (전체 연대기 줄거리)
    int ChapterCount = 1,
    int TotalTurnCount = 0,
    int MediumTermUpdateCount = 0
);