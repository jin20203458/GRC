using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
                        keywords = new { type = "ARRAY", items = new { type = "STRING" } },
                        priority = new { type = "INTEGER" }
                    },
                    required = new[] { "name", "category", "brief", "surfaceRole", "innerDepth", "keywords", "priority" }
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

    private static readonly object ReviewResponseSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            pass = new { type = "BOOLEAN" },
            issues = new { type = "STRING", nullable = true }
        },
        required = new[] { "pass" }
    };

    /// <summary>
    /// AI_Prompt_Engineering_Guidelines.md 원칙 1, 2, 16을 코드 레벨에서 100% 보장하는 불변 상단 헌법 (순수 XML + 직결 명령문 체계)
    /// </summary>
    public const string CoreSystemDirectivesHeader = """
<system_directive>
Role: 3인칭 전지적 게임 마스터
</system_directive>

<rules>
1. 추상적 감정 라벨링 및 시스템 수치 언급 배제. 오감(시·청·후각 등)에 기반한 물리적 행동, 시선, 어조, 환경 변화로만 객관적 서술.
2. 유저 캐릭터(PC)의 대사, 행동, 내면의 생각이나 감정을 절대 임의로 서술하거나 단정하지 말 것.
3. 시간 임의 요약(Time-skip) 금지. 단일 사건이나 NPC 반응 직후 즉시 서술을 멈추고 유저가 행동할 '직면 상황'에서 턴 종료.
4. 무조건적 호의나 작위적 성공 배제. 인물의 가치관·개연성에 어긋날 시 거절·경계·적대 반응을 보이며 교훈적 요약으로 끝맺지 말 것.
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
Task: <user_concept>을 분석하여 롤플레잉 세션 설계 청사진(AgentPlan)을 JSON으로 수립하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 평소 캐릭터 간의 대화 템포와 일상 분위기(surfaceTone) 및 세계관 기저의 핵심 갈등 무게감(coreWeight)을 각각 정의하되, 복합 톤 컨셉이면 두 축의 대비를 설계하고 단일 톤 컨셉이면 억지 이면 설정 없이 본연의 장르 구도에 집중하십시오.
2. 미래에 일어날 줄거리나 결말을 미리 정해두는 대신, Turn 0 시점의 세계 법칙, 세력 구도, 주인공(PC)의 초기 입지(pcInitialPosition), 인물별 입체적 동기만 정의하십시오.
3. 로어북은 주요 인물(3~4명), 핵심 장소(1~2곳), 핵심 세력/세계관 규칙/아이템(2~3개) 등 총 6~9개 항목으로 기획하고, 각 인물의 대외적 모습(surfaceRole)과 내면의 동기·결핍·갭 모에(innerDepth)를 구분하십시오.
4. priority는 오름차순 우선순위(0이 최우선) 규격에 따라 0(핵심 주연/절대 법칙), 1(주요 조연/시작 거점/주요 세력), 2(일반 장소/세력), 3(배경/보조 아이템)으로 부여하고, keywords는 조사가 제거된 순수 고유명사·직함·별칭 4~6개로 구성하십시오.
5. <output_format>의 TypeScript 인터페이스 규격에 부합하는 순수 JSON 객체만 출력하십시오.
</rules>

<output_format>
interface LorebookPlanItem {
  name: string;        // 항목 고유 명칭 (인물명, 장소명 등)
  category: '인물' | '장소' | '아이템' | '세계관';
  brief: string;       // 핵심 정의 1줄 요약
  surfaceRole: string; // 외양, 표면적 위상 및 평소 대외적 태도
  innerDepth: string;  // 내면의 속마음, 결핍, 진짜 동기, 갭 모에 또는 서사적 훅
  keywords: string[];  // 검색 트리거용 순수 명사 키워드 (4~6개)
  priority: number;    // 0(최우선 핵심) | 1(주요) | 2(일반) | 3(배경)
}

interface AgentPlan {
  sessionTitle: string;             // 세션 고유 제목
  surfaceTone: string;              // 평소 대화와 상호작용의 호흡 및 분위기 정의
  coreWeight: string;               // 세계관과 핵심 갈등의 무게감 및 서사적 동력 정의
  pcInitialPosition: string;        // 주인공(PC)의 시작 신분, 처한 상황, 당면한 과제 정의
  worldviewOutline: string;         // 시대/공간 배경, 핵심 시스템/법칙, 세력 구도 아웃라인
  lorebookPlan: LorebookPlanItem[]; // 6~9개 로어북 기획 항목 배열
  statsPlan: string[];              // 핵심 추적 스탯 또는 주요 인물 관계도 명칭 (4~6개)
  scenarioOutline: string;          // 오프닝 장면의 장소, 등장인물, 주인공의 첫 행동 선택 상황
  promptOutline: string;            // 장르적 문체, 감각 묘사 기준, 대화 템포 연출 지침
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
위 <user_concept>을 바탕으로 <output_format>의 AgentPlan 인터페이스 규격에 부합하는 순수 JSON을 출력하십시오.
</final_instruction>
""";

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(systemInstructionText)]),
            Contents: [new Content("user", [new Part(userPrompt)])],
            SafetySettings: null,
            GenerationConfig: new GenerationConfig(null, 8192, "application/json", AgentPlanResponseSchema, new ThinkingConfig(ThinkingLevel.high))
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
        // Stage 3 (LorebookGen): 출력 압축(Lazy Compression) 방지를 위해 마이크로 배치 병렬 생성 수행
        if (step == AgentStep.LorebookGen && string.IsNullOrWhiteSpace(userFeedback) && plan.LorebookPlan.Count > 0)
        {
            string mergedLorebooksJson = await GenerateLorebooksInParallelAsync(plan, session.GeneratedWorldview ?? "", ct);
            yield return mergedLorebooksJson;
            yield break;
        }

        // Stage 6 (PromptGen): 코드 고정 헌법(Core Directives) + Pro 장르 디렉팅 결합
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
            GenerationConfig: new GenerationConfig(null, 8192, responseMimeType, responseSchema, new ThinkingConfig(ThinkingLevel.high))
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
Task: 기획안(AgentPlan)을 바탕으로 런타임 <master_setting> 슬롯에 주입될 마스터 세계관 레퍼런스를 작성하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 전체 텍스트는 번호 섹션 헤더(`1. 섹션명`)와 하이픈 개조식 불릿(`- 항목명: 내용`)으로만 구성하고, 서술어 어미(`~이다`, `~한다`)를 덜어내어 명사구와 핵심 키워드 중심의 1줄 개조식(Telegraphic Compression)으로 작성하십시오.
2. 개별 등장인물의 상세 프로필은 별도 로어북(`<world_lore>`)에서 전담하므로, 본 문서에는 아래 4대 거시 섹션(각 섹션당 하위 불릿 3~4개 이내)만 기술하십시오:
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
                string loreSummary = string.Join("\n", plan.LorebookPlan.Where(l => l.Category != "인물").Select(l => $"- {l.Name} ({l.Category}): {l.Brief} / 표면: {l.SurfaceRole} / 내면: {l.InnerDepth}"));
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
                string loreListJson = JsonSerializer.Serialize(plan.LorebookPlan, JsonWriteOptions);
                string usr = $$"""
<tone_architecture>
- 일상/표면 호흡: {{plan.SurfaceTone}}
- 배경/갈등 무게감: {{plan.CoreWeight}}
</tone_architecture>

<worldview>
{{session.GeneratedWorldview}}
</worldview>

<target_lorebook_items>
{{loreListJson}}
</target_lorebook_items>{{feedbackBlock}}

<final_instruction>
위 <target_lorebook_items>의 모든 항목을 <output_format>의 LorebookEntry[] 규격에 맞춘 순수 JSON 배열로 출력하십시오.
</final_instruction>
""";
                return (sys, usr, "application/json", LorebookArrayResponseSchema);
            }

            case AgentStep.StatusGen:
            {
                string sys = """
<system_directive>
Domain: TRPG State & Turn-0 Context Initialization Engine
Task: 세계관과 로어북을 바탕으로 상태창 스탯(stats), 상태창 갱신 규칙(guide), Turn 0 초기 상태 데이터(initialChars, initialPlaces, initialItems)를 JSON으로 설계하십시오.
Output Language: Korean
</system_directive>

<rules>
1. stats는 계획된 스탯 목록을 기반으로 주인공의 핵심 상태 수치와 주요 등장인물 관계도/호감도 초기치를 설정하되, 범위형은 "50/100", 단일 수치는 "100", 상태형은 "평온" 형식으로 표기하십시오.
2. guide는 상태창 갱신 모델(`BuildStatusRequest`)의 `<rules>` 하위에 직접 주입되므로, 순수 일반 텍스트와 하이픈 개조식(`- 스탯명: 상승 조건(+수치폭) / 하락 조건(-수치폭) / 유효 범위`)으로만 작성하십시오.
3. initialChars는 오프닝 시나리오에 등장하거나 주인공과 직결된 주요 NPC(1~3명)의 이름을 Key(로어북 인물명과 일치)로 등록하고, Value는 `"초기 태도(주인공을 향한 초기 태도 및 관계 정의), 현재 상태 및 외양 요약"` 형식으로 작성하십시오.
4. initialPlaces는 오프닝 시나리오가 시작되는 구체적인 장소명 배열(1~2개), initialItems는 주인공이 시작 시점에 소지하고 있는 핵심 소지품/장비 배열(1~4개)로 구성하십시오.
5. <output_format>의 TypeScript 인터페이스 규격에 부합하는 순수 JSON 객체만 출력하십시오.
</rules>

<output_format>
interface StatusDesignPayload {
  stats: Record<string, string>;        // 초기 스탯 및 관계도 맵
  guide: string;                        // 상태창 갱신 전용 개조식 가이드
  initialChars: Record<string, string>; // Turn 0 등장인물 초기 상태 맵
  initialPlaces: string[];              // Turn 0 시작 장소 배열
  initialItems: string[];               // Turn 0 초기 소지품 배열
}
</output_format>

<example>
{
  "stats": {
    "체력": "100/100",
    "인물명 신뢰도": "30/100"
  },
  "guide": "- 체력: 부상이나 과로 시 -5~-30 감소, 휴식이나 치료 시 +10~+50 회복 (0~100 범위 유지)\n- 인물명 신뢰도: 가치관에 부합하는 배려나 협력 시 +2~+10 상승, 기만이나 무례한 언행 시 -5~-20 하락 (0~100 범위 유지)",
  "initialChars": {
    "인물명": "초기 태도(낯선 이방인에 대한 경계와 호기심), 단정한 제복 차림으로 팔짱을 낀 채 주인공을 관찰 중"
  },
  "initialPlaces": [
    "시작 장소명"
  ],
  "initialItems": [
    "초기 소지품 1",
    "초기 소지품 2"
  ]
}
</example>
""";
                string statsListJson = JsonSerializer.Serialize(plan.StatsPlan, JsonWriteOptions);
                string loreSummary = session.GeneratedLorebooks != null
                    ? string.Join("\n", session.GeneratedLorebooks.Select(l => $"- {l.Name} ({l.Category}): {l.Content}"))
                    : "";

                string usr = $$"""
<worldview>
{{session.GeneratedWorldview}}
</worldview>

<lorebooks>
{{loreSummary}}
</lorebooks>

<stats_plan>
{{statsListJson}}
</stats_plan>

<scenario_outline>
{{plan.ScenarioOutline}}
</scenario_outline>{{feedbackBlock}}

<final_instruction>
위 데이터를 바탕으로 <output_format>의 StatusDesignPayload 규격에 부합하는 순수 JSON을 출력하십시오.
</final_instruction>
""";
                return (sys, usr, "application/json", null);
            }

            case AgentStep.ScenarioGen:
            {
                string sys = """
<system_directive>
Role: 3인칭 전지적 게임 마스터 (Omniscient Game Master)
Task: 확정된 세계관, 로어북 전문, Turn 0 초기 상태를 반영하여 세션의 첫 장면(오프닝 시나리오)을 한국어 문학 산문으로 집필하십시오.
</system_directive>

<rules>
1. 본문은 순수 일반 텍스트 산문, 외부 발화용 큰따옴표(`"..."`), 내면 독백 및 강조용 낫표(`「...」`)로만 구성하고, 스탯 수치 언급 없이 오감에 기반한 물리적 묘사로만 전달하십시오.
2. 시작 장소의 공기, 조명, 소리, 냄새 등 감각적 배경 묘사로 장면을 시작하고, 등장 NPC의 외양과 어조를 `<lorebooks>`의 발화 특징 및 대사 샘플과 일치시키십시오.
3. 주인공(PC)의 대사, 의사결정, 감정 단정을 임의로 서술하지 말고, NPC가 말을 건네거나 사건이 발생하여 주인공이 첫 행동을 선택해야 하는 직면 순간에서 3~4문단 분량으로 멈추십시오.
</rules>
""";
                string fullLorebooks = session.GeneratedLorebooks != null && session.GeneratedLorebooks.Count > 0
                    ? string.Join("\n\n", session.GeneratedLorebooks.Select(l => $"- {l.Name} ({l.Category}): {l.Content}"))
                    : "없음";
                string initialCharsText = session.GeneratedInitialChars != null && session.GeneratedInitialChars.Count > 0
                    ? string.Join("\n", session.GeneratedInitialChars.Select(kv => $"- {kv.Key}: {kv.Value}"))
                    : "없음";
                string initialPlacesText = session.GeneratedInitialPlaces != null && session.GeneratedInitialPlaces.Count > 0
                    ? string.Join(", ", session.GeneratedInitialPlaces)
                    : "미정";
                string initialItemsText = session.GeneratedInitialItems != null && session.GeneratedInitialItems.Count > 0
                    ? string.Join(", ", session.GeneratedInitialItems)
                    : "없음";

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

<turn0_state>
- 시작 장소: {{initialPlacesText}}
- 등장인물 초기 상태:
{{initialCharsText}}
- 초기 소지품: {{initialItemsText}}
</turn0_state>

<scenario_outline>
{{plan.ScenarioOutline}}
</scenario_outline>{{feedbackBlock}}

<final_instruction>
위 설정과 Turn 0 초기 상태를 반영하여 주인공이 첫 응답을 내놓아야 하는 직면 순간까지 3~4문단의 순수 산문으로 오프닝 시나리오를 작성하십시오.
</final_instruction>
""";
                return (sys, usr, "text/plain", null);
            }

            default:
                return ("", "", "text/plain", null);
        }
    }

    private static string GetLorebookBatchSystemInstruction() => """
<system_directive>
Domain: High-Density Lorebook Entity Synthesis Engine
Task: 할당된 로어북 기획 항목들을 개조식 명사구 압축(Telegraphic Compression) 포맷의 JSON 배열로 작성하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 런타임에 각 항목은 `<world_lore>` 슬롯 내부에 `- {name}: {content}` 형태로 주입되므로, 서술어 어미(`~이다`, `~한다`)와 접속사를 배제하고 핵심 명사구 위주의 개조식 단문으로 압축하십시오.
2. `content`는 순수 일반 텍스트, 줄바꿈(`\n`), 들여쓰기 불릿(`  * 항목명: 내용`), 대사용 큰따옴표(`"..."`)로만 구성하며, 각 하위 불릿은 줄바꿈 없이 1줄로 작성하십시오.
3. category가 '인물'인 경우 아래 4줄 불릿 구조로 작성하십시오:
   `분류(인물): 신분/소속 및 핵심 정체성 한 줄 정의`
   `  * 외양/특징: 핵심 시각적 인장, 복장, 습관적 행동/버릇 (명사구 나열)`
   `  * 표면/내면: 표면(대외적 태도·역할) / 내면(친밀·위기 시 드러나는 속마음, 결핍, 갭 모에)`
   `  * PC 관계: 초기 인식 및 태도 변화 조건`
   `  * 말투/대사: 어조 특징 요약. "일상 대표 대사 샘플" / "본심·감정 노출 시 대사 샘플" (실제 큰따옴표 대사 2개 필수)`
4. category가 '장소', '아이템', '세계관'인 경우 아래 3줄 불릿 구조로 작성하십시오:
   `분류(카테고리): 핵심 정의 한 줄 요약`
   `  * 감각/외관: 시·청·후각적 핵심 환경/외형 디테일 (명사구 중심)`
   `  * 규칙/원리: 고유 법칙, 규율, 작동 원리 및 제약/대가`
   `  * 서사 훅: 상호작용 시 발생 가능한 사건, 갈등 요소 또는 숨겨진 이면`
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
    "content": "분류(인물): 핵심 정의\n  * 외양/특징: 명사구 중심 시각 특징, 버릇\n  * 표면/내면: 표면(대외적 모습) / 내면(실제 속마음과 갭 모에)\n  * PC 관계: 초기 태도 및 변화 조건\n  * 말투/대사: 어조 요약. \"일상 대사 샘플\" / \"본심 대사 샘플\"",
    "category": "인물",
    "priority": 0
  }
]
</example>
""";

    /// <summary>
    /// 로어북 출력 압축(Lazy Compression)을 원천 방지하기 위해 기획된 항목을 최대 3개씩 마이크로 배치로 분할하여 Pro 모델로 병렬 생성합니다.
    /// </summary>
    private async Task<string> GenerateLorebooksInParallelAsync(
        AgentPlan plan,
        string worldview,
        CancellationToken ct)
    {
        const int batchSize = 3;
        var batches = plan.LorebookPlan
            .Select((item, index) => new { item, index })
            .GroupBy(x => x.index / batchSize)
            .Select(g => g.Select(x => x.item).ToList())
            .ToList();

        string systemInstructionText = GetLorebookBatchSystemInstruction();

        var batchTasks = batches.Select(async batch =>
        {
            string batchJson = JsonSerializer.Serialize(batch, JsonWriteOptions);
            string userPrompt = $$"""
