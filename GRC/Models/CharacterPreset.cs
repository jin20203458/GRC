namespace GRC.Models;

/// <summary>
/// AI 챗봇의 페르소나 및 세팅 값을 정의하는 객체
/// </summary>
public record CharacterPreset(
    string Name,                 // 세션명
    string Worldview,            // 절대 압축되거나 변경되지 않는 불변의 법칙(세계관)
    string SystemPrompt,         // 성격, 말투 및 페르소나 지시문
    float? Temperature = null,    // 창의성 수치 (null 일 경우 API 기본값 사용)
    int MaxOutputTokens = 4096,   // 한 번에 출력할 최대 토큰 수
    List<LorebookEntry>? Lorebooks = null,
    Dictionary<string, string>? CustomStats = null,
    string StatusUpdateGuide = ""
);