using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GRC.Helpers;
using GRC.Models;

namespace GRC.Services;

public class SessionArchitectService : ISessionArchitectService
{
    private readonly IGeminiApiService _apiService;
    private readonly ISessionService _sessionService;
    private readonly IPresetStorageService _presetService;

    public SessionArchitectService(
        IGeminiApiService apiService,
        ISessionService sessionService,
        IPresetStorageService presetService)
    {
        _apiService = apiService;
        _sessionService = sessionService;
        _presetService = presetService;
    }

    public async IAsyncEnumerable<string> GeneratePlanAsync(
        string userConcept,
        CharacterPreset? existingPreset = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string systemInstructionText = """
<system_directive>
당신은 세계관 아키텍트이자 숙련된 TRPG/텍스트 RPG 디자이너입니다.
유저가 제시한 컨셉을 바탕으로 텍스트 RPG 세션의 전체 계획(AgentPlan)을 JSON으로 설계하십시오.
기존 프리셋 정보가 주어지면, 기존 설정을 존중하면서 확장하거나 개편하는 계획을 수립하십시오.
</system_directive>

<rules>
- 반드시 아래 JSON 스키마를 엄격히 준수하여 응답하십시오.
- JSON 외의 텍스트를 출력하지 마십시오.
</rules>

<output_format>
{
  "worldviewOutline": "세계관에 대한 1-2줄 핵심 요약 및 테마 정의 (예: 타락한 교회와 마녀사냥이 횡행하는 중세 다크 판타지)",
  "lorebookPlan": [
    {
      "name": "항목 이름 (예: 리리스)",
      "category": "인물, 장소, 아이템, 세계관 중 하나",
      "brief": "이 항목이 다룰 설정에 대한 1줄 요약"
    }
  ],
  "statsPlan": [
    "스탯 이름 1 (예: 타락도)",
    "스탯 이름 2 (예: 신앙심)"
  ],
  "scenarioOutline": "초기 시작 시나리오의 핵심 줄거리 및 유저가 맞닥뜨릴 첫 직면 상황에 대한 1-2줄 요약",
  "promptOutline": "GM(Game Master) 캐릭터의 서술 스타일, 어조, 지켜야 할 규칙 요약"
}
</output_format>
""";

        var systemInstruction = new Content("system", [new Part(systemInstructionText)]);
        
        string userPrompt = $"<user_concept>\n{userConcept}\n</user_concept>";
        if (existingPreset != null)
        {
            userPrompt += $"\n\n<existing_preset>\n" +
                          $"- 이름: {existingPreset.Name}\n" +
                          $"- 세계관 개요: {existingPreset.Worldview.Substring(0, Math.Min(existingPreset.Worldview.Length, 300))}...\n" +
                          $"- 등록된 로어북 항목 수: {existingPreset.Lorebooks?.Count ?? 0}\n" +
                          $"- 스탯 목록: {(existingPreset.CustomStats != null ? string.Join(", ", existingPreset.CustomStats.Keys) : "없음")}\n" +
                          $"</existing_preset>";
        }

        var contents = new List<Content> { new Content("user", [new Part(userPrompt)]) };
        var generationConfig = new GenerationConfig(null, 4096, "application/json");

        var request = new GeminiRequest(systemInstruction, contents, null, generationConfig);

        await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Flash35, ct))
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<string> GenerateStepContentAsync(
        AgentStep step,
        AgentPlan plan,
        ArchitectSession session,
        string? userFeedback = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string systemInstructionText = "";
        string userPrompt = "";
        string responseMimeType = "text/plain";

        switch (step)
        {
            case AgentStep.WorldviewGen:
                systemInstructionText = """
<system_directive>
당신은 밀도 높은 세계관을 작성하는 작가입니다.
제공된 세계관 개요를 바탕으로, 플레이어와 AI가 공유할 깊이 있는 세계관 설정문을 작성하십시오.
</system_directive>

<rules>
- 이 텍스트는 런타임에 AI 시스템 프롬프트의 <master_setting> 태그에 주입됩니다. 소설적 묘사 대신 핵심 정보(배경지식, 역사, 세력, 금기사항 등)를 간결한 레퍼런스 형태로 작성하십시오.
- 반드시 한국어로 작성하십시오.
</rules>

<constraints>
- 분량: 최대 4개 문단 이내
- 서술 방식: 백과사전식 설명, 정보 전달 위주의 명확한 문체
</constraints>
""";
                userPrompt = $"<plan_concept>\n{plan.Concept}\n</plan_concept>\n\n<worldview_outline>\n{plan.WorldviewOutline}\n</worldview_outline>";
                if (!string.IsNullOrEmpty(userFeedback))
                {
                    userPrompt += $"\n\n<user_feedback>\n{userFeedback}\n</user_feedback>";
                }
                break;

            case AgentStep.LorebookGen:
                systemInstructionText = """
<system_directive>
당신은 TRPG 세션의 백과사전(로어북)을 설계하는 데이터 디자이너입니다.
확정된 세계관과 로어북 계획을 바탕으로 게임 내 핵심 로어북 항목들을 생성하십시오.
</system_directive>

<rules>
- 각 항목은 계획의 로어북 목록에 명시된 항목을 기반으로 풍부하게 작성하십시오.
- category: 반드시 '인물', '장소', '아이템', '세계관' 중 하나를 사용하십시오.
- priority: 0=핵심(세계관 전체에 영향), 1=주요(특정 지역/집단에 영향), 2=배경(단순 정보)
- keywords: 텍스트 스캔으로 이 지식을 주입할 단어. 고유명사나 특정 특징적 단어만 사용하고, 범용 단어(예: 마법, 세계, 사람)는 포함하지 마십시오.
- JSON 외의 텍스트를 출력하지 마십시오.
</rules>

<output_format>
[
  {
    "name": "항목 이름 (예: 리리스)",
    "keywords": ["리리스", "타락한 성녀", "성녀"],
    "content": "리리스는 원래 광휘의 신전 소속 성녀였으나... (이 항목의 상세 설정 내용, 최대 2개 문단 이내)",
    "category": "인물",
    "priority": 0
  }
]
</output_format>
""";
                responseMimeType = "application/json";
                
                string loreListJson = JsonSerializer.Serialize(plan.LorebookPlan);
                userPrompt = $"<worldview>\n{session.GeneratedWorldview}\n</worldview>\n\n<lorebook_plan>\n{loreListJson}\n</lorebook_plan>";
                if (!string.IsNullOrEmpty(userFeedback))
                {
                    userPrompt += $"\n\n<user_feedback>\n{userFeedback}\n</user_feedback>";
                }
                break;

            case AgentStep.StatusGen:
                systemInstructionText = """
<system_directive>
당신은 텍스트 RPG의 캐릭터 상태창과 게임 진행 규칙을 설계하는 게임 디자이너입니다.
세계관과 스탯 계획을 바탕으로, 캐릭터 상태창에 표시할 스탯(CustomStats)과 스탯 변동/갱신 지침서(StatusUpdateGuide)를 작성하십시오.
</system_directive>

<rules>
- guide 필드는 번호가 매겨진 리스트 형식으로 작성하십시오.
- 각 스탯의 변동 폭과 한계치(최소/최대값 범위 클램핑)를 명시하십시오.
- 가이드 내 스탯 이름은 stats 객체의 키값과 완벽히 일치해야 합니다.
- JSON 외의 텍스트를 출력하지 마십시오.
</rules>

<output_format>
{
  "stats": {
    "스탯이름1 (예: 타락도)": "0/100 (스탯의 기본 값 및 범위)",
    "스탯이름2 (예: 신앙심)": "50 (기존 수치)"
  },
  "guide": "1. 타락도: 유저가 사악한 행동을 할 때마다 5~10 상승. 범위는 0에서 100을 넘지 않음.\n2. 신앙심: ..."
}
</output_format>
""";
                responseMimeType = "application/json";

                string statsListJson = JsonSerializer.Serialize(plan.StatsPlan);
                userPrompt = $"<worldview>\n{session.GeneratedWorldview}\n</worldview>\n\n<stats_plan>\n{statsListJson}\n</stats_plan>";
                if (!string.IsNullOrEmpty(userFeedback))
                {
                    userPrompt += $"\n\n<user_feedback>\n{userFeedback}\n</user_feedback>";
                }
                break;

            case AgentStep.ScenarioGen:
                systemInstructionText = """
<system_directive>
당신은 스토리텔링 능력이 아주 뛰어난 TRPG 게임 마스터(GM)입니다.
앞서 정의된 세계관, 로어북, 상태창 설정을 완전히 반영하여, 유저가 게임을 시작하자마자 몰입할 수 있는 '초기 상황 시나리오 오프닝'을 작성하십시오.
</system_directive>

<rules>
- 오감 묘사가 생생하게 살아있어야 하며, 현재 주인공이 처한 장소, 분위기, 당장의 물리적 현실을 객관적이고 감각적으로 드러내십시오.
- 순수 서사만 출력하십시오. 스탯 수치, 상태창 마크다운, 메타적 시스템 정보는 포함하지 마십시오.
- 주인공(유저)의 감정이나 행동, 대사를 대신 결정하지 마십시오. 오직 환경과 상황만 제시하십시오.
- 마지막 부분은 주인공이 당장 반응하거나 첫 행동을 결정해야 하는 명확한 '직면 상황'을 남긴 채 서술을 마치십시오.
</rules>

<constraints>
- 분량: 최대 3개 문단 이내
- 출력 언어: 한국어
</constraints>
""";
                string lorebookSummary = session.GeneratedLorebooks != null 
                    ? string.Join(", ", session.GeneratedLorebooks.Select(l => $"{l.Name}({l.Category})"))
                    : "없음";
                string statsSummary = session.GeneratedStats != null
                    ? string.Join(", ", session.GeneratedStats.Select(s => $"{s.Key}: {s.Value}"))
                    : "없음";

                userPrompt = $"""
<worldview>
{session.GeneratedWorldview}
</worldview>

<lorebook_summary>
{lorebookSummary}
</lorebook_summary>

<stats_summary>
{statsSummary}
</stats_summary>

<scenario_outline>
{plan.ScenarioOutline}
</scenario_outline>
""";
                if (!string.IsNullOrEmpty(userFeedback))
                {
                    userPrompt += $"\n\n<user_feedback>\n{userFeedback}\n</user_feedback>";
                }
                break;

            case AgentStep.PromptGen:
                systemInstructionText = """
<system_directive>
당신은 최고의 텍스트 RPG GM 페르소나를 조립하는 프롬프트 엔지니어입니다.
세계관, 시나리오, 상태창을 총망라하여 AI 챗봇이 게임 진행 시 지켜야 할 내부 작동 지시문(System Instruction)을 최종 작성하십시오.
반드시 `<system>` 태그로 전체를 감싸서 출력하십시오.
</system_directive>

<rules>
- 런타임 환경: 세계관은 `<master_setting>`으로 별도 주입되고, 로어북은 동적 자동 주입되며, 상태창 갱신은 별도 API가 처리합니다. 세계관 전체 반복이나 상태창 출력 형식 정의는 포함하지 마십시오. GM의 서술 규칙과 페르소나에 집중하십시오.
- 포함해야 할 핵심 규칙:
  1. [감각적 묘사] 추상적 설명 배제. 오감에 기반한 환경, NPC, 물리적 결과만 객관적으로 서술.
  2. [PC 통제 금지] 유저 캐릭터(PC)의 대사, 행동, 감정, 생각을 임의로 묘사하지 마십시오.
  3. [마이크로 템포] 단일 사건이나 단일 NPC 반응 직후 서술 중단. 유저가 반응해야 할 '직면 상황'에서 턴 종료.
  4. [NPC 자율성] 유저 행동의 성공 여부는 세계관 개연성과 NPC 성향에 따라 GM이 판단.
  5. [상태창 업데이트 지침 준수] 스탯 업데이트 가이드 요약을 프롬프트 하단에 포함. 직접 상태창을 렌더링하라는 지시는 포함하지 마십시오.
</rules>

<constraints>
- 출력 언어: 한국어
- 출력 형식: `<system>` 태그로 감싸진 시스템 지시문 전문
</constraints>
""";
                string worldviewText = session.GeneratedWorldview ?? "";
                string statsText = session.GeneratedStats != null ? string.Join(", ", session.GeneratedStats.Keys) : "";
                string guideText = session.GeneratedStatusGuide ?? "";
                string scenarioText = session.GeneratedScenario ?? "";

                userPrompt = $"""
<worldview>
{worldviewText}
</worldview>

<stats>
{statsText}
</stats>

<status_guide>
{guideText}
</status_guide>

<scenario>
{scenarioText}
</scenario>

<prompt_style_plan>
{plan.PromptOutline}
</prompt_style_plan>
""";
                if (!string.IsNullOrEmpty(userFeedback))
                {
                    userPrompt += $"\n\n<user_feedback>\n{userFeedback}\n</user_feedback>";
                }
                break;
            default:
                yield break;
        }

        var systemInstruction = new Content("system", [new Part(systemInstructionText)]);
        var contents = new List<Content> { new Content("user", [new Part(userPrompt)]) };
        var generationConfig = new GenerationConfig(null, 8192, responseMimeType);

        var request = new GeminiRequest(systemInstruction, contents, null, generationConfig);

        await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Flash35, ct))
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<string> ReviseContentAsync(
        AgentStep step,
        string previousContent,
        string userFeedback,
        AgentPlan plan,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string systemInstructionText = "";
        string responseMimeType = "text/plain";

        switch (step)
        {
            case AgentStep.PlanReview:
                systemInstructionText = """
<system_directive>
당신은 세계관 아키텍트입니다. 유저의 피드백을 반영하여 설계 계획(AgentPlan)을 수정하십시오.
</system_directive>

<rules>
- 이전 계획 JSON 구조를 바탕으로 요청에 따라 필드를 수정한 후 전체 계획을 JSON으로만 응답하십시오.
- JSON 외의 텍스트를 출력하지 마십시오.
</rules>
""";
                responseMimeType = "application/json";
                break;

            case AgentStep.WorldviewReview:
                systemInstructionText = """
<system_directive>
세계관 작가로서 유저의 피드백을 반영하여 세계관 설정문을 수정하십시오.
</system_directive>

<rules>
- 이전 세계관 설정문의 내용을 최대한 보존하면서 유저의 지시를 반영하십시오.
- 자연스러운 한국어 문장으로 세계관 텍스트 전문만 출력하십시오.
</rules>
""";
                break;

            case AgentStep.LorebookReview:
                systemInstructionText = """
<system_directive>
로어북 디자이너로서 유저의 피드백을 반영하여 로어북 항목들을 수정/보완하십시오.
</system_directive>

<rules>
- 이전 로어북 JSON 배열을 수정하거나 항목을 추가/삭제하여 전체 로어북 리스트를 JSON 배열로만 응답하십시오.
- JSON 외의 텍스트를 출력하지 마십시오.
</rules>
""";
                responseMimeType = "application/json";
                break;

            case AgentStep.StatusReview:
                systemInstructionText = """
<system_directive>
게임 디자이너로서 유저의 피드백을 반영하여 스탯 및 갱신 지침서를 수정하십시오.
</system_directive>

<rules>
- 이전 JSON 구조(stats, guide)를 유지하며 변경 사항을 적용하고, JSON으로만 응답하십시오.
- JSON 외의 텍스트를 출력하지 마십시오.
</rules>
""";
                responseMimeType = "application/json";
                break;

            case AgentStep.ScenarioReview:
                systemInstructionText = """
<system_directive>
게임 마스터로서 유저의 피드백을 반영하여 초기 시나리오 오프닝 텍스트를 수정하십시오.
</system_directive>

<rules>
- 유저의 요구 사항을 완벽히 흡수하여 더 몰입감 넘치는 오프닝을 다시 작성하십시오.
- 마지막은 반드시 주인공이 직면한 선택 상황으로 종료해야 합니다.
</rules>
""";
                break;

            case AgentStep.PromptReview:
                systemInstructionText = """
<system_directive>
프롬프트 엔지니어로서 유저의 피드백을 반영하여 AI 작동 지시문(System Instruction)을 수정하십시오.
</system_directive>

<rules>
- 반드시 `<system>` 태그로 전체를 감싸서 출력하십시오.
</rules>
""";
                break;
            default:
                yield break;
        }

        var systemInstruction = new Content("system", [new Part(systemInstructionText)]);
        
        string userPrompt = $"""
<previous_content>
{previousContent}
</previous_content>

<user_feedback>
{userFeedback}
</user_feedback>
""";

        var contents = new List<Content> { new Content("user", [new Part(userPrompt)]) };
        var generationConfig = new GenerationConfig(null, 8192, responseMimeType);

        var request = new GeminiRequest(systemInstruction, contents, null, generationConfig);

        await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Flash35, ct))
        {
            yield return chunk;
        }
    }

    public AgentPlan? ParsePlan(string rawResponse)
    {
        return LlmJsonParser.DeserializeSafe<AgentPlan>(rawResponse);
    }

    public List<LorebookEntry>? ParseLorebooks(string rawResponse)
    {
        return LlmJsonParser.DeserializeArraySafe<List<LorebookEntry>>(rawResponse);
    }

    private class StatusDesignResult
    {
        public Dictionary<string, string>? Stats { get; set; }
        public string? Guide { get; set; }
    }

    public (Dictionary<string, string> Stats, string Guide)? ParseStatusDesign(string rawResponse)
    {
        var result = LlmJsonParser.DeserializeSafe<StatusDesignResult>(rawResponse);
        if (result == null || result.Stats == null) return null;
        return (result.Stats, result.Guide ?? "");
    }

    private class ReviewResponse
    {
        public bool pass { get; set; }
        public string? issues { get; set; }
    }

    public async Task<(bool Pass, string? Feedback)> ReviewStepContentAsync(
        AgentStep reviewStep,
        string generatedContent,
        AgentPlan plan,
        ArchitectSession session,
        CancellationToken ct = default)
    {
        string prompt = "";
        switch (reviewStep)
        {
            case AgentStep.WorldviewReview:
                prompt = $@"<design_plan>
{plan.WorldviewOutline}
</design_plan>

<content_to_review>
{generatedContent}
</content_to_review>

위 세계관 설정이 설계 계획의 '세계관 개요' 테마와 일치하는지, 빈 껍데기만 있는 설명이 아닌지 검수하십시오.";
                break;

            case AgentStep.LorebookReview:
                prompt = $@"<design_plan>
{JsonSerializer.Serialize(plan.LorebookPlan)}
</design_plan>

<content_to_review>
{generatedContent}
</content_to_review>

계획된 로어북 항목들이 누락 없이 구현되었는지, JSON 형식이 유효한 배열인지 검수하십시오.";
                break;

            case AgentStep.StatusReview:
                prompt = $@"<design_plan>
{string.Join(", ", plan.StatsPlan)}
</design_plan>

<content_to_review>
{generatedContent}
</content_to_review>

계획된 스탯 목록이 stats 객체 내에 누락 없이 포함되었는지, JSON 형식이 유효하며 stats와 guide 필드를 갖추고 있는지 검수하십시오.";
                break;

            case AgentStep.ScenarioReview:
                prompt = $@"<design_plan>
{plan.ScenarioOutline}
</design_plan>

<content_to_review>
{generatedContent}
</content_to_review>

세계관과 모순이 없는지, 설계 계획의 시나리오 개요와 부합하는지, 글의 마지막이 주인공이 처한 '직면 상황/선택의 순간'으로 끝나는지 검수하십시오.";
                break;

            case AgentStep.PromptReview:
                prompt = $@"<content_to_review>
{generatedContent}
</content_to_review>

핵심 지시(감각적 묘사, PC 통제 금지, 마이크로 템포, NPC 자율성, 상태창 업데이트 지침 준수)가 포함되어 있는지, 전체가 `<system>` 및 `</system>` 태그로 감싸져 있는지 검수하십시오.";
                break;

            default:
                return (true, null);
        }

        string systemInstruction = @"<system_directive>
당신은 생성된 TRPG 구성 요소 데이터를 치명적 오류나 정합성 측면에서 엄격하게 검증하는 AI 감사관입니다.
</system_directive>

<rules>
- 검증 결과를 분석하여 아래 JSON 스키마로만 응답하십시오.
- JSON 외의 텍스트를 출력하지 마십시오.
</rules>

<output_format>
{
  ""pass"": true 또는 false,
  ""issues"": ""검증 실패 시 지적 사항과 구체적인 피드백 내용 (pass가 true인 경우 null)""
}
</output_format>";

        var si = new Content("system", [new Part(systemInstruction)]);
        var contents = new List<Content> { new Content("user", [new Part(prompt)]) };
        var config = new GenerationConfig(null, 4096, "application/json");

        var request = new GeminiRequest(si, contents, null, config);

        try
        {
            string fullResponse = "";
            await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Flash35, ct))
            {
                fullResponse += chunk;
            }

            var reviewResult = LlmJsonParser.DeserializeSafe<ReviewResponse>(fullResponse);
            if (reviewResult == null)
            {
                return (true, null);
            }

            return (reviewResult.pass, reviewResult.issues);
        }
        catch
        {
            return (true, null);
        }
    }

    public async Task<string> ApplyToNewSessionAsync(ArchitectSession session)
    {
        if (session.Plan == null)
            throw new InvalidOperationException("계획이 존재하지 않아 세션을 적용할 수 없습니다.");

        // 1. 세션명 결정 (세계관 개요나 컨셉에서 추출)
        string sessionName = ExtractSessionName(session.Plan.WorldviewOutline ?? session.Plan.Concept);

        // 2. CharacterPreset 생성
        var preset = new CharacterPreset(
            Name: sessionName,
            Worldview: session.GeneratedWorldview ?? "",
            SystemPrompt: session.GeneratedSystemPrompt ?? "",
            Temperature: null,
            MaxOutputTokens: 8192,
            Lorebooks: session.GeneratedLorebooks ?? new(),
            CustomStats: session.GeneratedStats ?? new(),
            StatusUpdateGuide: session.GeneratedStatusGuide ?? ""
        );

        // 3. 파일 이름 결정
        string fileName = ChatDataHelper.GenerateSessionFileName(preset.Name);

        // 4. 프리셋 저장
        await _presetService.SavePresetAsync(fileName, preset);

        // 5. 초기 세션 구성
        var chapterContext = new ChapterContext
        {
            Plot = "[제 1장] 새로운 이야기가 시작되었습니다.",
            CustomStats = session.GeneratedStats ?? new()
        };

        var history = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(session.GeneratedScenario))
        {
            history.Add(new ChatMessage("user", $"[초기 상황 설정]\n{session.GeneratedScenario}", DateTime.Now));
            history.Add(new ChatMessage("model", "[시스템: 해당 세계관과 초기 상황을 완벽히 인지했습니다. 페르소나를 유지하며 롤플레잉을 대기합니다.]", DateTime.Now.AddSeconds(1)));
        }

        var chatSession = new ChatSession(
            Preset: preset,
            History: history,
            CurrentContext: chapterContext,
            PrevContexts: new List<ChapterContext>(),
            LongTermSummary: "아직 요약된 줄거리가 없습니다.",
            ChapterCount: 1,
            TotalTurnCount: 0,
            MediumTermUpdateCount: 0
        );

        // 6. 세션 데이터 저장
        await _sessionService.SaveSessionAsync(fileName, chatSession);

        // 7. FullHistory 로그 초기화
        if (!string.IsNullOrWhiteSpace(session.GeneratedScenario))
        {
            await FullHistoryLogger.LogMessageAsync(fileName,
                new ChatMessage("user", $"[초기 상황]\n{session.GeneratedScenario}", DateTime.Now));
        }

        return fileName;
    }

    public async Task ApplyToExistingSessionAsync(ArchitectSession session)
    {
        if (string.IsNullOrEmpty(session.ExistingSessionFileName) || session.ExistingPreset == null)
            throw new InvalidOperationException("기존 세션 정보가 없어 적용할 수 없습니다.");

        string fileName = session.ExistingSessionFileName;

        // 1. 프리셋 업데이트
        var updatedPreset = session.ExistingPreset with
        {
            Worldview = session.GeneratedWorldview ?? session.ExistingPreset.Worldview,
            SystemPrompt = session.GeneratedSystemPrompt ?? session.ExistingPreset.SystemPrompt,
            Lorebooks = session.GeneratedLorebooks ?? session.ExistingPreset.Lorebooks,
            CustomStats = session.GeneratedStats ?? session.ExistingPreset.CustomStats,
            StatusUpdateGuide = session.GeneratedStatusGuide ?? session.ExistingPreset.StatusUpdateGuide
        };

        // 2. 프리셋 저장
        await _presetService.SavePresetAsync(fileName, updatedPreset);

        // 3. 기존 세션 파일 로드 및 업데이트
        var existingSession = await _sessionService.LoadSessionAsync(fileName);
        if (existingSession != null)
        {
            var currentContext = existingSession.CurrentContext;
            if (session.GeneratedStats != null)
            {
                // 기존 스탯에 추가된 스탯 병합
                foreach (var key in session.GeneratedStats.Keys)
                {
                    if (!currentContext.CustomStats.ContainsKey(key))
                    {
                        currentContext.CustomStats[key] = session.GeneratedStats[key];
                    }
                }
            }

            // 시나리오 내용이 재생성되었고 기존 대화가 없다면(즉 생성하자마자 첫 턴 전 상태) 시나리오도 갱신
            var history = new List<ChatMessage>(existingSession.History);
            string plot = currentContext.Plot;
            if (history.Count <= 1 && !string.IsNullOrWhiteSpace(session.GeneratedScenario))
            {
                history.Clear();
                history.Add(new ChatMessage("user", $"[초기 상황]\n{session.GeneratedScenario}", DateTime.Now));
                plot = session.GeneratedScenario;

                await FullHistoryLogger.ClearHistoryAsync(fileName);
                await FullHistoryLogger.LogMessageAsync(fileName,
                    new ChatMessage("user", $"[초기 상황]\n{session.GeneratedScenario}", DateTime.Now));
            }

            var updatedSession = existingSession with
            {
                Preset = updatedPreset,
                History = history,
                CurrentContext = new ChapterContext
                {
                    Plot = plot,
                    CustomStats = currentContext.CustomStats,
                    Chars = currentContext.Chars,
                    Items = currentContext.Items,
                    Places = currentContext.Places,
                    TriggeredMetaEvents = currentContext.TriggeredMetaEvents
                }
            };

            await _sessionService.SaveSessionAsync(fileName, updatedSession);
        }
    }

    private string ExtractSessionName(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "AI 생성 세션";
        
        // 쌍따옴표 내의 텍스트가 있으면 그것을 제목으로 추출 시도 (예: "멸망의 성녀 아르카디아" -> 멸망의 성녀 아르카디아)
        int quoteStart = text.IndexOf('"');
        if (quoteStart >= 0)
        {
            int quoteEnd = text.IndexOf('"', quoteStart + 1);
            if (quoteEnd > quoteStart)
            {
                string title = text.Substring(quoteStart + 1, quoteEnd - quoteStart - 1).Trim();
                if (!string.IsNullOrEmpty(title)) return title;
            }
        }

        // 따옴표가 없다면 적당히 15자 내외로 자름
        string clean = text.Replace("\r", "").Replace("\n", " ").Trim();
        if (clean.Length > 20)
        {
            return clean.Substring(0, 17) + "...";
        }
        return clean;
    }
}
