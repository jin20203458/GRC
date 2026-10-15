using GRC.Models;
using System.Collections.Generic;

namespace GRC.Services;

/// <summary>
/// Cognitive Core 3.0 프롬프트 조립기 인터페이스
/// 100% 불변 동결 SystemInstruction 및 최신 턴 직전 Dynamic Context Grounding 패킹을 담당합니다.
/// </summary>
public interface ICognitivePromptAssembler
{
    /// <summary>
    /// 페르소나, 단기 대화 이력, 최신 유저 메시지, 인지 컨텍스트를 결합하여 최종 GeminiRequest를 생성합니다.
    /// </summary>
    GeminiRequest AssemblePrompt(
        CharacterPreset preset,
        IEnumerable<ChatMessage> shortTermHistory,
        ChatMessage latestUserMessage,
        CognitiveContext cognitiveContext
    );
}