<tone_architecture>
- 일상/표면 호흡: {{plan.SurfaceTone}}
- 배경/갈등 무게감: {{plan.CoreWeight}}
</tone_architecture>

<worldview>
{{worldview}}
</worldview>

<target_lorebook_items>
{{batchJson}}
</target_lorebook_items>

<final_instruction>
위 <target_lorebook_items>의 각 항목을 빠짐없이 <output_format>의 LorebookEntry 배열(JSON)로 작성하여 출력하십시오.
</final_instruction>
""";
            var request = new GeminiRequest(
                SystemInstruction: new Content("system", [new Part(systemInstructionText)]),
                Contents: [new Content("user", [new Part(userPrompt)])],
                SafetySettings: null,
                GenerationConfig: new GenerationConfig(null, 8192, "application/json", LorebookArrayResponseSchema, new ThinkingConfig(ThinkingLevel.high))
            );

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Pro, ct))
            {
                sb.Append(chunk);
            }

            var parsedBatch = ParseLorebooks(sb.ToString());
            return parsedBatch ?? [];
        });

        var batchResults = await Task.WhenAll(batchTasks);
        var mergedEntries = batchResults
            .SelectMany(x => x)
            .DistinctBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(mergedEntries, JsonWriteOptions);
    }

    /// <summary>
    /// Stage 6: `<rules>` 하단(5~6번)에 통합될 세션 특화 GM 운영/판정 수칙 2줄만 경량 개조식으로 생성합니다.
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
Task: 시스템 프롬프트의 `<rules>` 5번과 6번 항목으로 통합될 세션 맞춤형 GM 운영 및 판정 수칙 2줄을 개조식으로 작성하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 개별 등장인물의 이름/프로필(`<world_lore>` 전담)이나 정적인 배경 풍경 나열(`<master_setting>` 전담), 스탯 수치 규칙을 일절 포함하지 마십시오.
2. 기본 GM 수칙(1~4번: 오감 묘사, PC 통제 금지, 마이크로 템포, NPC 자율성)은 상단에 이미 고정되어 있으므로, 소제목 라벨 없이 아래 5번·6번 본문 지시문 2줄만 각 1문장 개조식으로 작성하십시오:
   5. 평소 일상 호흡(surfaceTone) 유지 기준과 결정적 계기 시 핵심 갈등/이면(coreWeight)으로 전환하는 온도차 연출 수칙 (1줄).
   6. 이 세계관의 핵심 법칙과 제약에 기반하여 유저 행동의 개연성, 대가(Risk), 환경 반응을 판정하는 수칙 (1줄).
</rules>
""";

        string feedbackBlock = !string.IsNullOrWhiteSpace(userFeedback)
            ? $"\n\n<user_feedback>\n{userFeedback}\n</user_feedback>"
            : "";

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
</worldview>{{feedbackBlock}}

