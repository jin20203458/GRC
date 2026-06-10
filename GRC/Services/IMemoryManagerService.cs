using GRC.Models;
using System.Threading.Tasks;

namespace GRC.Services;

public interface IMemoryManagerService
{
    /// <summary>
    /// 현재 유저 메시지와 프리셋, 그리고 기억된 문맥을 조합해 최종 API 요청 객체를 생성합니다.
    /// </summary>
    Task<GeminiRequest> BuildRequestAsync(ChatMessage userMessage, CharacterPreset preset, string? metaDirective = null);

    /// <summary>
    /// 모델의 응답을 단기 기억에 추가합니다.
    /// </summary>
    void AddModelResponse(ChatMessage message);

    // 이번 턴에 고품질 닻 내리기(PRO 모델)가 필요한지 여부를 나타내는 플래그
    bool ConsumeAnchorFlag();
    bool ConsumeChapterChangedFlag();

    /// <summary>
    /// 전체 대화 내역과 요약을 초기화합니다.
    /// </summary>
    /// 
    void DeleteMessage(ChatMessage message);

    void Clear();

    void RestoreSession(ChatSession session);

    void InjectInitialScenario(string scenarioText);

    ChatSession ExportSession(CharacterPreset currentPreset);

    void UpdateContextStatus(StatusPayload payload);
    ChapterContext CurrentContext { get; }
    string CurrentLorebookText { get; }

}