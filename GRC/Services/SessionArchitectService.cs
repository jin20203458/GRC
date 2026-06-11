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
당신은 세계관 아키텍트이자 숙련된 TRPG/텍스트 RPG 디자이너입니다.
유저가 제시한 원본 컨셉을 바탕으로, 훌륭한 텍스트 RPG 세션을 만들기 위한 전체 계획(AgentPlan)을 JSON 형태로 설계해 주세요.
만약 기존 프리셋 정보가 주어지면, 기존 설정을 존중하면서 이를 확장하거나 개편하는 계획을 수립해야 합니다.
반드시 JSON 규격을 엄격히 지켜 대답하세요. JSON 외의 다른 텍스트는 절대 포함하지 마십시오.

출력 JSON 스키마:
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
""";

        var systemInstruction = new Content("system", [new Part(systemInstructionText)]);
        
        string userPrompt = $"유저 컨셉: {userConcept}";
        if (existingPreset != null)
        {
            userPrompt += $"\n\n[기존 세션 설정 참고]\n" +
                          $"- 이름: {existingPreset.Name}\n" +
                          $"- 세계관 개요: {existingPreset.Worldview.Substring(0, Math.Min(existingPreset.Worldview.Length, 300))}...\n" +
                          $"- 등록된 로어북 항목 수: {existingPreset.Lorebooks?.Count ?? 0}\n" +
                          $"- 스탯 목록: {(existingPreset.CustomStats != null ? string.Join(", ", existingPreset.CustomStats.Keys) : "없음")}";
        }

        var contents = new List<Content> { new Content("user", [new Part(userPrompt)]) };
        var generationConfig = new GenerationConfig(1.0f, 4096, "application/json");

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
당신은 밀도 높은 세계관을 작성하는 작가입니다.
제공된 전체 계획의 세계관 개요를 바탕으로, 플레이어와 AI가 공유할 깊이 있고 매혹적인 세계관 설정문을 작성하세요.
- 분량: 1000자 ~ 1500자 내외
- 서술 방식: 백과사전식 설명과 분위기를 느낄 수 있는 서술적 문체가 조화롭게 섞여 있어야 합니다.
- 주의: 게임 규칙보다는 이 세계의 고유한 배경지식, 역사, 세력, 금기사항 등에 집중하세요.
- 언어: 반드시 한국어로 작성하십시오.
""";
                userPrompt = $"컨셉: {plan.Concept}\n세계관 개요: {plan.WorldviewOutline}";
                if (!string.IsNullOrEmpty(userFeedback))
                {
                    userPrompt += $"\n\n유저 피드백 및 요구사항: {userFeedback}";
                }
                break;

            case AgentStep.LorebookGen:
                systemInstructionText = """
당신은 TRPG 세션의 백과사전(로어북)을 설계하는 데이터 디자이너입니다.
이전 단계에서 확정된 세계관과 계획된 로어북 목록을 바탕으로, 게임 내에서 핵심적으로 참조될 로어북 항목들을 생성하세요.
반드시 JSON 배열 형식으로만 응답해야 합니다. JSON 외의 어떠한 텍스트나 설명도 절대 붙이지 마십시오.

출력 JSON 스키마:
[
  {
    "name": "항목 이름 (예: 리리스)",
    "keywords": ["리리스", "타락한 성녀", "성녀"],
    "content": "리리스는 원래 광휘의 신전 소속 성녀였으나... (이 항목의 상세 설정 내용, 200~400자)",
    "category": "인물",
    "priority": 0
  }
]
- 각 항목은 계획의 로어북 목록에 명시된 항목들을 기반으로 풍부하게 작성하세요.
- category는 반드시 '인물', '장소', '아이템', '세계관' 중 하나여야 합니다.
- keywords는 텍스트 내에서 스캔하여 이 지식을 주입할 단어들이며, 해당 명칭의 변형이나 동의어를 2-3개 포함해야 합니다.
""";
                responseMimeType = "application/json";
                
                string loreListJson = JsonSerializer.Serialize(plan.LorebookPlan);
                userPrompt = $"세계관:\n{session.GeneratedWorldview}\n\n로어북 계획 리스트:\n{loreListJson}";
                if (!string.IsNullOrEmpty(userFeedback))
                {
                    userPrompt += $"\n\n유저 피드백 및 요구사항: {userFeedback}";
                }
                break;

            case AgentStep.StatusGen:
                systemInstructionText = """
당신은 텍스트 RPG의 캐릭터 상태창과 게임 진행 규칙을 설계하는 게임 디자이너입니다.
세계관과 계획된 스탯 목록을 바탕으로, 캐릭터의 상태창에 표시할 스탯(CustomStats)과 해당 스탯들이 대화 진행에 따라 어떻게 변동하고 갱신되어야 하는지 안내하는 '상태창 갱신 지침서(StatusUpdateGuide)'를 작성하세요.
반드시 JSON 형식으로만 응답해야 합니다. JSON 외의 어떠한 텍스트나 설명도 절대 붙이지 마십시오.

출력 JSON 스키마:
{
  "stats": {
    "스탯이름1 (예: 타락도)": "0/100 (스탯의 기본 값 및 범위)",
    "스탯이름2 (예: 신앙심)": "50 (기존 수치)"
  },
  "guide": "이 세션에서 스탯이 갱신되는 구체적인 규칙과 지침을 적으세요. 예: 유저가 사악한 행동을 할 때마다 타락도가 5~10 상승합니다. 호감도는 NPC와의 대화에 따라 수시로 변동합니다."
}
""";
                responseMimeType = "application/json";

                string statsListJson = JsonSerializer.Serialize(plan.StatsPlan);
                userPrompt = $"세계관:\n{session.GeneratedWorldview}\n\n스탯 계획 리스트:\n{statsListJson}";
                if (!string.IsNullOrEmpty(userFeedback))
                {
                    userPrompt += $"\n\n유저 피드백 및 요구사항: {userFeedback}";
                }
                break;

            case AgentStep.ScenarioGen:
                systemInstructionText = """
당신은 스토리텔링 능력이 아주 뛰어난 TRPG 게임 마스터(GM)입니다.
앞서 정의된 세계관, 로어북, 상태창 설정을 완전히 반영하여, 유저가 게임을 시작하자마자 몰입할 수 있는 '초기 상황 시나리오 오프닝'을 작성하세요.
- 분량: 600~1000자 내외
- 서술 특징: 오감 묘사가 생생하게 살아있어야 하며, 현재 주인공이 처한 장소, 분위기, 당장의 물리적 현실을 객관적이고 감각적으로 드러내세요.
- 주의: 주인공(유저)의 감정이나 행동, 대사를 대신 결정하지 마십시오. 오직 환경과 상황만 제시해야 합니다.
- 마지막 부분: 주인공이 당장 반응하거나 첫 행동을 결정해야만 하는 명확한 '직면 상황'을 남긴 채 서술을 마쳐야 합니다. (예: '...당신은 이 어둠 속에서 어느 길로 향하겠습니까?', '...눈앞의 기사가 검을 뽑아 들었습니다. 어떻게 대응하겠습니까?')
""";
                string lorebookSummary = session.GeneratedLorebooks != null 
                    ? string.Join(", ", session.GeneratedLorebooks.Select(l => $"{l.Name}({l.Category})"))
                    : "없음";
                string statsSummary = session.GeneratedStats != null
                    ? string.Join(", ", session.GeneratedStats.Select(s => $"{s.Key}: {s.Value}"))
                    : "없음";

                userPrompt = $"""
세계관:
{session.GeneratedWorldview}

등록된 로어북 요약:
{lorebookSummary}

캐릭터 스탯 설정:
{statsSummary}

시나리오 요약 계획:
{plan.ScenarioOutline}
""";
                if (!string.IsNullOrEmpty(userFeedback))
                {
                    userPrompt += $"\n\n유저 피드백 및 요구사항: {userFeedback}";
                }
                break;

            case AgentStep.PromptGen:
                systemInstructionText = """
당신은 최고의 텍스트 RPG GM 페르소나를 조립하는 엔지니어입니다.
앞서 작성된 세계관, 시나리오, 상태창을 총망라하여, AI 챗봇이 게임을 진행할 때 지켜야 할 내부 작동 지시문(System Instruction / System Prompt)을 최종 작성하세요.
- 이 프롬프트는 AI에게 주입되어 GM으로서의 페르소나와 규칙을 규정하게 됩니다.
- 반드시 `<system>` 태그로 전체를 감싸서 출력하세요.
- 포함해야 할 핵심 규칙:
  1. [감각적 묘사] 추상적 설명 배제. 오감(시/청/후각 등)에 기반한 환경, NPC, 물리적 결과만 객관적으로 서술.
  2. [PC 통제 금지] 유저 캐릭터(PC)의 대사, 행동, 감정, 생각은 절대 임의로 묘사하지 말 것.
  3. [마이크로 템포] 단일 사건이나 단일 NPC 반응 직후 즉시 서술 중단. 유저가 반응해야 할 '직면 상황'에서 턴 종료.
  4. [NPC 자율성] 유저 행동의 성공 여부는 세계관 개연성과 NPC 성향에 따라 GM이 판단할 것.
  5. [상태창 업데이트 지침 준수] 앞서 정의된 스탯 업데이트 가이드를 요약하여 프롬프트 하단에 포함할 것.
""";
                string worldviewText = session.GeneratedWorldview ?? "";
                string statsText = session.GeneratedStats != null ? string.Join(", ", session.GeneratedStats.Keys) : "";
                string guideText = session.GeneratedStatusGuide ?? "";
                string scenarioText = session.GeneratedScenario ?? "";

                userPrompt = $"""
세계관:
{worldviewText}

캐릭터 스탯:
{statsText}

스탯 업데이트 가이드:
{guideText}

초기 오프닝 시나리오:
{scenarioText}

프롬프트 스타일 계획:
{plan.PromptOutline}
""";
                if (!string.IsNullOrEmpty(userFeedback))
                {
                    userPrompt += $"\n\n유저 피드백 및 요구사항: {userFeedback}";
                }
                break;
            default:
                yield break;
        }

        var systemInstruction = new Content("system", [new Part(systemInstructionText)]);
        var contents = new List<Content> { new Content("user", [new Part(userPrompt)]) };
        var generationConfig = new GenerationConfig(1.0f, 8192, responseMimeType);

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
당신은 세계관 아키텍트입니다. 유저의 피드백을 반영하여 설계 계획(AgentPlan)을 수정하세요.
반드시 이전 계획 JSON 구조를 바탕으로 요청에 따라 필드를 수정한 후 전체 계획을 다시 JSON 형식으로만 답변하십시오. JSON 외 다른 말은 금지합니다.
""";
                responseMimeType = "application/json";
                break;

            case AgentStep.WorldviewReview:
                systemInstructionText = """