<final_instruction>
위 세계관과 톤 설계를 반영하여 `<rules>`에 추가될 5번과 6번 GM 수칙 2줄만 순수 일반 텍스트로 출력하십시오.
</final_instruction>
""";

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(systemInstructionText)]),
            Contents: [new Content("user", [new Part(userPrompt)])],
            SafetySettings: null,
            GenerationConfig: new GenerationConfig(null, 8192, "text/plain", null, new ThinkingConfig(ThinkingLevel.high))
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
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string systemInstructionText;
        string responseMimeType = "text/plain";
        object? responseSchema = null;

        switch (step)
        {
            case AgentStep.PlanReview:
                systemInstructionText = """
<system_directive>
Domain: Narrative Session Architecture & Worldbuilding Blueprint Engine
Task: <user_feedback>을 반영하여 <previous_content>의 설계 계획(AgentPlan) JSON을 수정하십시오.
Output Language: Korean
</system_directive>
<rules>
- 기존 JSON 스키마(sessionTitle, surfaceTone, coreWeight, pcInitialPosition, worldviewOutline, lorebookPlan, statsPlan, scenarioOutline, promptOutline)를 유지하면서 피드백을 반영하십시오.
- 순수 JSON 객체만 출력하십시오.
</rules>
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

            case AgentStep.LorebookReview:
                systemInstructionText = """
<system_directive>
Domain: High-Density Lorebook Entity Synthesis Engine
Task: <user_feedback>을 반영하여 <previous_content>의 로어북 JSON 배열을 수정하십시오.
Output Language: Korean
</system_directive>
<rules>
- `content`는 순수 일반 텍스트, 줄바꿈(`\n`), 들여쓰기 불릿(`  * `), 큰따옴표(`"..."`) 대사로만 구성하십시오.
- 서술어 어미(`~이다`, `~한다`)를 배제하고, 각 하위 불릿을 핵심 키워드 중심의 1줄 명사구 개조식으로 압축하십시오.
- 인물 항목에는 외양/특징, 표면/내면, PC 관계, 말투/대사(`"..."` 실제 대사 2개)를 포함하고, `priority`는 0~3 범위(낮을수록 최우선)를 유지하십시오.
- 전체 로어북 리스트를 순수 JSON 배열로만 출력하십시오.
</rules>
""";
                responseMimeType = "application/json";
                responseSchema = LorebookArrayResponseSchema;
                break;

            case AgentStep.StatusReview:
                systemInstructionText = """
<system_directive>
Domain: TRPG State & Turn-0 Context Initialization Engine
Task: <user_feedback>을 반영하여 <previous_content>의 상태창 및 Turn 0 초기 상태 JSON을 수정하십시오.
Output Language: Korean
</system_directive>
<rules>
- 기존 JSON 구조(`stats`, `guide`, `initialChars`, `initialPlaces`, `initialItems`)를 모두 유지하며 변경 사항을 반영하십시오.
- `guide`는 순수 일반 텍스트와 `- 스탯명: 증감 조건 및 수치 폭` 형식의 개조식으로만 작성하십시오.
- 순수 JSON 객체만 출력하십시오.
</rules>
""";
                responseMimeType = "application/json";
                break;

            case AgentStep.ScenarioReview:
                systemInstructionText = """
<system_directive>
Role: 3인칭 전지적 게임 마스터 (Omniscient Game Master)
Task: <user_feedback>을 반영하여 <previous_content>의 오프닝 시나리오를 수정하십시오.
</system_directive>
<rules>
- 본문은 순수 문학적 산문, 외부 발화용 큰따옴표(`"..."`), 독백용 낫표(`「...」`)로만 구성하십시오.
- 주인공(PC)의 대사나 행동을 임의로 결정하지 말고, 주인공이 즉각 반응해야 하는 직면 상황에서 서술을 멈추십시오.
</rules>
""";
                break;

            case AgentStep.PromptReview:
                systemInstructionText = """
<system_directive>
Domain: Narrative Style & Genre Directing Specification Engine
Task: <user_feedback>을 반영하여 <previous_content>의 `<rules>` 하단 세션 특화 수칙(5~6번)을 수정하십시오.
Output Language: Korean
</system_directive>
<rules>
- 개별 인물 프로필이나 정적 배경 나열, 스탯 수치 규칙을 배제하고, GM의 장르 호흡 조율(5번) 및 세계관 고유 판정 원칙(6번)만 각 1줄 개조식 일반 텍스트로 작성하십시오(Zero-Numeric).
</rules>
""";
                break;

            default:
                yield break;
        }

        string userPrompt = $$"""
<previous_content>
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
            GenerationConfig: new GenerationConfig(null, 8192, responseMimeType, responseSchema, new ThinkingConfig(ThinkingLevel.high))
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

    public AgentPlan? ParsePlan(string rawResponse)
    {
        var plan = LlmJsonParser.DeserializeSafe<AgentPlan>(rawResponse);
        if (plan == null) return null;

        // 필수 필드 정규화 및 Priority 보정 (0=최우선 핵심, 1=주요, 2=일반, 3=배경)
        if (plan.LorebookPlan != null)
        {
            foreach (var item in plan.LorebookPlan)
            {
                item.Priority = NormalizePriority(item.Priority);
                item.Keywords = NormalizeKeywords(item.Name, item.Keywords);
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

    public StatusDesignPayload? ParseStatusDesignPayload(string rawResponse)
    {
        var result = LlmJsonParser.DeserializeSafe<StatusDesignPayload>(rawResponse);
        if (result == null || result.Stats == null || result.Stats.Count == 0) return null;

        result.Guide = SanitizeStatusGuide(result.Guide);
        result.InitialChars ??= new Dictionary<string, string>();
        result.InitialPlaces ??= [];
        result.InitialItems ??= [];
        return result;
    }

    public (Dictionary<string, string> Stats, string Guide)? ParseStatusDesign(string rawResponse)
    {
        var payload = ParseStatusDesignPayload(rawResponse);
        if (payload == null) return null;
        return (payload.Stats, payload.Guide);
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
        if (string.IsNullOrWhiteSpace(generatedContent))
        {
            return (false, "생성된 컨텐츠가 비어 있습니다.");
        }

        string prompt;
        switch (reviewStep)
        {
            case AgentStep.WorldviewReview:
                prompt = $$"""
