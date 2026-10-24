using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    private readonly ILorebookService _lorebookService;

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// AI_Prompt_Engineering_Guidelines 원칙 8: gemini-4-argon (ThinkingLevel.high) 내부 추론 토큰 차감을 고려한 32,768 출력 토큰 헤드룸
    /// </summary>
    public const int ArchitectMaxOutputTokens = 32768;

    // AI_Prompt_Engineering_Guidelines 원칙 3 (2계층 API 물리적 스키마 계약)
    private static readonly object AgentPlanResponseSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            sessionTitle = new { type = "STRING" },
            surfaceTone = new { type = "STRING" },
            coreWeight = new { type = "STRING" },
            pcInitialPosition = new { type = "STRING" },
            worldviewOutline = new { type = "STRING" },
            lorebookPlan = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        name = new { type = "STRING" },
                        category = new { type = "STRING" },
                        brief = new { type = "STRING" },
                        surfaceRole = new { type = "STRING" },
                        innerDepth = new { type = "STRING" },
                        connectedEntities = new { type = "STRING" },
                        keywords = new { type = "ARRAY", items = new { type = "STRING" } },
                        priority = new { type = "INTEGER" }
                    },
                    required = new[] { "name", "category", "brief", "surfaceRole", "innerDepth", "connectedEntities", "keywords", "priority" }
                }
            },
            statsPlan = new { type = "ARRAY", items = new { type = "STRING" } },
            scenarioOutline = new { type = "STRING" },
            promptOutline = new { type = "STRING" }
        },
        required = new[]
        {
            "sessionTitle", "surfaceTone", "coreWeight", "pcInitialPosition",
            "worldviewOutline", "lorebookPlan", "statsPlan", "scenarioOutline", "promptOutline"
        }
    };

    private static readonly object LorebookArrayResponseSchema = new
    {
        type = "ARRAY",
        items = new
        {
            type = "OBJECT",
            properties = new
            {
                name = new { type = "STRING" },
                keywords = new { type = "ARRAY", items = new { type = "STRING" } },
                content = new { type = "STRING" },
                category = new { type = "STRING" },
                priority = new { type = "INTEGER" }
            },
            required = new[] { "name", "keywords", "content", "category", "priority" }
        }
    };

    private static readonly object StatusDesignResponseSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            stats = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        name = new { type = "STRING" },
                        value = new { type = "STRING" }
                    },
                    required = new[] { "name", "value" }
                }
            },
            guide = new { type = "STRING" },
            initialChars = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        name = new { type = "STRING" },
                        value = new { type = "STRING" }
                    },
                    required = new[] { "name", "value" }
                }
            },
            initialPlaces = new { type = "ARRAY", items = new { type = "STRING" } },
            initialItems = new { type = "ARRAY", items = new { type = "STRING" } },
            genreDirectives = new { type = "ARRAY", items = new { type = "STRING" } }
        },
        required = new[] { "stats", "guide", "initialChars", "initialPlaces", "initialItems", "genreDirectives" }
    };

    /// <summary>
    /// AI_Prompt_Engineering_Guidelines.md 원칙 1, 2, 16을 코드 레벨에서 100% 보장하는 불변 상단 헌법 (순수 XML + 직결 명령문 체계)
    /// </summary>
    public const string CoreSystemDirectivesHeader = """
<system_directive>
Role: 3인칭 전지적 게임 마스터
</system_directive>

<rules>
1. 추상적 감정 라벨링, 시스템 수치 언급, 문단 끝의 교훈적 요약을 배제하고, 오감(시·청·후각 등)에 기반한 물리적 행동, 시선, 어조, 환경 변화로만 객관적 서술.
2. 유저 캐릭터(PC)의 대사, 행동, 내면의 생각·감정을 임의로 대신 서술하지 말고, PC의 선택이나 대응이 필요한 시점에서 서술을 멈추고 턴을 넘길 것.
3. 작위적인 주인공 보정(무조건적 호의·기적적 성공)과 억지 실패 보정을 모두 배제하고, 세계관 법칙 및 당사자 간의 실제 역량·무력·상황 격차와 인물 가치관에 부합하는 현실적 인과율로 판정할 것.
""";

    public SessionArchitectService(
        IGeminiApiService apiService,
        ISessionService sessionService,
        IPresetStorageService presetService,
        ILorebookService lorebookService)
    {
        _apiService = apiService;
        _sessionService = sessionService;
        _presetService = presetService;
        _lorebookService = lorebookService;
    }

    public async IAsyncEnumerable<string> GeneratePlanAsync(
        string userConcept,
        CharacterPreset? existingPreset = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string systemInstructionText = """
<system_directive>
Domain: Narrative Session Architecture & Worldbuilding Blueprint Engine
Task: <user_concept>을 분석하여 마스터 세계관 4대 섹션(worldviewOutline)과 그물망(Mesh) 로어북 기획이 하나로 맞물리는 롤플레잉 세션 설계 청사진(AgentPlan)을 JSON으로 수립하십시오.
Output Language: Korean
</system_directive>

<rules>
1. surfaceTone은 평소 주변 환경의 분위기와 NPC들의 일상 대화 템포이며, coreWeight는 세계관 기저의 핵심 갈등과 위기 시 적대 세력·환경의 압박감입니다. 두 항목 모두 세계와 NPC들의 연출 기준이어야 하며, 주인공(PC)의 개인적 성격, 유머 감각, 감정 상태를 대신 규정해서는 안 됩니다.
2. 미래에 일어날 줄거리나 결말을 미리 정해두는 대신, Turn 0 시점의 세계 법칙, 세력 구도, 주인공(PC)의 시작 객관적 신분·위치·제약(pcInitialPosition), 인물별 입체적 동기와 상호 관계망만 정의하십시오. PC의 대사, 내면 심리, 선택, 태도를 미리 단정 짓지 마십시오.
3. worldviewOutline 필드는 런타임 <master_setting> 슬롯에 그대로 주입될 마스터 세계관 레퍼런스이므로, 번호 섹션 헤더(1~4)와 각 하위 하이픈 불릿(`- 항목명: 내용` 3~4개)마다 반드시 줄바꿈(\n)으로 구분된 명사구 중심 개조식(Telegraphic Compression)으로 직접 작성하십시오(서술어 어미 `~이다`, `~한다` 배제):
   1. 배경 및 세계의 핵심 법칙
   2. 세력 구도 및 사회적 현실
   3. 주인공(PC)의 초기 입지 및 직면 환경 (PC의 객관적 위장 신분, 물리적 제약/패널티, 시작 시점의 당면 과제만 객관적으로 서술하며, PC의 성격이나 감정 표현을 단정하지 말 것)
   4. 서사 톤앤매너 및 감각적 디테일 (공간의 공기, 날씨, 냄새, 소리 등 오감 환경 묘사 기준 및 위기 시 적대 NPC들의 압박감 묘사 기준만 명시하며, PC가 살기를 뿜거나 특정 행동을 취한다고 서술하는 것 금지)
4. 로어북(lorebookPlan)은 컨셉의 스케일에 맞춰 주요 인물(4~6명), 구체적 단일 장소(2~4곳), 핵심 단일 아이템/무구(1~3개), 고유 세부 조직/사건(1~2개) 등 총 8~15개 항목으로 기획하되, 아래 원칙을 엄격히 준수하십시오:
   - 역할 분리(중복 배제): 거시적 세계 법칙과 사회적 현실은 이미 위 worldviewOutline(<master_setting>)에서 상시 제공하므로, lorebookPlan에 이를 단순 반복하는 중복 '세계관' 항목을 작성하지 마십시오. lorebookPlan은 서사 진행 중 조건부로 인출되어야 할 구체적 개체들만을 전담합니다.
   - 단일 개체 원칙: 각 로어북의 name은 반드시 단 하나의 고유 개체명만 지정해야 하며, 'A와 B'처럼 2개 이상의 개체를 하나의 항목으로 묶는 복합 명칭을 엄격히 금지합니다.
5. 각 로어북 항목은 고립된 부품이 되지 않도록 connectedEntities 필드에 다른 로어북 항목(인물·장소·세력·아이템)들과의 구체적 연결고리(소속, 라이벌/적대, 공유한 과거, 감시, 거래, 오프닝 사건 개입 방식)를 명시하여 인물↔인물↔세력↔장소가 유기적으로 얽히게 설계하십시오.
6. priority는 오름차순 우선순위(0이 최우선) 규격에 따라 0(핵심 주연/절대 법칙), 1(주요 조연/시작 거점/주요 세력), 2(일반 장소/세력), 3(배경/보조 아이템)으로 부여하고, keywords는 조사가 제거된 순수 고유명사·직함·별칭 4~6개로 구성하십시오.
7. <output_format>의 TypeScript 인터페이스 규격에 부합하는 순수 JSON 객체만 출력하십시오.
</rules>

<output_format>
interface LorebookPlanItem {
  name: string;              // 항목 고유 명칭 (인물명, 장소명 등)
  category: '인물' | '장소' | '아이템' | '세계관';
  brief: string;             // 핵심 정의 1줄 요약
  surfaceRole: string;       // 외양, 표면적 위상 및 평소 대외적 태도
  innerDepth: string;        // 내면의 속마음, 결핍, 진짜 동기, 갭 모에 또는 서사적 훅
  connectedEntities: string; // 다른 로어북 항목들과의 구체적 관계망, 대립각, 소속 및 인과적 연결고리
  keywords: string[];        // 검색 트리거용 순수 명사 키워드 (4~6개)
  priority: number;          // 0(최우선 핵심) | 1(주요) | 2(일반) | 3(배경)
}

interface AgentPlan {
  sessionTitle: string;             // 세션 고유 제목
  surfaceTone: string;              // 평소 주변 환경과 NPC들의 일상 대화 분위기 정의 (PC 성격/태도 규정 금지)
  coreWeight: string;               // 세계관 기저 갈등과 위기 시 환경적 압박감 정의 (PC 행동/감정 규정 금지)
  pcInitialPosition: string;        // 주인공(PC)의 시작 객관적 신분, 위치, 당면 과제, 물리적 제약
  worldviewOutline: string;         // 4대 번호 섹션(1~4)과 하이픈 불릿(- 항목: 내용)으로 작성된 마스터 세계관 본문
  lorebookPlan: LorebookPlanItem[]; // 8~15개 그물망 로어북 기획 항목 배열
  statsPlan: string[];              // 핵심 추적 스탯 또는 주요 인물 관계도 명칭 (4~6개)
  scenarioOutline: string;          // 오프닝 장면의 장소, 등장인물, 갈등 점화 및 주인공의 첫 선택 상황
  promptOutline: string;            // 장르적 문체, 환경 감각 묘사 기준, NPC 대화 템포 연출 지침
}
</output_format>
""";

        string existingPresetBlock = "";
        if (existingPreset != null)
        {
            string worldviewPreview = existingPreset.Worldview.Substring(0, Math.Min(existingPreset.Worldview.Length, 500));
            int loreCount = existingPreset.Lorebooks?.Count ?? 0;
            string statKeys = existingPreset.CustomStats != null ? string.Join(", ", existingPreset.CustomStats.Keys) : "없음";
            existingPresetBlock = $$"""

<existing_preset>
- 이름: {{existingPreset.Name}}
- 세계관 개요: {{worldviewPreview}}
- 등록된 로어북 항목 수: {{loreCount}}
- 스탯 목록: {{statKeys}}
</existing_preset>
""";
        }

        string userPrompt = $$"""
<user_concept>
{{userConcept}}
</user_concept>{{existingPresetBlock}}

<final_instruction>
위 <user_concept>을 바탕으로 4대 섹션 개조식 마스터 세계관(worldviewOutline)과 그물망 로어북 기획(lorebookPlan)이 통합된 <output_format>의 AgentPlan JSON을 출력하십시오.
</final_instruction>
""";

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(systemInstructionText)]),
            Contents: [new Content("user", [new Part(userPrompt)])],
            SafetySettings: null,
            GenerationConfig: new GenerationConfig(null, ArchitectMaxOutputTokens, "application/json", AgentPlanResponseSchema, new ThinkingConfig(ThinkingLevel.high))
        );

        await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Pro, ct))
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
        // Stage 2 (LorebookGen): 2-Wave 통합 앙상블 인과 생성 (Wave 1: 배경/장소/아이템 통합 1회 -> Wave 2: 인물 전원 통합 1회)
        if (step == AgentStep.LorebookGen && string.IsNullOrWhiteSpace(userFeedback) && plan.LorebookPlan.Count > 0)
        {
            string worldviewContext = !string.IsNullOrWhiteSpace(session.GeneratedWorldview)
                ? session.GeneratedWorldview
                : SanitizeWorldview(plan.WorldviewOutline);
            string mergedLorebooksJson = await GenerateLorebooksCausallyAsync(plan, worldviewContext, ct);
            yield return mergedLorebooksJson;
            yield break;
        }

        // Legacy/Direct Stage (PromptGen): 코드 고정 헌법(Core Directives) + 전수 컨텍스트 기반 Pro 장르 디렉팅 결합
        if (step == AgentStep.PromptGen)
        {
            string genreDirectives = await GenerateGenreDirectivesAsync(plan, session, userFeedback, ct);
            string compiledSystemPrompt = CompileHybridSystemPrompt(genreDirectives, plan);
            yield return compiledSystemPrompt;
            yield break;
        }

        var (systemInstructionText, userPrompt, responseMimeType, responseSchema) = BuildStepPrompt(step, plan, session, userFeedback);
        if (string.IsNullOrWhiteSpace(systemInstructionText))
        {
            yield break;
        }

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(systemInstructionText)]),
            Contents: [new Content("user", [new Part(userPrompt)])],
            SafetySettings: null,
            GenerationConfig: new GenerationConfig(null, ArchitectMaxOutputTokens, responseMimeType, responseSchema, new ThinkingConfig(ThinkingLevel.high))
        );

        var rawBuilder = new StringBuilder();
        await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Pro, ct))
        {
            rawBuilder.Append(chunk);
        }

        string rawResult = rawBuilder.ToString();
        string sanitizedResult = step switch
        {
            AgentStep.WorldviewGen => SanitizeWorldview(rawResult),
            AgentStep.ScenarioGen => SanitizeScenario(rawResult),
            _ => rawResult
        };

        yield return sanitizedResult;
    }

    private (string SystemInstruction, string UserPrompt, string MimeType, object? Schema) BuildStepPrompt(
        AgentStep step,
        AgentPlan plan,
        ArchitectSession session,
        string? userFeedback)
    {
        string feedbackBlock = !string.IsNullOrWhiteSpace(userFeedback)
            ? $"\n\n<user_feedback>\n{userFeedback}\n</user_feedback>"
            : "";

        switch (step)
        {
            case AgentStep.WorldviewGen:
            {
                string sys = """
<system_directive>
Domain: Worldbuilding Reference Specification Engine
Task: 기획안(AgentPlan)과 전체 로어북 연결망을 바탕으로 런타임 <master_setting> 슬롯에 주입될 마스터 세계관 레퍼런스를 작성하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 전체 텍스트는 번호 섹션 헤더(`1. 섹션명`)와 하이픈 개조식 불릿(`- 항목명: 내용`)으로만 구성하고, 서술어 어미(`~이다`, `~한다`)를 덜어내어 명사구와 핵심 키워드 중심의 1줄 개조식(Telegraphic Compression)으로 작성하십시오.
2. 개별 등장인물의 상세 프로필은 별도 로어북(`<world_lore>`)에서 전담하므로, 본 문서에는 아래 4대 거시 섹션(각 섹션당 하위 불릿 3~4개 이내)만 기술하되 `<planned_entities>`의 장소·세력·인물이 활동하는 거시적 권력 구도와 무대 규칙이 유기적으로 맞물리게 하십시오:
   1. 배경 및 세계의 핵심 법칙
   - 시대적/공간적 무대, 물리/마법/기술/사회 시스템의 작동 원리와 제약 조건(대가).
   2. 세력 구도 및 사회적 현실
   - 주요 집단/기관/계층 간의 역학 관계, 일상적인 사회 풍경, 수면 아래의 갈등 구조.
   3. 주인공(PC)의 초기 입지 및 직면 환경
   - 주인공의 현재 신분, 소속 거점, 주변 세계가 주인공을 대하는 시선과 당면한 물리적/사회적 과제.
   4. 서사 톤앤매너 및 감각적 디테일
   - 일상 호흡(surfaceTone)과 핵심 무게감(coreWeight)의 조화 기준, 시각/청각/후각적 배경 디테일.
3. Turn 0 시점의 무대와 법칙만 정의하십시오.
</rules>
""";
                string loreSummary = FormatBlueprintRoster(plan.LorebookPlan);
                string usr = $$"""
<concept>
{{plan.Concept}}
</concept>

<tone_architecture>
- 일상/표면 호흡: {{plan.SurfaceTone}}
- 배경/갈등 무게감: {{plan.CoreWeight}}
- 주인공 초기 입지: {{plan.PcInitialPosition}}
</tone_architecture>

<worldview_outline>
{{plan.WorldviewOutline}}
</worldview_outline>

<planned_entities>
{{loreSummary}}
</planned_entities>{{feedbackBlock}}

<final_instruction>
위 기획 데이터를 바탕으로 <rules>의 4대 번호 섹션 규격에 맞춘 마스터 세계관 레퍼런스를 순수 일반 텍스트로 출력하십시오.
</final_instruction>
""";
                return (sys, usr, "text/plain", null);
            }

            case AgentStep.LorebookGen:
            {
                string sys = GetLorebookBatchSystemInstruction();
                string fullRoster = FormatBlueprintRoster(plan.LorebookPlan);
                string loreListJson = JsonSerializer.Serialize(plan.LorebookPlan, JsonWriteOptions);
                string usr = $$"""
<tone_architecture>
- 일상/표면 호흡: {{plan.SurfaceTone}}
- 배경/갈등 무게감: {{plan.CoreWeight}}
</tone_architecture>

<worldview>
{{session.GeneratedWorldview}}
</worldview>

<full_blueprint_roster>
{{fullRoster}}
</full_blueprint_roster>

<target_lorebook_items>
{{loreListJson}}
</target_lorebook_items>{{feedbackBlock}}

<final_instruction>
위 <target_lorebook_items>의 모든 항목을 <full_blueprint_roster>의 관계망과 정합되도록 <output_format>의 LorebookEntry[] 규격에 맞춘 순수 JSON 배열로 출력하십시오.
</final_instruction>
""";
                return (sys, usr, "application/json", LorebookArrayResponseSchema);
            }

            case AgentStep.ScenarioGen:
            {
                string sys = """
<system_directive>
Role: 3인칭 전지적 게임 마스터 (Omniscient Game Master)
Task: 확정된 세계관과 전체 로어북 전문을 완벽히 반영하여 세션의 첫 장면(오프닝 시나리오)을 한국어 문학 산문으로 집필하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 본문은 순수 일반 텍스트 산문, 외부 발화용 큰따옴표(`"..."`), 내면 독백 및 강조용 낫표(`「...」`)로만 구성하고, 스탯 수치 언급 없이 오감에 기반한 물리적 묘사로만 전달하십시오.
2. 시작 장소의 공기, 조명, 소리, 냄새 등 감각적 배경 묘사를 `<lorebooks>`의 장소/세계관 설정과 일치시키고, 오프닝 현장에 직접 등장하는 인물(1~3명 이내로 밀도 있게 집중)의 외양·버릇 및 어조 특징(존비칭, 어미, 호흡)만 반영하십시오.
   - [복사 금지]: 로어북의 `말투/대사` 항목에 적힌 예시 대사 문장을 그대로 복사·인용하는 것을 엄격히 금지하며, 반드시 오프닝 상황에 맞는 새로운 대사를 창작하십시오.
   - [본심 조기 노출 금지]: 관계가 깊어진 뒤에야 드러나는 내면의 본심(연정 고백, 집착, 은밀한 거래 등)을 첫 만남에서 조기 노출하지 마십시오.
3. 주인공(PC)의 대사, 의사결정, 감정 단정을 임의로 서술하지 말고, NPC가 말을 건네거나 사건이 발생하여 주인공이 첫 행동을 선택해야 하는 직면 순간에서 3~4문단 분량으로 멈추십시오.
</rules>
""";
                string fullLorebooks = session.GeneratedLorebooks != null && session.GeneratedLorebooks.Count > 0
                    ? string.Join("\n\n", session.GeneratedLorebooks.Select(l => $"- {l.Name} ({l.Category}): {l.Content}"))
                    : FormatBlueprintRoster(plan.LorebookPlan);

                string usr = $$"""
<tone_architecture>
- 일상/표면 호흡: {{plan.SurfaceTone}}
- 배경/갈등 무게감: {{plan.CoreWeight}}
- 주인공 초기 입지: {{plan.PcInitialPosition}}
</tone_architecture>

<worldview>
{{session.GeneratedWorldview}}
</worldview>

<lorebooks>
{{fullLorebooks}}
</lorebooks>

<scenario_outline>
{{plan.ScenarioOutline}}
</scenario_outline>{{feedbackBlock}}

<final_instruction>
위 세계관과 로어북 설정을 유기적으로 녹여내어, 주인공이 첫 응답을 내놓아야 하는 직면 순간까지 3~4문단의 순수 산문으로 오프닝 시나리오를 작성하십시오.
</final_instruction>
""";
                return (sys, usr, "text/plain", null);
            }

            case AgentStep.StatusGen:
            {
                string sys = """
<system_directive>
Domain: TRPG State, Turn-0 Context & Genre Directing Initialization Engine
Task: 확정된 세계관, 로어북, 그리고 방금 집필된 <opening_scenario> 본문을 정밀 분석하여 상태창 스탯(stats), 상태창 수치 갱신 규칙(guide), Turn 0 초기 상태 데이터(initialChars, initialPlaces, initialItems), 그리고 세션 맞춤형 비수치 GM 수칙 2줄(genreDirectives)을 JSON으로 통합 추출·설계하십시오.
Output Language: Korean
</system_directive>

<rules>
1. stats는 <stats_plan>을 기반으로 주인공의 핵심 상태 수치와 <opening_scenario>에 직접 등장한 주요 등장인물의 관계도/호감도 초기치를 설정하되, 범위형은 "50/100", 단일 수치는 "100", 상태형은 "평온" 형식으로 표기하십시오.
2. guide는 상태창 갱신 모델(`BuildStatusRequest`)의 `<rules>` 하위에 직접 주입되는 수치 전용 규칙이므로, 순수 일반 텍스트와 하이픈 개조식(`- 스탯명: 상승 조건(+수치폭) / 하락 조건(-수치폭) / 유효 범위`)으로만 작성하십시오.
   - 아직 상태창에 등록되지 않은 로어북 주요 인물과 본편에서 새롭게 조우·상호작용할 경우, 해당 인물의 관계도 스탯(`인물명 호감도` 또는 `인물명 신뢰도`, 0~100 범위)을 초기치와 함께 신규 추가하여 추적하는 규칙을 반드시 명시하십시오.
3. initialChars는 <opening_scenario> 본문에 직접 등장하여 대사나 행동을 주고받은 모든 NPC를 누락 없이 등록하되, 각 항목의 name은 반드시 <valid_character_names>에 명시된 로어북 인물 고유명사와 글자 그대로 100% 일치시키십시오(임의 직함·수식어 추가 금지). 로어북(<world_lore>)의 복장·배경 설정 및 <current_state>의 장소명과 중복되지 않도록, "초기 태도(...)" 같은 고정 라벨이나 장소·복장 수식어를 배제하고 <opening_scenario> 마지막 순간의 "핵심 감정·태도 키워드, 즉각적 행동 요약"만 3~6어절(25자 이내)로 간결하게 작성하십시오.
4. initialPlaces는 <opening_scenario>가 실제로 벌어지고 있는 구체적인 시작 장소명 배열(1~2개)로 구성하십시오. initialItems는 <opening_scenario> 마지막 장면 시점에 주인공이 몸에 직접 휴대·착용하고 있거나 손이 닿는 현장 소지품(1~4개)으로만 구성하며, 지하실·창고 등 다른 공간에 보관·봉인되어 즉각 사용 불가능한 물품이나 거점 시설은 제외하십시오.
5. genreDirectives는 메인 서사 GM 시스템 프롬프트의 `<rules>` 4번·5번 슬롯에 통합될 비수치 서사/판정 연출 수칙 2개 문자열 배열입니다. 스탯 수치나 캐릭터 개별 프로필을 일절 포함하지 말고(Zero-Numeric), 소제목 라벨 없이 아래 2개 명령문을 각 1문장 개조식으로 작성하십시오:
   - 첫 번째 문장(4번 수칙 - 서사 분위기 및 환경 연출): 평소 일상 호흡(surfaceTone)과 위기 시 긴장감(coreWeight)의 온도차를 연출하되, **반드시 배경 환경의 감각 묘사(공기, 빛, 소리, 냄새)와 NPC들의 대사 템포·태도·압박감만을 연출 대상으로 삼으십시오.** 유저 캐릭터(PC)의 감정, 성격, 유머 감각, 살기 표출, 전투 행동을 대신 규정하거나 지시문 주어로 삼는 것을 엄격히 금지합니다(PC 불간섭 원칙 준수).
   - 두 번째 문장(5번 수칙 - 세계관 인과율 및 위험 판정): 이 세계관과 로어북의 고유 법칙·제약에 기반하여 유저 행동의 개연성, 대가(Risk), 세력/환경의 반작용을 공정하게 판정하는 수칙.
6. <output_format>의 TypeScript 인터페이스 규격에 부합하는 순수 JSON 객체만 출력하십시오.
</rules>

<output_format>
interface KeyValueItem {
  name: string;
  value: string;
}

interface StatusDesignPayload {
  stats: KeyValueItem[];        // 초기 스탯 및 관계도 배열 ({ name: 스탯명, value: 초기값 })
  guide: string;                // 상태창 갱신 전용 개조식 가이드 (수치 규칙 전담)
  initialChars: KeyValueItem[]; // Turn 0 등장인물 초기 상태 배열 (name은 로어북 인물명과 정확히 일치)
  initialPlaces: string[];      // Turn 0 시작 장소 배열
  initialItems: string[];       // Turn 0 초기 소지품 배열
  genreDirectives: string[];    // 메인 서사 GM용 4번·5번 비수치 연출/판정 수칙 (2개 문장 배열)
}
</output_format>

<example>
{
  "stats": [
    { "name": "체력", "value": "100/100" },
    { "name": "인물명 신뢰도", "value": "30/100" }
  ],
  "guide": "- 체력: 부상이나 과로 시 -5~-30 감소, 휴식이나 치료 시 +10~+50 회복 (0~100 범위 유지)\n- 인물명 신뢰도: 가치관에 부합하는 배려나 협력 시 +2~+10 상승, 기만이나 무례한 언행 시 -5~-20 하락 (0~100 범위 유지)\n- 신규 인물 관계도: 본편 진행 중 새로운 주요 인물과 대면·상호작용 시 해당 인물의 호감도(초기치 20~30/100)를 상태창에 신규 추가하여 추적",
  "initialChars": [
    { "name": "인물명", "value": "경계심과 호기심, 팔짱을 낀 채 주인공을 주시 중" }
  ],
  "initialPlaces": [
    "시작 장소명"
  ],
  "initialItems": [
    "초기 휴대 소지품 1",
    "초기 휴대 소지품 2"
  ],
  "genreDirectives": [
    "평소에는 학원 마을의 활기찬 소음과 이웃 NPC들의 정겨운 대화 템포를 중심으로 묘사하되, 지하 유적이나 금기 단서 접촉 시 주변 공기의 급격한 냉각과 적대 세력의 서늘한 압박감으로 환경 분위기를 급반전시킬 것.",
    "학원 도시의 자치 규율과 마도구 과부하 제약을 엄격히 적용하여, 무모한 무력 행사 시 학생회 감시망 작동과 물리적 반동을 즉각 부여할 것."
  ]
}
</example>
""";
                string statsListJson = JsonSerializer.Serialize(plan.StatsPlan, JsonWriteOptions);
                string loreSummary = session.GeneratedLorebooks != null && session.GeneratedLorebooks.Count > 0
                    ? string.Join("\n", session.GeneratedLorebooks.Select(l => $"- {l.Name} ({l.Category}): {l.Content}"))
                    : FormatBlueprintRoster(plan.LorebookPlan);

                var validCharNames = session.GeneratedLorebooks != null && session.GeneratedLorebooks.Count > 0
                    ? session.GeneratedLorebooks
                        .Where(l => string.Equals(l.Category?.Trim(), "인물", StringComparison.OrdinalIgnoreCase))
                        .Select(l => l.Name)
                        .ToList()
                    : plan.LorebookPlan
                        .Where(l => string.Equals(l.Category?.Trim(), "인물", StringComparison.OrdinalIgnoreCase))
                        .Select(l => l.Name)
                        .ToList();

                string validCharNamesText = validCharNames.Count > 0
                    ? string.Join(", ", validCharNames)
                    : "로어북 인물 참조";

                string openingScenarioText = !string.IsNullOrWhiteSpace(session.GeneratedScenario)
                    ? session.GeneratedScenario
                    : plan.ScenarioOutline;

                string worldviewText = !string.IsNullOrWhiteSpace(session.GeneratedWorldview)
                    ? session.GeneratedWorldview
                    : SanitizeWorldview(plan.WorldviewOutline);

                string usr = $$"""
<tone_architecture>
- 일상/표면 호흡: {{plan.SurfaceTone}}
- 배경/갈등 무게감: {{plan.CoreWeight}}
- 연출 개요: {{plan.PromptOutline}}
</tone_architecture>

<worldview>
{{worldviewText}}
</worldview>

<lorebooks>
{{loreSummary}}
</lorebooks>

<valid_character_names>
{{validCharNamesText}}
</valid_character_names>

<stats_plan>
{{statsListJson}}
</stats_plan>

<opening_scenario>
{{openingScenarioText}}
</opening_scenario>{{feedbackBlock}}

<final_instruction>
위 <opening_scenario>와 <lorebooks>, <worldview>를 바탕으로 Turn 0 시점의 현장 상태 및 세션 특화 GM 수칙이 통합된 <output_format>의 StatusDesignPayload JSON을 출력하십시오.
</final_instruction>
""";
                return (sys, usr, "application/json", StatusDesignResponseSchema);
            }

            default:
                return ("", "", "text/plain", null);
        }
    }

    private static string GetLorebookBatchSystemInstruction() => """
<system_directive>
Domain: High-Density Lorebook Entity Synthesis Engine
Task: 할당된 로어북 기획 항목들을 전체 청사진 및 배경 설정과 유기적으로 맞물리는 개조식 명사구 압축(Telegraphic Compression) 포맷의 JSON 배열로 작성하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 런타임에 각 항목은 `<world_lore>` 슬롯 내부에 `- {name}: {content}` 형태로 주입되므로, 서술어 어미(`~이다`, `~한다`)와 접속사를 배제하고 핵심 명사구 위주의 개조식 단문으로 압축하십시오.
2. `content`는 순수 일반 텍스트, 줄바꿈(`\n`), 들여쓰기 불릿(`  * 항목명: 내용`), 대사용 큰따옴표(`"..."`)로만 구성하며, 각 하위 불릿은 줄바꿈 없이 1줄로 작성하십시오.
3. category가 '인물'인 경우 아래 5줄 불릿 구조로 작성하십시오:
   `분류(인물): 신분/소속 및 핵심 정체성 한 줄 정의`
   `  * 외양/특징: 핵심 시각적 인장, 복장, 습관적 행동/버릇 (명사구 나열)`
   `  * 표면/내면: 표면(대외적 태도·역할) / 내면(친밀·위기 시 드러나는 속마음, 결핍, 갭 모에 - 복수 인물 기획 시 인물마다 결핍과 호감 양상이 독점욕·집착으로 획일화되지 않도록 차별화)`
   `  * PC 관계: 초기 인식 및 태도 변화 조건`
   `  * 타 인물/세력 관계: 로어북 내 다른 등장인물 및 소속 세력·장소와의 구체적 역학·감정·이해관계 (고유명사 직접 명시)`
   `  * 말투/대사: 어조 특징 요약. "일상 대표 대사 샘플" / "본심·감정 노출 시 대사 샘플" (실제 큰따옴표 대사 2개 필수)`
4. category가 '장소', '아이템', '세계관'인 경우 아래 3줄 불릿 구조로 작성하십시오:
   `분류(카테고리): 핵심 정의 한 줄 요약`
   `  * 감각/외관: 시·청·후각적 핵심 환경/외형 디테일 (명사구 중심)`
   `  * 규칙/원리: 고유 법칙, 규율, 작동 원리 및 제약/대가`
   `  * 서사 훅: 연관된 인물·세력 고유명사 및 상호작용 시 발생 가능한 사건·이면`
5. keywords는 한국어 조사가 제거된 순수 고유명사·직함·별칭 4~7개 배열로 지정하고, priority는 오름차순 우선순위(0=핵심, 1=주요, 2=일반, 3=배경) 수치를 유지하십시오.
6. <output_format>의 TypeScript 인터페이스 규격에 부합하는 순수 JSON 배열만 출력하십시오.
</rules>

<output_format>
interface LorebookEntry {
  name: string;
  keywords: string[];
  content: string;
  category: '인물' | '장소' | '아이템' | '세계관';
  priority: number; // 0 | 1 | 2 | 3
}
</output_format>

<example>
[
  {
    "name": "항목 이름",
    "keywords": ["이름", "직함", "별칭"],
    "content": "분류(인물): 핵심 정의\n  * 외양/특징: 명사구 중심 시각 특징, 버릇\n  * 표면/내면: 표면(대외적 모습) / 내면(실제 속마음과 갭 모에)\n  * PC 관계: 초기 태도 및 변화 조건\n  * 타 인물/세력 관계: 타 인물·세력과의 구체적 이해관계 및 갈등\n  * 말투/대사: 어조 요약. \"일상 대사 샘플\" / \"본심 대사 샘플\"",
    "category": "인물",
    "priority": 0
  }
]
</example>
""";

    private static string FormatBlueprintRoster(IEnumerable<LorebookPlanItem>? items)
    {
        if (items == null) return "없음";
        var lines = items.Select(l =>
        {
            string conn = !string.IsNullOrWhiteSpace(l.ConnectedEntities) ? $" / 연결망: {l.ConnectedEntities}" : "";
            return $"- {l.Name} ({l.Category}, 우선순위 {l.Priority}): {l.Brief} / 표면: {l.SurfaceRole} / 내면: {l.InnerDepth}{conn}";
        }).ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : "없음";
    }

    /// <summary>
    /// 2-Wave 통합 앙상블 인과 로어북 생성 파이프라인:
    /// Wave 1에서 비인물(장소·세계관·아이템) 무대 인프라 전체를 단일 추론 공간(1회 호출)에서 확정한 뒤,
    /// Wave 2에서 확정된 배경 로어북 전문과 전체 청사진 명단을 주입하여 모든 등장인물 전원을 단일 추론 공간(1회 호출, 앙상블 캐스팅)에서 동시 생성합니다.
    /// </summary>
    private async Task<string> GenerateLorebooksCausallyAsync(
        AgentPlan plan,
        string worldview,
        CancellationToken ct)
    {
        string systemInstructionText = GetLorebookBatchSystemInstruction();
        string fullBlueprintRoster = FormatBlueprintRoster(plan.LorebookPlan);

        var worldItems = plan.LorebookPlan
            .Where(i => !string.Equals(i.Category?.Trim(), "인물", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var characterItems = plan.LorebookPlan
            .Where(i => string.Equals(i.Category?.Trim(), "인물", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var wave1Entries = new List<LorebookEntry>();

        // Wave 1: 배경/장소/아이템 무대 인프라 전체 1회 통합 생성
        if (worldItems.Count > 0)
        {
            string batchJson = JsonSerializer.Serialize(worldItems, JsonWriteOptions);
            string userPrompt = $$"""
<tone_architecture>
- 일상/표면 호흡: {{plan.SurfaceTone}}
- 배경/갈등 무게감: {{plan.CoreWeight}}
</tone_architecture>

<worldview>
{{worldview}}
</worldview>

<full_blueprint_roster>
{{fullBlueprintRoster}}
</full_blueprint_roster>

<target_lorebook_items>
{{batchJson}}
</target_lorebook_items>

<final_instruction>
위 <target_lorebook_items>의 모든 배경/장소/아이템 항목이 서로의 지리적·제도적 설정 및 <full_blueprint_roster>의 연결망과 빈틈없이 맞물리도록 <output_format>의 LorebookEntry 배열(JSON)로 한 번에 작성하여 출력하십시오.
</final_instruction>
""";
            var request = new GeminiRequest(
                SystemInstruction: new Content("system", [new Part(systemInstructionText)]),
                Contents: [new Content("user", [new Part(userPrompt)])],
                SafetySettings: null,
                GenerationConfig: new GenerationConfig(null, ArchitectMaxOutputTokens, "application/json", LorebookArrayResponseSchema, new ThinkingConfig(ThinkingLevel.high))
            );

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Pro, ct))
            {
                sb.Append(chunk);
            }

            wave1Entries.AddRange(ParseLorebooks(sb.ToString()) ?? []);
        }

        // Wave 2: 확정된 Wave 1 무대 로어북 전문 + 전체 명단을 주입하여 등장인물 전원 1회 통합 앙상블 캐스팅
        var wave2Entries = new List<LorebookEntry>();
        if (characterItems.Count > 0)
        {
            string establishedWorldLore = wave1Entries.Count > 0
                ? string.Join("\n\n", wave1Entries.Select(e => $"- {e.Name} ({e.Category}): {e.Content}"))
                : "별도 배경 로어북 없음 (세계관 및 전체 명단 참조)";

            string batchJson = JsonSerializer.Serialize(characterItems, JsonWriteOptions);
            string userPrompt = $$"""
<tone_architecture>
- 일상/표면 호흡: {{plan.SurfaceTone}}
- 배경/갈등 무게감: {{plan.CoreWeight}}
</tone_architecture>

<worldview>
{{worldview}}
</worldview>

<full_blueprint_roster>
{{fullBlueprintRoster}}
</full_blueprint_roster>

<established_world_lore>
{{establishedWorldLore}}
</established_world_lore>

<target_lorebook_items>
{{batchJson}}
</target_lorebook_items>

<final_instruction>
위 <established_world_lore>와 <full_blueprint_roster>를 바탕으로, <target_lorebook_items>의 모든 등장인물이 서로 겹치지 않는 고유한 말투·대사 스펙트럼을 갖고 주인공뿐만 아니라 다른 인물·세력·장소와도 구체적인 양방향 관계망을 형성하도록 하되, 여러 인물의 내면 결핍과 호감 표현 방식이 동일한 클리셰(독점욕·집착 등)로 획일화되지 않도록 인물마다 관계 발전의 결(선의의 경쟁, 순수한 헌신, 지적 호기심과 공범 의식, 어른스러운 비호 등)을 명확히 차별화하여 <output_format>의 LorebookEntry 배열(JSON)로 한 번에 작성하여 출력하십시오.
</final_instruction>
""";
            var request = new GeminiRequest(
                SystemInstruction: new Content("system", [new Part(systemInstructionText)]),
                Contents: [new Content("user", [new Part(userPrompt)])],
                SafetySettings: null,
                GenerationConfig: new GenerationConfig(null, ArchitectMaxOutputTokens, "application/json", LorebookArrayResponseSchema, new ThinkingConfig(ThinkingLevel.high))
            );

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Pro, ct))
            {
                sb.Append(chunk);
            }

            wave2Entries.AddRange(ParseLorebooks(sb.ToString()) ?? []);
        }

        var mergedEntries = wave1Entries
            .Concat(wave2Entries)
            .DistinctBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(mergedEntries, JsonWriteOptions);
    }

    /// <summary>
    /// `<rules>` 하단(4~5번)에 통합될 세션 특화 GM 운영/판정 수칙 2줄을 생성합니다.
    /// 세계관, 로어북 본문 전문, 스탯 항목, 오프닝 시나리오를 모두 참조하여 정합성을 극대화합니다.
    /// </summary>
    private async Task<string> GenerateGenreDirectivesAsync(
        AgentPlan plan,
        ArchitectSession session,
        string? userFeedback,
        CancellationToken ct)
    {
        string systemInstructionText = """
<system_directive>
Domain: Narrative Style & Genre Directing Specification Engine
Task: 시스템 프롬프트의 `<rules>` 4번과 5번 항목으로 통합될 세션 맞춤형 GM 운영 및 판정 수칙 2줄을 개조식으로 작성하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 개별 등장인물의 이름/프로필(`<world_lore>` 전담)이나 정적인 배경 풍경 나열(`<master_setting>` 전담), 스탯 수치 규칙을 일절 포함하지 마십시오.
2. 기본 GM 수칙(1~3번: 오감 중심 객관 서술, PC 주체성/턴 존중, 현실적 인과율 판정)은 상단에 이미 고정되어 있으므로, 소제목 라벨 없이 아래 4번·5번 본문 지시문 2줄만 각 1문장 개조식으로 작성하십시오:
   4. 서사 분위기 및 환경 연출 수칙: 평소 일상 호흡(surfaceTone)과 위기 시 긴장감(coreWeight)의 온도차를 연출하되, 반드시 배경 환경의 감각 묘사(공기, 빛, 소리, 냄새)와 NPC들의 대사 템포·태도·압박감만을 연출 대상으로 삼으십시오. 유저 캐릭터(PC)의 감정, 성격, 유머 감각, 살기 표출, 전투 행동을 대신 규정하거나 지시문 주어로 삼는 것을 엄격히 금지합니다(1줄).
   5. 세계관 인과율 및 위험 판정 수칙: 이 세계관의 핵심 법칙과 제약에 기반하여 유저 행동의 개연성, 대가(Risk), 세력/환경의 반작용을 공정하게 판정하는 수칙 (1줄).
</rules>
""";

        string feedbackBlock = !string.IsNullOrWhiteSpace(userFeedback)
            ? $"\n\n<user_feedback>\n{userFeedback}\n</user_feedback>"
            : "";

        string loreOverview = session.GeneratedLorebooks != null && session.GeneratedLorebooks.Count > 0
            ? string.Join("\n", session.GeneratedLorebooks.Select(l => $"- {l.Name} ({l.Category}): {l.Content}"))
            : FormatBlueprintRoster(plan.LorebookPlan);

        string trackedStats = session.GeneratedStats != null && session.GeneratedStats.Count > 0
            ? string.Join(", ", session.GeneratedStats.Keys)
            : string.Join(", ", plan.StatsPlan);

        string openingScenario = !string.IsNullOrWhiteSpace(session.GeneratedScenario)
            ? session.GeneratedScenario
            : plan.ScenarioOutline;

        string userPrompt = $$"""
<session_title>
{{plan.SessionTitle}}
</session_title>

<tone_architecture>
- 일상/표면 호흡: {{plan.SurfaceTone}}
- 배경/갈등 무게감: {{plan.CoreWeight}}
- 연출 개요: {{plan.PromptOutline}}
</tone_architecture>

<worldview>
{{session.GeneratedWorldview}}
</worldview>

<lorebooks_overview>
{{loreOverview}}
</lorebooks_overview>

<tracked_stats>
{{trackedStats}}
</tracked_stats>

<opening_scenario>
{{openingScenario}}
</opening_scenario>{{feedbackBlock}}

<final_instruction>
위 세계관, 로어북 구도, 오프닝 분위기를 모두 반영하여 `<rules>`에 추가될 4번과 5번 GM 수칙 2줄만 순수 일반 텍스트로 출력하십시오.
</final_instruction>
""";

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(systemInstructionText)]),
            Contents: [new Content("user", [new Part(userPrompt)])],
            SafetySettings: null,
            GenerationConfig: new GenerationConfig(null, ArchitectMaxOutputTokens, "text/plain", null, new ThinkingConfig(ThinkingLevel.high))
        );

        var sb = new StringBuilder();
        await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Pro, ct))
        {
            sb.Append(chunk);
        }

        return sb.ToString();
    }

    public async IAsyncEnumerable<string> ReviseContentAsync(
        AgentStep step,
        string previousContent,
        string userFeedback,
        AgentPlan plan,
        ArchitectSession? session = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // LorebookReview: 전체 배열 단일 덮어쓰기로 인한 압축 손실(Lazy Compression)을 방지하기 위해 외과수술식 핀포인트 보정 수행
        if (step == AgentStep.LorebookReview)
        {
            string revisedLorebooksJson = await ReviseLorebooksSurgicallyAsync(previousContent, userFeedback, plan, session, ct);
            yield return revisedLorebooksJson;
            yield break;
        }

        string systemInstructionText;
        string responseMimeType = "text/plain";
        object? responseSchema = null;

        switch (step)
        {
            case AgentStep.PlanReview:
                systemInstructionText = """
<system_directive>
Domain: Narrative Session Architecture & Worldbuilding Blueprint Engine
Task: <user_feedback>을 반영하여 <previous_content>의 설계 계획 및 마스터 세계관(AgentPlan) JSON을 수정하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 기존 JSON 스키마(sessionTitle, surfaceTone, coreWeight, pcInitialPosition, worldviewOutline, lorebookPlan, statsPlan, scenarioOutline, promptOutline)와 항목 간 관계망(connectedEntities)을 유지하면서 피드백을 반영하십시오.
2. worldviewOutline은 4대 번호 섹션(1~4)과 하이픈 개조식 불릿(- 항목명: 내용) 포맷을 유지하십시오.
3. <output_format>의 TypeScript 인터페이스 규격에 부합하는 순수 JSON 객체만 출력하십시오.
</rules>

<output_format>
interface LorebookPlanItem {
  name: string;
  category: '인물' | '장소' | '아이템' | '세계관';
  brief: string;
  surfaceRole: string;
  innerDepth: string;
  connectedEntities: string;
  keywords: string[];
  priority: number;
}

interface AgentPlan {
  sessionTitle: string;
  surfaceTone: string;
  coreWeight: string;
  pcInitialPosition: string;
  worldviewOutline: string;
  lorebookPlan: LorebookPlanItem[];
  statsPlan: string[];
  scenarioOutline: string;
  promptOutline: string;
}
</output_format>
""";
                responseMimeType = "application/json";
                responseSchema = AgentPlanResponseSchema;
                break;

            case AgentStep.WorldviewReview:
                systemInstructionText = """
<system_directive>
Domain: Worldbuilding Reference Specification Engine
Task: <user_feedback>을 반영하여 <previous_content>의 마스터 세계관 레퍼런스를 수정하십시오.
Output Language: Korean
</system_directive>
<rules>
- 오직 번호 섹션 헤더(`1. 배경 및 세계의 핵심 법칙` 등)와 하이픈 개조식 불릿(`- 항목: 내용`) 형식의 순수 일반 텍스트로만 구성하십시오.
- 서술어 어미를 배제하고 명사구 중심의 1줄 개조식 압축 포맷을 유지하십시오.
</rules>
""";
                break;

            case AgentStep.StatusReview:
                systemInstructionText = """
<system_directive>
Domain: TRPG State, Turn-0 Context & Genre Directing Initialization Engine
Task: <user_feedback>을 반영하여 <previous_content>의 상태창, Turn 0 초기 상태 및 세션 특화 GM 수칙 JSON을 수정하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 기존 JSON 구조(`stats`, `guide`, `initialChars`, `initialPlaces`, `initialItems`, `genreDirectives`)를 모두 유지하며 변경 사항을 반영하십시오.
2. `initialChars`의 각 `name`은 반드시 로어북 인물 고유명사와 정확히 일치시키십시오.
3. `guide`는 순수 일반 텍스트와 `- 스탯명: 증감 조건 및 수치 폭` 형식의 개조식으로만 작성하십시오.
4. `genreDirectives`는 스탯 수치를 배제한 비수치 GM 연출/판정 수칙 2개 문장 배열로 작성하되, PC의 감정/행동이 아닌 환경 및 NPC 연출만 지시하도록 작성하십시오(Zero-Numeric 및 PC 불간섭 준수).
5. <output_format>의 TypeScript 인터페이스 규격에 부합하는 순수 JSON 객체만 출력하십시오.
</rules>

<output_format>
interface KeyValueItem {
  name: string;
  value: string;
}

interface StatusDesignPayload {
  stats: KeyValueItem[];
  guide: string;
  initialChars: KeyValueItem[];
  initialPlaces: string[];
  initialItems: string[];
  genreDirectives: string[];
}
</output_format>
""";
                responseMimeType = "application/json";
                responseSchema = StatusDesignResponseSchema;
                break;

            case AgentStep.ScenarioReview:
                systemInstructionText = """
<system_directive>
Role: 3인칭 전지적 게임 마스터 (Omniscient Game Master)
Task: <user_feedback>을 반영하여 <previous_content>의 오프닝 시나리오를 수정하십시오.
Output Language: Korean
</system_directive>
<rules>
- 본문은 순수 문학적 산문, 외부 발화용 큰따옴표(`"..."`), 독백용 낫표(`「...」`)로만 구성하십시오.
- 로어북에 정의된 인물의 외양과 말투 특징을 유지하되 예시 대사 문장의 단순 복사를 금지하고, 주인공(PC)의 대사나 행동을 임의로 결정하지 말며 직면 상황에서 서술을 멈추십시오.
</rules>
""";
                break;

            case AgentStep.PromptReview:
                systemInstructionText = """
<system_directive>
Domain: Narrative Style & Genre Directing Specification Engine
Task: <user_feedback>을 반영하여 <previous_content>의 `<rules>` 하단 세션 특화 수칙(4~5번)을 수정하십시오.
Output Language: Korean
</system_directive>
<rules>
- 개별 인물 프로필이나 정적 배경 나열, 스탯 수치 규칙을 배제하고, GM의 장르 호흡 조율(4번) 및 세계관 고유 판정 원칙(5번)만 각 1줄 개조식 일반 텍스트로 작성하십시오(Zero-Numeric).
</rules>
""";
                break;

            default:
                yield break;
        }

        string cumulativeContextBlock = "";
        if (session != null && (step == AgentStep.ScenarioReview || step == AgentStep.StatusReview || step == AgentStep.PromptReview))
        {
            string worldviewRef = !string.IsNullOrWhiteSpace(session.GeneratedWorldview)
                ? session.GeneratedWorldview
                : SanitizeWorldview(plan.WorldviewOutline);
            string loreRef = session.GeneratedLorebooks != null && session.GeneratedLorebooks.Count > 0
                ? string.Join("\n", session.GeneratedLorebooks.Select(l => $"- {l.Name} ({l.Category}): {l.Content}"))
                : FormatBlueprintRoster(plan.LorebookPlan);

            cumulativeContextBlock = $$"""
<worldview>
{{worldviewRef}}
</worldview>

<lorebooks>
{{loreRef}}
</lorebooks>

""";
        }

        string userPrompt = $$"""
{{cumulativeContextBlock}}<previous_content>
{{previousContent}}
</previous_content>

<user_feedback>
{{userFeedback}}
</user_feedback>

<final_instruction>
위 <user_feedback>을 반영하여 수정된 최종 결과물만 즉시 출력하십시오.
</final_instruction>
""";

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(systemInstructionText)]),
            Contents: [new Content("user", [new Part(userPrompt)])],
            SafetySettings: null,
            GenerationConfig: new GenerationConfig(null, ArchitectMaxOutputTokens, responseMimeType, responseSchema, new ThinkingConfig(ThinkingLevel.high))
        );

        var rawBuilder = new StringBuilder();
        await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Pro, ct))
        {
            rawBuilder.Append(chunk);
        }

        string rawResult = rawBuilder.ToString();
        string sanitized = step switch
        {
            AgentStep.WorldviewReview => SanitizeWorldview(rawResult),
            AgentStep.ScenarioReview => SanitizeScenario(rawResult),
            AgentStep.PromptReview => CompileHybridSystemPrompt(rawResult, plan),
            _ => rawResult
        };

        yield return sanitized;
    }

    /// <summary>
    /// 로어북 수정 시 전체 항목을 단일 프롬프트로 재작성하지 않고,
    /// C# 결정론적 검수 관문이 지목한 `[수정 대상: ...]` 항목 또는 유저 피드백에 언급된 특정 항목만 추출하여
    /// 단일 통합 호출로 정밀 재생성한 뒤 기존 로어북 배열에 외과수술식으로 병합합니다.
    /// </summary>
    private async Task<string> ReviseLorebooksSurgicallyAsync(
        string previousContent,
        string userFeedback,
        AgentPlan plan,
        ArchitectSession? session,
        CancellationToken ct)
    {
        var existingEntries = ParseLorebooks(previousContent)
            ?? session?.GeneratedLorebooks
            ?? [];

        var targetNames = ExtractTargetLorebookNames(userFeedback, plan.LorebookPlan, existingEntries);

        List<LorebookPlanItem> itemsToRegenerate;
        bool isPartialSurgicalUpdate = false;

        if (targetNames.Count > 0 && existingEntries.Count > 0 && targetNames.Count < existingEntries.Count)
        {
            isPartialSurgicalUpdate = true;
            itemsToRegenerate = targetNames.Select(name =>
            {
                var planned = plan.LorebookPlan.FirstOrDefault(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
                if (planned != null) return planned;

                var existing = existingEntries.FirstOrDefault(e => string.Equals(e.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
                return new LorebookPlanItem
                {
                    Name = existing?.Name ?? name,
                    Category = existing?.Category ?? "세계관",
                    Brief = existing?.Content.Split('\n').FirstOrDefault() ?? "",
                    Keywords = existing?.Keywords ?? [name],
                    Priority = existing?.Priority ?? 1
                };
            }).ToList();
        }
        else if (plan.LorebookPlan.Count > 0)
        {
            itemsToRegenerate = plan.LorebookPlan;
        }
        else
        {
            itemsToRegenerate = existingEntries.Select(e => new LorebookPlanItem
            {
                Name = e.Name,
                Category = e.Category,
                Brief = e.Content.Split('\n').FirstOrDefault() ?? "",
                Keywords = e.Keywords,
                Priority = e.Priority
            }).ToList();
        }

        string systemInstructionText = GetLorebookBatchSystemInstruction();
        string fullBlueprintRoster = FormatBlueprintRoster(plan.LorebookPlan);
        string worldview = !string.IsNullOrWhiteSpace(session?.GeneratedWorldview)
            ? session.GeneratedWorldview
            : SanitizeWorldview(plan.WorldviewOutline);
        string existingLoreSummary = existingEntries.Count > 0
            ? string.Join("\n\n", existingEntries.Select(e => $"- {e.Name} ({e.Category}): {e.Content}"))
            : "없음";

        string batchJson = JsonSerializer.Serialize(itemsToRegenerate, JsonWriteOptions);
        string userPrompt = $$"""
<tone_architecture>
- 일상/표면 호흡: {{plan.SurfaceTone}}
- 배경/갈등 무게감: {{plan.CoreWeight}}
</tone_architecture>

<worldview>
{{worldview}}
</worldview>

<full_blueprint_roster>
{{fullBlueprintRoster}}
</full_blueprint_roster>

<existing_lorebooks>
{{existingLoreSummary}}
</existing_lorebooks>

<target_lorebook_items>
{{batchJson}}
</target_lorebook_items>

<user_feedback>
{{userFeedback}}
</user_feedback>

<final_instruction>
위 <user_feedback>과 <existing_lorebooks>의 정합성을 반영하여 <target_lorebook_items>의 각 항목을 <output_format>의 LorebookEntry 배열(JSON)로 수정·작성하여 출력하십시오.
</final_instruction>
""";
        var request = new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(systemInstructionText)]),
            Contents: [new Content("user", [new Part(userPrompt)])],
            SafetySettings: null,
            GenerationConfig: new GenerationConfig(null, ArchitectMaxOutputTokens, "application/json", LorebookArrayResponseSchema, new ThinkingConfig(ThinkingLevel.high))
        );

        var sb = new StringBuilder();
        await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Pro, ct))
        {
            sb.Append(chunk);
        }

        var regeneratedEntries = (ParseLorebooks(sb.ToString()) ?? [])
            .DistinctBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (isPartialSurgicalUpdate && existingEntries.Count > 0 && regeneratedEntries.Count > 0)
        {
            var regenMap = regeneratedEntries.ToDictionary(e => e.Name.Trim(), e => e, StringComparer.OrdinalIgnoreCase);
            var mergedList = new List<LorebookEntry>(existingEntries.Count);
            var replacedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var existing in existingEntries)
            {
                if (regenMap.TryGetValue(existing.Name.Trim(), out var updated))
                {
                    mergedList.Add(updated);
                    replacedNames.Add(existing.Name.Trim());
                }
                else
                {
                    mergedList.Add(existing);
                }
            }

            foreach (var regen in regeneratedEntries)
            {
                if (!replacedNames.Contains(regen.Name.Trim()))
                {
                    mergedList.Add(regen);
                }
            }

            return JsonSerializer.Serialize(mergedList, JsonWriteOptions);
        }

        return JsonSerializer.Serialize(regeneratedEntries.Count > 0 ? regeneratedEntries : existingEntries, JsonWriteOptions);
    }

    private static List<string> ExtractTargetLorebookNames(
        string userFeedback,
        IEnumerable<LorebookPlanItem>? planItems,
        IEnumerable<LorebookEntry>? existingEntries)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(userFeedback)) return result;

        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (planItems != null)
        {
            foreach (var p in planItems)
            {
                if (!string.IsNullOrWhiteSpace(p.Name)) knownNames.Add(p.Name.Trim());
            }
        }
        if (existingEntries != null)
        {
            foreach (var e in existingEntries)
            {
                if (!string.IsNullOrWhiteSpace(e.Name)) knownNames.Add(e.Name.Trim());
            }
        }

        // 1. [수정 대상: 항목1, 항목2] 태그 파싱
        var tagMatch = Regex.Match(userFeedback, @"\[수정\s*대상:\s*([^\]]+)\]");
        if (tagMatch.Success)
        {
            var taggedNames = tagMatch.Groups[1].Value
                .Split([',', '、', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n));

            foreach (var tn in taggedNames)
            {
                var matchedKnown = knownNames.FirstOrDefault(k =>
                    string.Equals(k, tn, StringComparison.OrdinalIgnoreCase) ||
                    k.Contains(tn, StringComparison.OrdinalIgnoreCase) ||
                    tn.Contains(k, StringComparison.OrdinalIgnoreCase));
                result.Add(matchedKnown ?? tn);
            }
        }

        // 2. 태그가 없을 경우 피드백 본문에 직접 언급된 로어북 항목명 탐지
        if (result.Count == 0 && knownNames.Count > 0)
        {
            foreach (var name in knownNames)
            {
                if (userFeedback.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(name);
                }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public AgentPlan? ParsePlan(string rawResponse)
    {
        var plan = LlmJsonParser.DeserializeSafe<AgentPlan>(rawResponse);
        if (plan == null) return null;

        plan.WorldviewOutline = SanitizeWorldview(plan.WorldviewOutline);

        // 필수 필드 정규화 및 Priority 보정 (0=최우선 핵심, 1=주요, 2=일반, 3=배경)
        if (plan.LorebookPlan != null)
        {
            foreach (var item in plan.LorebookPlan)
            {
                item.Priority = NormalizePriority(item.Priority);
                item.Keywords = NormalizeKeywords(item.Name, item.Keywords);
                item.ConnectedEntities ??= "";
            }
        }

        return plan;
    }

    public List<LorebookEntry>? ParseLorebooks(string rawResponse)
    {
        var entries = LlmJsonParser.DeserializeArraySafe<List<LorebookEntry>>(rawResponse);
        if (entries == null || entries.Count == 0) return null;

        var sanitizedList = new List<LorebookEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;

            int normalizedPriority = NormalizePriority(entry.Priority);
            var normalizedKeywords = NormalizeKeywords(entry.Name, entry.Keywords);
            string cleanContent = SanitizeLorebookContent(entry.Content);

            sanitizedList.Add(new LorebookEntry(
                Name: entry.Name.Trim(),
                Keywords: normalizedKeywords,
                Content: cleanContent,
                Category: string.IsNullOrWhiteSpace(entry.Category) ? "세계관" : entry.Category.Trim(),
                Priority: normalizedPriority,
                Embedding: entry.Embedding
            ));
        }

        return sanitizedList.Count > 0 ? sanitizedList : null;
    }

    /// <summary>
    /// 로어북 우선순위를 GRC 표준 오름차순 스케일(0=최우선 핵심, 1=주요, 2=일반, 3=배경)로 정규화합니다.
    /// </summary>
    public static int NormalizePriority(int rawPriority)
    {
        if (rawPriority < 0) return 0;
        if (rawPriority <= 3) return rawPriority;
        // LLM이 큰 숫자(100, 80, 60 등)로 반환한 경우 0~3 체계로 자동 매핑
        if (rawPriority >= 90) return 0;
        if (rawPriority >= 75) return 1;
        if (rawPriority >= 50) return 2;
        return 3;
    }

    /// <summary>
    /// 신규 물리적 스키마(`KeyValueItem[]` 배열 + `genreDirectives`)와 기존 단위 테스트용(`Record<string, string>` 객체) 포맷을 모두 수용하는 양방향 파서입니다.
    /// </summary>
    public StatusDesignPayload? ParseStatusDesignPayload(
        string rawResponse,
        IEnumerable<LorebookEntry>? lorebooks = null)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return null;

        try
        {
            string cleaned = StripCodeFences(rawResponse);
            int firstBrace = cleaned.IndexOf('{');
            int lastBrace = cleaned.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                cleaned = cleaned.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            var rootNode = JsonNode.Parse(cleaned) as JsonObject;
            if (rootNode != null)
            {
                var stats = ParseKeyValueMapNode(rootNode["stats"]);
                if (stats.Count > 0)
                {
                    string guide = SanitizeStatusGuide(rootNode["guide"]?.GetValue<string>() ?? "");
                    var initialChars = ParseKeyValueMapNode(rootNode["initialChars"]);
                    if (lorebooks != null)
                    {
                        initialChars = NormalizeInitialCharKeys(initialChars, lorebooks);
                    }

                    var initialPlaces = ParseStringListNode(rootNode["initialPlaces"]);
                    var initialItems = ParseStringListNode(rootNode["initialItems"]);
                    var genreDirectives = ParseStringListNode(rootNode["genreDirectives"]);

                    return new StatusDesignPayload
                    {
                        Stats = stats,
                        Guide = guide,
                        InitialChars = initialChars,
                        InitialPlaces = initialPlaces,
                        InitialItems = initialItems,
                        GenreDirectives = genreDirectives
                    };
                }
            }
        }
        catch
        {
            // Fallback to LlmJsonParser below
        }

        var result = LlmJsonParser.DeserializeSafe<StatusDesignPayload>(rawResponse);
        if (result == null || result.Stats == null || result.Stats.Count == 0) return null;

        result.Guide = SanitizeStatusGuide(result.Guide);
        result.InitialChars = NormalizeInitialCharKeys(result.InitialChars, lorebooks);
        result.InitialPlaces ??= [];
        result.InitialItems ??= [];
        result.GenreDirectives ??= [];
        return result;
    }

    private static Dictionary<string, string> ParseKeyValueMapNode(JsonNode? node)
    {
        var map = new Dictionary<string, string>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject obj)
                {
                    string? name = (obj["name"] ?? obj["key"] ?? obj["Name"] ?? obj["Key"])?.ToString()?.Trim();
                    string? val = (obj["value"] ?? obj["Value"])?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(name) && val != null)
                    {
                        map[name] = val;
                    }
                }
            }
        }
        else if (node is JsonObject dictObj)
        {
            foreach (var kv in dictObj)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                {
                    map[kv.Key.Trim()] = kv.Value.ToString().Trim();
                }
            }
        }
        return map;
    }

    private static List<string> ParseStringListNode(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                string? val = item?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    list.Add(val);
                }
            }
        }
        return list;
    }

    /// <summary>
    /// `initialChars`의 Key가 로어북(`Category == "인물"`)의 `Name`과 일치하도록 정규화하고 value를 정제합니다.
    /// (예: `"특활부장 아리스"` -> `"아리스"`)
    /// </summary>
    public static Dictionary<string, string> NormalizeInitialCharKeys(
        Dictionary<string, string>? initialChars,
        IEnumerable<LorebookEntry>? lorebooks)
    {
        if (initialChars == null || initialChars.Count == 0)
            return new Dictionary<string, string>();

        var normalized = new Dictionary<string, string>();
        var charLores = lorebooks?
            .Where(l => string.Equals(l.Category?.Trim(), "인물", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(l.Name))
            .ToList() ?? [];

        foreach (var (rawKey, val) in initialChars)
        {
            if (string.IsNullOrWhiteSpace(rawKey)) continue;
            string cleanKey = rawKey.Trim();

            string resolvedKey = cleanKey;
            if (charLores.Count > 0)
            {
                // 1순위: 정확히 일치하는 로어북 인물명
                var exactMatch = charLores.FirstOrDefault(l =>
                    string.Equals(l.Name.Trim(), cleanKey, StringComparison.OrdinalIgnoreCase));

                // 2순위: 직함/수식어가 포함되거나 부분 이름이 사용된 경우 (예: "특활부장 아리스" <-> "아리스")
                var substringMatch = exactMatch ?? charLores.FirstOrDefault(l =>
                    cleanKey.Contains(l.Name.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    l.Name.Trim().Contains(cleanKey, StringComparison.OrdinalIgnoreCase));

                // 3순위: 로어북 키워드 매칭
                var keywordMatch = substringMatch ?? charLores.FirstOrDefault(l =>
                    l.Keywords != null && l.Keywords.Any(kw =>
                        !string.IsNullOrWhiteSpace(kw) &&
                        (string.Equals(kw.Trim(), cleanKey, StringComparison.OrdinalIgnoreCase) ||
                         cleanKey.Contains(kw.Trim(), StringComparison.OrdinalIgnoreCase))));

                resolvedKey = keywordMatch != null ? keywordMatch.Name.Trim() : cleanKey;
            }

            normalized[resolvedKey] = SanitizeInitialCharValue(val);
        }

        return normalized;
    }

    public (Dictionary<string, string> Stats, string Guide)? ParseStatusDesign(string rawResponse)
    {
        var payload = ParseStatusDesignPayload(rawResponse);
        if (payload == null) return null;
        return (payload.Stats, payload.Guide);
    }

    /// <summary>
    /// 100% C# 결정론적 무결성 검수 관문 (Zero-LLM-Call Deterministic Gate):
    /// 불필요한 LLM 자가 감사 호출을 완전히 배제하고, 파서·정규식·스키마 대조를 통해
    /// 구조적 누락이나 형식 결함이 있는 항목만 0.001초 내에 적발하여 외과수술식 보정을 트리거합니다.
    /// </summary>
    public Task<(bool Pass, string? Feedback)> ReviewStepContentAsync(
        AgentStep reviewStep,
        string generatedContent,
        AgentPlan plan,
        ArchitectSession session,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(generatedContent))
        {
            return Task.FromResult<(bool, string?)>((false, "생성된 컨텐츠가 비어 있습니다."));
        }

        switch (reviewStep)
        {
            case AgentStep.WorldviewReview:
            {
                string cleaned = SanitizeWorldview(generatedContent);
                if (cleaned.Length < 20)
                {
                    return Task.FromResult<(bool, string?)>((false, "세계관 설정 분량이 너무 짧습니다. 4대 번호 섹션과 하이픈 개조식 불릿으로 상세히 작성하십시오."));
                }
                return Task.FromResult<(bool, string?)>((true, null));
            }

            case AgentStep.LorebookReview:
            {
                var entries = ParseLorebooks(generatedContent);
                if (entries == null || entries.Count == 0)
                {
                    return Task.FromResult<(bool, string?)>((false, "유효한 로어북 JSON 배열로 파싱되지 않았습니다."));
                }

                var defectNames = new List<string>();
                var issues = new List<string>();

                if (plan.LorebookPlan != null && plan.LorebookPlan.Count > 0)
                {
                    foreach (var planned in plan.LorebookPlan)
                    {
                        if (string.IsNullOrWhiteSpace(planned.Name)) continue;
                        string targetName = planned.Name.Trim();
                        bool exists = entries.Any(e =>
                            string.Equals(e.Name.Trim(), targetName, StringComparison.OrdinalIgnoreCase) ||
                            e.Name.Contains(targetName, StringComparison.OrdinalIgnoreCase) ||
                            targetName.Contains(e.Name.Trim(), StringComparison.OrdinalIgnoreCase));

                        if (!exists)
                        {
                            defectNames.Add(targetName);
                            issues.Add($"기획된 '{targetName}' 항목이 누락됨");
                        }
                    }
                }

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Content) || entry.Content.Trim().Length < 10)
                    {
                        defectNames.Add(entry.Name);
                        issues.Add($"'{entry.Name}' 본문이 비어 있거나 너무 짧음");
                        continue;
                    }

                    bool isCharacter = string.Equals(entry.Category?.Trim(), "인물", StringComparison.OrdinalIgnoreCase);
                    if (isCharacter && (entry.Content.Contains("말투") || entry.Content.Contains("대사")) && !entry.Content.Contains('"'))
                    {
                        defectNames.Add(entry.Name);
                        issues.Add($"'{entry.Name}' 인물 로어북에 실제 큰따옴표(\"...\") 대표 대사 샘플이 누락됨");
                    }
                }

                if (defectNames.Count > 0)
                {
                    string distinctTargets = string.Join(", ", defectNames.Distinct(StringComparer.OrdinalIgnoreCase));
                    string issueSummary = string.Join("; ", issues.Distinct());
                    return Task.FromResult<(bool, string?)>((false, $"[수정 대상: {distinctTargets}] {issueSummary}"));
                }

                return Task.FromResult<(bool, string?)>((true, null));
            }

            case AgentStep.ScenarioReview:
            {
                string cleaned = SanitizeScenario(generatedContent);
                if (cleaned.Length < 30)
                {
                    return Task.FromResult<(bool, string?)>((false, "오프닝 시나리오 산문 분량이 너무 짧습니다. 3~4문단 분량으로 작성하십시오."));
                }
                return Task.FromResult<(bool, string?)>((true, null));
            }

            case AgentStep.StatusReview:
            {
                var payload = ParseStatusDesignPayload(generatedContent, session.GeneratedLorebooks);
                if (payload == null || payload.Stats.Count == 0 || string.IsNullOrWhiteSpace(payload.Guide))
                {
                    return Task.FromResult<(bool, string?)>((false, "상태창 초기 스탯(stats) 또는 수치 갱신 가이드(guide)가 누락되었습니다."));
                }
                return Task.FromResult<(bool, string?)>((true, null));
            }

            case AgentStep.PromptReview:
            {
                bool hasValidStructure = generatedContent.StartsWith("<system_directive>", StringComparison.Ordinal) &&
                                         generatedContent.EndsWith("</rules>", StringComparison.Ordinal);
                bool hasZeroNumericLeak = !Regex.IsMatch(generatedContent, @"(상태창\s*업데이트\s*지침|uiBadges|스탯\s*업데이트\s*가이드)", RegexOptions.IgnoreCase);
                if (hasValidStructure && hasZeroNumericLeak)
                {
                    return Task.FromResult<(bool, string?)>((true, null));
                }
                return Task.FromResult<(bool, string?)>((false, "시스템 프롬프트는 <system_directive>와 <rules> 블록으로 구성되어야 하며 상태창/스탯 업데이트 지침이 섞이지 않아야 합니다."));
            }

            default:
                return Task.FromResult<(bool, string?)>((true, null));
        }
    }

    public async Task<string> ApplyToNewSessionAsync(ArchitectSession session)
    {
        if (session.Plan == null)
            throw new InvalidOperationException("계획이 존재하지 않아 세션을 적용할 수 없습니다.");

        // 1. 세션명 결정 (SessionTitle 우선, 없으면 WorldviewOutline/Concept에서 추출)
        string sessionName = !string.IsNullOrWhiteSpace(session.Plan.SessionTitle)
            ? ExtractSessionName(session.Plan.SessionTitle)
            : ExtractSessionName(session.Plan.WorldviewOutline ?? session.Plan.Concept);

        // 2. CharacterPreset 생성 (로어북 768차원 시맨틱 임베딩 자동 부착 및 슬롯별 최종 정제)
        var rawLorebooks = session.GeneratedLorebooks ?? [];
        var embeddedLorebooks = await _lorebookService.EnsureEmbeddingsAsync(rawLorebooks);

        string? rawWorldview = !string.IsNullOrWhiteSpace(session.GeneratedWorldview)
            ? session.GeneratedWorldview
            : session.Plan.WorldviewOutline;
        string cleanWorldview = SanitizeWorldview(rawWorldview ?? "");
        string cleanStatusGuide = SanitizeStatusGuide(session.GeneratedStatusGuide ?? "");
        string cleanSystemPrompt = string.IsNullOrWhiteSpace(session.GeneratedSystemPrompt)
            ? CompileHybridSystemPrompt("", session.Plan)
            : (session.GeneratedSystemPrompt.Trim().StartsWith("<system_directive>", StringComparison.Ordinal)
                ? session.GeneratedSystemPrompt.Trim()
                : CompileHybridSystemPrompt(session.GeneratedSystemPrompt, session.Plan));

        var preset = new CharacterPreset(
            Name: sessionName,
            Worldview: cleanWorldview,
            SystemPrompt: cleanSystemPrompt,
            Temperature: null,
            MaxOutputTokens: 8192,
            Lorebooks: embeddedLorebooks,
            CustomStats: session.GeneratedStats ?? new(),
            StatusUpdateGuide: cleanStatusGuide
        );

        // 3. 파일 이름 결정 및 프리셋 저장
        string fileName = ChatDataHelper.GenerateSessionFileName(preset.Name);
        await _presetService.SavePresetAsync(fileName, preset);

        // 4. Turn 0 ChapterContext 시딩 (초기 인물 Key 정규화, 장소, 소지품 완벽 초기화)
        var normalizedChars = NormalizeInitialCharKeys(session.GeneratedInitialChars, embeddedLorebooks);
        var initialChars = normalizedChars.Count > 0
            ? normalizedChars
            : SeedFallbackInitialChars(embeddedLorebooks);

        var initialPlaces = session.GeneratedInitialPlaces != null && session.GeneratedInitialPlaces.Count > 0
            ? new List<string>(session.GeneratedInitialPlaces)
            : SeedFallbackInitialPlaces(embeddedLorebooks);

        var initialItems = session.GeneratedInitialItems != null
            ? new List<string>(session.GeneratedInitialItems)
            : [];

        string initialPlotSummary = !string.IsNullOrWhiteSpace(session.Plan.ScenarioOutline)
            ? $"[제 1장]\n* {session.Plan.ScenarioOutline.Trim()}"
            : "[제 1장]\n* 새로운 이야기가 시작되었습니다.";

        var chapterContext = new ChapterContext
        {
            Plot = initialPlotSummary,
            CustomStats = session.GeneratedStats != null ? new Dictionary<string, string>(session.GeneratedStats) : new(),
            Chars = initialChars,
            Places = initialPlaces,
            Items = initialItems
        };

        var history = new List<ChatMessage>();
        string cleanScenario = SanitizeScenario(session.GeneratedScenario ?? "");
        if (!string.IsNullOrWhiteSpace(cleanScenario))
        {
            history.Add(new ChatMessage("user", $"<initial_scenario>\n{cleanScenario}\n</initial_scenario>", DateTime.Now));
            history.Add(new ChatMessage("model", "주어진 세계관과 초기 상황의 흐름을 받아들여, 첫 발걸음을 뗄 준비를 마쳤습니다.", DateTime.Now.AddSeconds(1)));
        }

        var chatSession = new ChatSession(
            Preset: preset,
            History: history,
            CurrentContext: chapterContext,
            LongTermSummary: "아직 요약된 줄거리가 없습니다.",
            ChapterCount: 1,
            TotalTurnCount: 0,
            MediumTermUpdateCount: 0
        );

        // 5. 세션 데이터 저장 및 FullHistory 로그 초기화
        await _sessionService.SaveSessionAsync(fileName, chatSession);

        if (!string.IsNullOrWhiteSpace(cleanScenario))
        {
            await FullHistoryLogger.LogMessageAsync(fileName,
                new ChatMessage("user", $"[초기 상황]\n{cleanScenario}", DateTime.Now));
        }

        return fileName;
    }

    public async Task ApplyToExistingSessionAsync(ArchitectSession session)
    {
        if (string.IsNullOrEmpty(session.ExistingSessionFileName) || session.ExistingPreset == null)
            throw new InvalidOperationException("기존 세션 정보가 없어 적용할 수 없습니다.");

        string fileName = session.ExistingSessionFileName;

        var targetLorebooks = session.GeneratedLorebooks ?? session.ExistingPreset.Lorebooks;
        var embeddedLorebooks = await _lorebookService.EnsureEmbeddingsAsync(targetLorebooks);

        string updatedWorldview = !string.IsNullOrWhiteSpace(session.GeneratedWorldview)
            ? SanitizeWorldview(session.GeneratedWorldview)
            : session.ExistingPreset.Worldview;

        string updatedSystemPrompt = !string.IsNullOrWhiteSpace(session.GeneratedSystemPrompt)
            ? CompileHybridSystemPrompt(session.GeneratedSystemPrompt, session.Plan)
            : session.ExistingPreset.SystemPrompt;

        string updatedStatusGuide = !string.IsNullOrWhiteSpace(session.GeneratedStatusGuide)
            ? SanitizeStatusGuide(session.GeneratedStatusGuide)
            : session.ExistingPreset.StatusUpdateGuide;

        var updatedPreset = session.ExistingPreset with
        {
            Worldview = updatedWorldview,
            SystemPrompt = updatedSystemPrompt,
            Lorebooks = embeddedLorebooks,
            CustomStats = session.GeneratedStats ?? session.ExistingPreset.CustomStats,
            StatusUpdateGuide = updatedStatusGuide
        };

        await _presetService.SavePresetAsync(fileName, updatedPreset);

        var existingSession = await _sessionService.LoadSessionAsync(fileName);
        if (existingSession != null)
        {
            var currentContext = existingSession.CurrentContext;
            if (session.GeneratedStats != null)
            {
                foreach (var key in session.GeneratedStats.Keys)
                {
                    if (!currentContext.CustomStats.ContainsKey(key))
                    {
                        currentContext.CustomStats[key] = session.GeneratedStats[key];
                    }
                }
            }

            if (session.GeneratedInitialChars != null)
            {
                var normalizedInitialChars = NormalizeInitialCharKeys(session.GeneratedInitialChars, embeddedLorebooks);
                foreach (var kv in normalizedInitialChars)
                {
                    if (!currentContext.Chars.ContainsKey(kv.Key))
                    {
                        currentContext.Chars[kv.Key] = kv.Value;
                    }
                }
            }

            if (currentContext.Places.Count == 0 && session.GeneratedInitialPlaces != null)
            {
                currentContext.Places.AddRange(session.GeneratedInitialPlaces);
            }

            if (currentContext.Items.Count == 0 && session.GeneratedInitialItems != null)
            {
                currentContext.Items.AddRange(session.GeneratedInitialItems);
            }

            var history = new List<ChatMessage>(existingSession.History);
            string plot = currentContext.Plot;
            string cleanScenario = SanitizeScenario(session.GeneratedScenario ?? "");

            if (history.Count <= 2 && !string.IsNullOrWhiteSpace(cleanScenario))
            {
                history.Clear();
                history.Add(new ChatMessage("user", $"<initial_scenario>\n{cleanScenario}\n</initial_scenario>", DateTime.Now));
                history.Add(new ChatMessage("model", "주어진 세계관과 초기 상황의 흐름을 받아들여, 첫 발걸음을 뗄 준비를 마쳤습니다.", DateTime.Now.AddSeconds(1)));

                await FullHistoryLogger.ClearHistoryAsync(fileName);
                await FullHistoryLogger.LogMessageAsync(fileName,
                    new ChatMessage("user", $"[초기 상황]\n{cleanScenario}", DateTime.Now));
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
                    EventLedgers = currentContext.EventLedgers
                }
            };

            await _sessionService.SaveSessionAsync(fileName, updatedSession);
        }
    }

    #region Deterministic Format Sanitizers & Compilers (AI_Prompt_Engineering_Guidelines Enforcers)

    /// <summary>
    /// 한국어 롤플레잉 텍스트에 혼입된 데바나가리, 아랍, 태국, 키릴, 히브리 등 비정상 다국어 글리치 문자 블록을 제거합니다.
    /// </summary>
    public static string StripForeignGlitchScripts(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return Regex.Replace(raw, @"[\u0400-\u06FF\u0900-\u0E7F]+", "");
    }

    /// <summary>
    /// 인물 초기 상태 value에서 `초기 태도(...)` 고정 래퍼를 벗겨내고 핵심 상태 키워드만 남기며 다국어 글리치를 정제합니다.
    /// (예: `초기 태도(경계심), 팔짱을 낀 채 주시 중` -> `경계심, 팔짱을 낀 채 주시 중`)
    /// </summary>
    public static string SanitizeInitialCharValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string text = StripForeignGlitchScripts(raw).Trim();
        text = Regex.Replace(text, @"^초기\s*태도\s*\(([^)]+)\)\s*,\s*", "$1, ");
        text = Regex.Replace(text, @"^초기\s*태도\s*:\s*", "");
        return text.Trim();
    }

    /// <summary>
    /// `<master_setting>` 내부에 주입될 세계관 텍스트에서 XML 태그, 마크다운 헤더(#), 대괄호 섹션 헤더([]), 볼드(**), 코드펜스를 제거합니다.
    /// </summary>
    public static string SanitizeWorldview(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string text = StripForeignGlitchScripts(StripCodeFences(raw));
        // 모든 XML/HTML 형태 태그 제거 (<master_setting> 중복이나 임의 태그 오염 방지)
        text = Regex.Replace(text, @"</?[a-zA-Z0-9_:-]+(?:\s[^>]*)?>", "");
        // 마크다운 헤더 (### 제목 -> 제목) 및 대괄호 섹션 헤더 ([1. 제목] -> 1. 제목) 정제
        text = Regex.Replace(text, @"(?m)^#{1,6}\s*(.+?)\s*$", m => m.Groups[1].Value.Trim().Trim('[', ']').Trim());
        text = Regex.Replace(text, @"(?m)^\[([^\r\n\]]+)\]\s*$", "$1");
        // 마크다운 볼드/이탤릭 제거
        text = text.Replace("**", "");

        // 결정론적 줄바꿈 복원: 단일 줄로 평탄화된 번호 섹션 헤더(1~9.) 및 하이픈 불릿(- 항목명:) 앞 줄바꿈 강제 삽입
        text = Regex.Replace(text, @"(?<!^)(?<!\r?\n)\s+(?=[1-9]\.\s+[가-힣a-zA-Z])", "\n\n");
        text = Regex.Replace(text, @"(?<!^)(?<!\r?\n)\s+-\s+(?=[가-힣a-zA-Z0-9()·/_\s]{2,35}:\s)", "\n- ");

        return NormalizeBlankLines(text);
    }

    /// <summary>
    /// `<world_lore>` 내부에 `- {Name}: {Content}`로 주입될 로어북 본문에서 XML 태그, # 헤더, 대괄호 의사 태그([]), ** 볼드를 제거합니다.
    /// </summary>
    public static string SanitizeLorebookContent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string text = StripForeignGlitchScripts(StripCodeFences(raw));
        text = Regex.Replace(text, @"</?[a-zA-Z0-9_:-]+(?:\s[^>]*)?>", "");
        text = Regex.Replace(text, @"(?m)^#{1,6}\s*", "");
        text = text.Replace("**", "");
        // 첫 줄 `[인물] 설명` -> `분류(인물): 설명` 변환 및 본문 내 `[표면]`, `[내면]` 대괄호 제거
        text = Regex.Replace(text, @"^\[(인물|장소|아이템|세계관)\]\s*:?\s*", "분류($1): ");
        text = Regex.Replace(text, @"\[(표면|내면)\]\s*", "$1: ");
        return NormalizeBlankLines(text);
    }

    /// <summary>
    /// `BuildStatusRequest`의 `<rules>` 7번 항목 하위에 주입될 상태창 가이드에서 XML 태그, # 헤더, 대괄호 스탯명([]), ** 볼드를 제거합니다.
    /// </summary>
    public static string SanitizeStatusGuide(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string text = StripForeignGlitchScripts(StripCodeFences(raw));
        text = Regex.Replace(text, @"</?[a-zA-Z0-9_:-]+(?:\s[^>]*)?>", "");
        text = Regex.Replace(text, @"(?m)^#{1,6}\s*", "");
        text = text.Replace("**", "");
        text = Regex.Replace(text, @"(?m)^(-\s*)\[([^\]]+)\]\s*:\s*", "$1$2: ");
        return NormalizeBlankLines(text);
    }

    /// <summary>
    /// 오프닝 시나리오에서 마크다운 별표(*), 헤더(#), XML 태그 및 비정상 외국어 글리치를 제거하고 순수 문학적 산문과 대사(""), 독백(「」)만 남깁니다.
    /// </summary>
    public static string SanitizeScenario(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string text = StripForeignGlitchScripts(StripCodeFences(raw));
        text = Regex.Replace(text, @"</?[a-zA-Z0-9_:-]+(?:\s[^>]*)?>", "");
        text = Regex.Replace(text, @"(?m)^#{1,6}\s*", "");
        text = Regex.Replace(text, @"(?m)^>\s*", "");
        text = text.Replace("*", "");
        return NormalizeBlankLines(text);
    }

    /// <summary>
    /// 코드 고정 헌법(1~4번)과 세션 맞춤형 GM 수칙(5~6번)을 순수 XML(`<system_directive>` + `<rules>`) 및 콜론(`:`) 포맷으로 통합 컴파일합니다.
    /// 대괄호(`[ ]`) 의사 태그와 스탯/상태창 갱신 관련 지침을 물리적으로 필터링하여 원칙 1, 2(Zero-Numeric)를 100% 보장합니다.
    /// </summary>
    public static string CompileHybridSystemPrompt(string? rawGenreDirectives, AgentPlan? plan = null)
    {
        string cleanedGenre = StripCodeFences(rawGenreDirectives ?? "");
        cleanedGenre = Regex.Replace(cleanedGenre, @"</?(?:system|system_directive|rules)>", "", RegexOptions.IgnoreCase);
        cleanedGenre = Regex.Replace(cleanedGenre, @"</?[a-zA-Z0-9_:-]+(?:\s[^>]*)?>", "");
        cleanedGenre = cleanedGenre.Replace("**", "");

        // Zero-Numeric 및 고정 헤더(Role, 1~4번 기본 수칙) 중복 라인 물리적 필터링
        var rawLines = cleanedGenre
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Where(l => !l.StartsWith("[Role]", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("Role:", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.Equals("[Core Directives]", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.Equals("[Narrative Style & Genre Direction]", StringComparison.OrdinalIgnoreCase))
            .Where(l => !Regex.IsMatch(l, @"^[1-3]\.\s*(?:\[?(?:감각적|PC\s*통제|마이크로\s*템포|NPC\s*자율성)|추상적\s*감정|유저\s*캐릭터|시간\s*임의\s*요약|무조건적\s*호의|작위적)", RegexOptions.IgnoreCase))
            .Where(l => !Regex.IsMatch(l, @"(상태창\s*업데이트|스탯\s*업데이트|uiBadges|클램핑|Clamping|수치\s*증감)", RegexOptions.IgnoreCase))
            .ToList();

        var numberedLines = new List<string>();
        int nextNumber = 4;

        foreach (var line in rawLines)
        {
            string body = Regex.Replace(line, @"^\d+\.\s*", "").Trim();
            if (string.IsNullOrWhiteSpace(body)) continue;
            // `[항목명]: 내용`, `[항목명] 내용`, 또는 `항목명: 내용` 형태의 불필요한 앞부분 소제목 라벨을 제거하고 본문 명령문만 남김
            body = Regex.Replace(body, @"^\[[^\]]+\]\s*:?\s*", "");
            body = Regex.Replace(body, @"^[가-힣a-zA-Z0-9\s·/]{2,18}:\s+", "");
            if (string.IsNullOrWhiteSpace(body)) continue;
            numberedLines.Add($"{nextNumber}. {body}");
            nextNumber++;
        }

        if (numberedLines.Count == 0 && plan != null)
        {
            if (!string.IsNullOrWhiteSpace(plan.SurfaceTone) || !string.IsNullOrWhiteSpace(plan.CoreWeight))
            {
                numberedLines.Add($"{nextNumber++}. 평소에는 주변 환경과 NPC 대화에서 {plan.SurfaceTone}의 분위기를 유지하되, 결정적 계기나 위기 발생 시 {plan.CoreWeight}의 긴장감과 환경적 압박감으로 대비시킬 것(단, PC의 태도·행동은 유저 선택에 맡길 것).");
            }
            if (!string.IsNullOrWhiteSpace(plan.PromptOutline))
            {
                numberedLines.Add($"{nextNumber++}. {plan.PromptOutline}");
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(CoreSystemDirectivesHeader.Trim());

        foreach (var line in numberedLines)
        {
            sb.AppendLine(line);
        }

        sb.Append("</rules>");
        return sb.ToString();
    }

    public static List<string> NormalizeKeywords(string entryName, IEnumerable<string>? rawKeywords)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(entryName))
        {
            result.Add(entryName.Trim());
            // 공백으로 구분된 이름(예: "세실리아 본 로젠버그")이면 첫 어절("세실리아")도 자동 키워드 등록
            var nameParts = entryName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (nameParts.Length > 1 && nameParts[0].Length >= 2)
            {
                result.Add(nameParts[0]);
            }
        }

        if (rawKeywords != null)
        {
            foreach (var kw in rawKeywords)
            {
                if (string.IsNullOrWhiteSpace(kw)) continue;
                string cleaned = kw.Trim().Trim('#', '*', '"', '\'');
                if (cleaned.Length >= 2)
                {
                    result.Add(cleaned);
                }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    private static Dictionary<string, string> SeedFallbackInitialChars(List<LorebookEntry> lorebooks)
    {
        var dict = new Dictionary<string, string>();
        foreach (var charLore in lorebooks.Where(l => l.Category == "인물").OrderBy(l => l.Priority).Take(2))
        {
            string firstLine = charLore.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "등장인물";
            dict[charLore.Name] = $"초기 상태: {firstLine}";
        }
        return dict;
    }

    private static List<string> SeedFallbackInitialPlaces(List<LorebookEntry> lorebooks)
    {
        return lorebooks
            .Where(l => l.Category == "장소")
            .OrderBy(l => l.Priority)
            .Take(1)
            .Select(l => l.Name)
            .ToList();
    }

    private static string StripCodeFences(string text)
    {
        string trimmed = text.Trim();
        trimmed = Regex.Replace(trimmed, @"^```[a-zA-Z0-9_-]*\s*", "");
        trimmed = Regex.Replace(trimmed, @"\s*```$", "");
        return trimmed.Trim();
    }

    private static string NormalizeBlankLines(string text)
    {
        string normalized = text.Replace("\r\n", "\n").Trim();
        return Regex.Replace(normalized, @"\n{3,}", "\n\n");
    }

    private static string ExtractSessionName(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "AI 생성 세션";

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

        string clean = text.Replace("\r", "").Replace("\n", " ").Trim();
        if (clean.Length > 24)
        {
            return clean.Substring(0, 21).Trim() + "...";
        }
        return clean;
    }

    #endregion
}
