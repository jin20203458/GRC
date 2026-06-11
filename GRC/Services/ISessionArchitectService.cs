using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GRC.Models;

namespace GRC.Services;

public interface ISessionArchitectService
{
    /// <summary>
    /// 유저 컨셉으로부터 전체 계획(AgentPlan)을 수립합니다.
    /// </summary>
    IAsyncEnumerable<string> GeneratePlanAsync(
        string userConcept, CharacterPreset? existingPreset = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// 계획에 기반하여 각 단계의 컨텐츠를 생성합니다.
    /// </summary>
    IAsyncEnumerable<string> GenerateStepContentAsync(
        AgentStep step, AgentPlan plan, ArchitectSession session,
        string? userFeedback = null, CancellationToken ct = default);
    
    /// <summary>
    /// 유저의 자유 수정 요청을 처리합니다. (이전 생성물 + 피드백 → 재생성)
    /// </summary>
    IAsyncEnumerable<string> ReviseContentAsync(
        AgentStep step, string previousContent, string userFeedback,
        AgentPlan plan, CancellationToken ct = default);
    
    /// <summary>
    /// AI 응답 텍스트로부터 구조화된 데이터를 파싱합니다.
    /// </summary>
    AgentPlan? ParsePlan(string rawResponse);
    List<LorebookEntry>? ParseLorebooks(string rawResponse);
    (Dictionary<string, string> Stats, string Guide)? ParseStatusDesign(string rawResponse);
    
    /// <summary>
    /// 최종 결과물을 세션 파일로 적용합니다.
    /// </summary>
    Task<string> ApplyToNewSessionAsync(ArchitectSession session);
    Task ApplyToExistingSessionAsync(ArchitectSession session);
}