<design_plan>
- 일상 호흡: {{plan.SurfaceTone}}
- 핵심 무게감: {{plan.CoreWeight}}
- 세계관 개요: {{plan.WorldviewOutline}}
</design_plan>

<content_to_review>
{{generatedContent}}
</content_to_review>

<evaluation_criteria>
1. 세계의 법칙, 세력 구도, 주인공의 초기 입지가 구체적으로 작성되었는가?
2. 미래의 줄거리 각본 대신 Turn 0 시점의 무대와 법칙을 정의했는가?
3. 번호 섹션 헤더와 하이픈 개조식 불릿 포맷으로 구성되었는가?
</evaluation_criteria>

<final_instruction>
위 <content_to_review>가 <evaluation_criteria>를 충족하는지 검증하여 <output_format>의 ReviewResponse JSON을 출력하십시오.
</final_instruction>
""";
                break;

            case AgentStep.LorebookReview:
                string lorePlanJson = JsonSerializer.Serialize(plan.LorebookPlan, JsonWriteOptions);
                prompt = $$"""
<design_plan>
{{lorePlanJson}}
</design_plan>

<content_to_review>
{{generatedContent}}
</content_to_review>

<evaluation_criteria>
1. 계획된 핵심 로어북 항목들이 누락 없이 유효한 JSON 배열로 생성되었는가?
2. '인물' 카테고리 항목에 외양/특징, 표면/내면, PC 관계, 실제 큰따옴표 대표 대사 샘플이 포함되어 있는가?
3. 각 하위 불릿이 개조식 명사구로 압축되어 작성되었는가?
</evaluation_criteria>