세계관 작가로서 유저의 피드백을 반영하여 세계관 설정문을 수정해 주세요.
이전 세계관 설정문의 내용을 최대한 보존하면서 유저의 지시를 반영하고, 자연스러운 한국어 문장으로 세계관 텍스트 전문만 출력하세요.
""";
                break;

            case AgentStep.LorebookReview:
                systemInstructionText = """
로어북 디자이너로서 유저의 피드백을 반영하여 로어북 항목들을 수정/보완해 주세요.
이전 로어북 JSON 배열을 수정하거나 항목을 추가/삭제하여 전체 로어북 리스트를 JSON 배열 형식으로만 응답해야 합니다. JSON 외의 말은 절대 금지합니다.
""";
                responseMimeType = "application/json";
                break;

            case AgentStep.StatusReview:
                systemInstructionText = """
게임 디자이너로서 유저의 피드백을 반영하여 스탯 및 갱신 지침서를 수정해 주세요.
이전 JSON 구조(stats, guide)를 유지하며 변경 사항을 적용하고, 결과물을 JSON 형식으로만 응답해야 합니다. JSON 외의 말은 절대 금지합니다.
""";
                responseMimeType = "application/json";
                break;

            case AgentStep.ScenarioReview:
                systemInstructionText = """
게임 마스터로서 유저의 피드백을 반영하여 초기 시나리오 오프닝 텍스트를 수정해 주세요.
유저의 요구 사항을 완벽히 흡수하여 더 몰입감 넘치는 오프닝을 다시 작성하세요. 마지막은 반드시 주인공이 직면한 선택 상황으로 끝나야 합니다.
""";
                break;

            case AgentStep.PromptReview:
                systemInstructionText = """
프롬프트 엔지니어로서 유저의 피드백을 반영하여 AI 작동 지시문(System Instruction)을 수정해 주세요.
반드시 `<system>` 태그로 전체를 감싸서 대답하십시오.
""";
                break;
            default:
                yield break;
        }

        var systemInstruction = new Content("system", [new Part(systemInstructionText)]);
        
        string userPrompt = $"""
[이전 생성 내용]
{previousContent}

[유저 수정 요청 사항]
{userFeedback}
""";

        var contents = new List<Content> { new Content("user", [new Part(userPrompt)]) };
        var generationConfig = new GenerationConfig(1.0f, 8192, responseMimeType);

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
            Temperature: 1.0f,
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
            history.Add(new ChatMessage("user", $"[초기 상황]\n{session.GeneratedScenario}", DateTime.Now));
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