<final_instruction>
위 <content_to_review>가 <evaluation_criteria>를 충족하는지 검증하여 <output_format>의 ReviewResponse JSON을 출력하십시오.
</final_instruction>
""";
                break;

            case AgentStep.StatusReview:
                string statsPlanJoined = string.Join(", ", plan.StatsPlan);
                prompt = $$"""
<design_plan>
{{statsPlanJoined}}
</design_plan>

<content_to_review>
{{generatedContent}}
</content_to_review>

<evaluation_criteria>
1. stats와 guide가 유효한 JSON으로 존재하며 각 스탯의 증감 폭과 범위가 명시되었는가?
2. guide 내부가 하이픈 개조식 상태 갱신 규칙으로만 구성되었는가?
</evaluation_criteria>

<final_instruction>
위 <content_to_review>가 <evaluation_criteria>를 충족하는지 검증하여 <output_format>의 ReviewResponse JSON을 출력하십시오.
</final_instruction>
""";
                break;

            case AgentStep.ScenarioReview:
                prompt = $$"""
<design_plan>
{{plan.ScenarioOutline}}
</design_plan>

<content_to_review>
{{generatedContent}}
</content_to_review>

<evaluation_criteria>
1. 순수 문학적 산문과 큰따옴표("") 대사, 낫표(「」) 독백으로 구성되었는가?
2. 주인공(PC)의 대사나 의사결정을 임의로 서술하지 않았는가?
3. 마지막 문장이 주인공의 첫 행동이나 응답을 요구하는 직면 상황에서 끝났는가?
</evaluation_criteria>

<final_instruction>
위 <content_to_review>가 <evaluation_criteria>를 충족하는지 검증하여 <output_format>의 ReviewResponse JSON을 출력하십시오.
</final_instruction>
""";
                break;

            case AgentStep.PromptReview:
                // 코드 하이브리드 컴파일러를 통과한 프롬프트는 결정론적 검증으로 즉시 패스 판정
                bool hasValidStructure = generatedContent.StartsWith("<system_directive>", StringComparison.Ordinal) &&
                                         generatedContent.EndsWith("</rules>", StringComparison.Ordinal);
                bool hasZeroNumericLeak = !Regex.IsMatch(generatedContent, @"(상태창\s*업데이트\s*지침|uiBadges|스탯\s*업데이트\s*가이드)", RegexOptions.IgnoreCase);
                if (hasValidStructure && hasZeroNumericLeak)
                {
                    return (true, null);
                }
                return (false, "시스템 프롬프트는 <system_directive>와 <rules> 블록으로 구성되어야 하며 상태창/스탯 업데이트 지침이 섞이지 않아야 합니다.");

            default:
                return (true, null);
        }

        string systemInstruction = """
<system_directive>
Domain: Narrative Session QA & Prompt Engineering Compliance Auditor
Task: <content_to_review>가 <design_plan> 및 <evaluation_criteria>를 충족하는지 검증하여 JSON으로 판정하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 문체 취향 차이로는 반려하지 말고, <evaluation_criteria>의 핵심 요건이 누락되었을 때만 pass: false와 구체적 수정 지시(issues)를 반환하십시오.
2. <output_format>의 TypeScript 인터페이스 규격에 부합하는 순수 JSON만 출력하십시오.
</rules>

<output_format>
interface ReviewResponse {
  pass: boolean;         // 검수 통과 여부
  issues: string | null; // 반려 시 구체적 결함 및 수정 지시 (통과 시 null)
}
</output_format>
""";

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(systemInstruction)]),
            Contents: [new Content("user", [new Part(prompt)])],
            SafetySettings: null,
            GenerationConfig: new GenerationConfig(null, 8192, "application/json", ReviewResponseSchema, new ThinkingConfig(ThinkingLevel.medium))
        );

        try
        {
            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.SendMessageStreamAsync(request, ModelTier.Pro, ct))
            {
                sb.Append(chunk);
            }

            var reviewResult = LlmJsonParser.DeserializeSafe<ReviewResponse>(sb.ToString());
            if (reviewResult == null) return (true, null);
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

        // 1. 세션명 결정 (SessionTitle 우선, 없으면 WorldviewOutline/Concept에서 추출)
        string sessionName = !string.IsNullOrWhiteSpace(session.Plan.SessionTitle)
            ? ExtractSessionName(session.Plan.SessionTitle)
            : ExtractSessionName(session.Plan.WorldviewOutline ?? session.Plan.Concept);

        // 2. CharacterPreset 생성 (로어북 768차원 시맨틱 임베딩 자동 부착 및 슬롯별 최종 정제)
        var rawLorebooks = session.GeneratedLorebooks ?? [];
        var embeddedLorebooks = await _lorebookService.EnsureEmbeddingsAsync(rawLorebooks);

        string cleanWorldview = SanitizeWorldview(session.GeneratedWorldview ?? "");
        string cleanStatusGuide = SanitizeStatusGuide(session.GeneratedStatusGuide ?? "");
        string cleanSystemPrompt = string.IsNullOrWhiteSpace(session.GeneratedSystemPrompt)
            ? CompileHybridSystemPrompt("", session.Plan)
            : CompileHybridSystemPrompt(session.GeneratedSystemPrompt, session.Plan);

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

        // 4. Turn 0 ChapterContext 시딩 (초기 인물, 장소, 소지품이 비어 있지 않도록 완벽 초기화)
        var initialChars = session.GeneratedInitialChars != null && session.GeneratedInitialChars.Count > 0
            ? new Dictionary<string, string>(session.GeneratedInitialChars)
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
            history.Add(new ChatMessage("user", $"[초기 상황 설정]\n{cleanScenario}", DateTime.Now));
            history.Add(new ChatMessage("model", "[시스템: 해당 세계관과 초기 상황을 완벽히 인지했습니다. 페르소나를 유지하며 롤플레잉을 대기합니다.]", DateTime.Now.AddSeconds(1)));
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
                foreach (var kv in session.GeneratedInitialChars)
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
                history.Add(new ChatMessage("user", $"[초기 상황 설정]\n{cleanScenario}", DateTime.Now));
                history.Add(new ChatMessage("model", "[시스템: 해당 세계관과 초기 상황을 완벽히 인지했습니다. 페르소나를 유지하며 롤플레잉을 대기합니다.]", DateTime.Now.AddSeconds(1)));

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
                    TriggeredMetaEvents = currentContext.TriggeredMetaEvents,
                    EventLedgers = currentContext.EventLedgers
                }
            };

            await _sessionService.SaveSessionAsync(fileName, updatedSession);
        }
    }

    #region Deterministic Format Sanitizers & Compilers (AI_Prompt_Engineering_Guidelines Enforcers)

    /// <summary>
    /// `<master_setting>` 내부에 주입될 세계관 텍스트에서 XML 태그, 마크다운 헤더(#), 대괄호 섹션 헤더([]), 볼드(**), 코드펜스를 제거합니다.
    /// </summary>
    public static string SanitizeWorldview(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string text = StripCodeFences(raw);
        // 모든 XML/HTML 형태 태그 제거 (<master_setting> 중복이나 임의 태그 오염 방지)
        text = Regex.Replace(text, @"</?[a-zA-Z0-9_:-]+(?:\s[^>]*)?>", "");
        // 마크다운 헤더 (### 제목 -> 제목) 및 대괄호 섹션 헤더 ([1. 제목] -> 1. 제목) 정제
        text = Regex.Replace(text, @"(?m)^#{1,6}\s*(.+?)\s*$", m => m.Groups[1].Value.Trim().Trim('[', ']').Trim());
        text = Regex.Replace(text, @"(?m)^\[([^\r\n\]]+)\]\s*$", "$1");
        // 마크다운 볼드/이탤릭 제거
        text = text.Replace("**", "");
        return NormalizeBlankLines(text);
    }

    /// <summary>
    /// `<world_lore>` 내부에 `- {Name}: {Content}`로 주입될 로어북 본문에서 XML 태그, # 헤더, 대괄호 의사 태그([]), ** 볼드를 제거합니다.
    /// </summary>
    public static string SanitizeLorebookContent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string text = StripCodeFences(raw);
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

        string text = StripCodeFences(raw);
        text = Regex.Replace(text, @"</?[a-zA-Z0-9_:-]+(?:\s[^>]*)?>", "");
        text = Regex.Replace(text, @"(?m)^#{1,6}\s*", "");
        text = text.Replace("**", "");
        text = Regex.Replace(text, @"(?m)^(-\s*)\[([^\]]+)\]\s*:\s*", "$1$2: ");
        return NormalizeBlankLines(text);
    }

    /// <summary>
    /// 오프닝 시나리오에서 마크다운 별표(*), 헤더(#), XML 태그를 제거하고 순수 문학적 산문과 대사(""), 독백(「」)만 남깁니다.
    /// </summary>
    public static string SanitizeScenario(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string text = StripCodeFences(raw);
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
            .Where(l => !Regex.IsMatch(l, @"^[1-4]\.\s*(?:\[?(?:감각적|PC\s*통제|마이크로\s*템포|NPC\s*자율성)|추상적\s*감정|유저\s*캐릭터|시간\s*임의\s*요약|무조건적\s*호의)", RegexOptions.IgnoreCase))
            .Where(l => !Regex.IsMatch(l, @"(상태창\s*업데이트|스탯\s*업데이트|uiBadges|클램핑|Clamping|수치\s*증감)", RegexOptions.IgnoreCase))
            .ToList();

        var numberedLines = new List<string>();
        int nextNumber = 5;

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
                numberedLines.Add($"{nextNumber++}. 평소에는 {plan.SurfaceTone} 호흡을 유지하되, 결정적 계기나 단서 접촉 시에만 {plan.CoreWeight}의 무게감을 행동 디테일과 복선으로 드러낼 것.");
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
