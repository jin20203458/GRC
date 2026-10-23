using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GRC.Models;
using GRC.Services;
using GRC.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace GRC.Tests;

public class SessionArchitectServiceTests(ITestOutputHelper output)
{
    private class MockArchitectApiService : IGeminiApiService
    {
        public List<(GeminiRequest Request, ModelTier Tier)> CapturedCalls { get; } = [];
        public Func<GeminiRequest, ModelTier, string>? ResponseGenerator { get; set; }

        public Task<string> SendMessageAsync(GeminiRequest request, ModelTier? overrideTier = null, CancellationToken cancellationToken = default)
        {
            var tier = overrideTier ?? ModelTier.Flash;
            lock (CapturedCalls)
            {
                CapturedCalls.Add((request, tier));
            }
            string resp = ResponseGenerator != null ? ResponseGenerator(request, tier) : "{}";
            return Task.FromResult(resp);
        }

        public async IAsyncEnumerable<string> SendMessageStreamAsync(
            GeminiRequest request,
            ModelTier? overrideTier = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var tier = overrideTier ?? ModelTier.Flash;
            lock (CapturedCalls)
            {
                CapturedCalls.Add((request, tier));
            }
            string resp = ResponseGenerator != null ? ResponseGenerator(request, tier) : "{}";
            await Task.Yield();
            yield return resp;
        }

        public Task<float[]> GetEmbeddingAsync(string text, string? taskType = null, CancellationToken cancellationToken = default)
        {
            var vec = new float[768];
            vec[0] = 0.5f;
            return Task.FromResult(vec);
        }
    }

    private class InMemorySessionService : ISessionService
    {
        public Dictionary<string, ChatSession> SavedSessions { get; } = new();

        public Task SaveSessionAsync(string fileName, ChatSession session)
        {
            SavedSessions[fileName] = session;
            return Task.CompletedTask;
        }

        public Task<ChatSession?> LoadSessionAsync(string fileName)
        {
            SavedSessions.TryGetValue(fileName, out var session);
            return Task.FromResult(session);
        }

        public Task<IEnumerable<string>> GetSessionFilesAsync()
            => Task.FromResult<IEnumerable<string>>(SavedSessions.Keys);

        public Task DeleteSessionAsync(string fileName)
        {
            SavedSessions.Remove(fileName);
            return Task.CompletedTask;
        }
    }

    private class InMemoryPresetService : IPresetStorageService
    {
        public Dictionary<string, CharacterPreset> SavedPresets { get; } = new();

        public Task SavePresetAsync(string? sessionFileName, CharacterPreset preset)
        {
            SavedPresets[sessionFileName ?? "default"] = preset;
            return Task.CompletedTask;
        }

        public Task<CharacterPreset> LoadPresetAsync(string? sessionFileName = null)
        {
            return Task.FromResult(SavedPresets[sessionFileName ?? "default"]);
        }
    }

    private class PassThroughLorebookService : ILorebookService
    {
        public Task<LorebookEntry?> ExtractMemoryToLorebookAsync(string messageText)
            => Task.FromResult<LorebookEntry?>(null);

        public Task<List<LorebookEntry>> EnsureEmbeddingsAsync(
            IEnumerable<LorebookEntry>? lorebooks,
            CancellationToken cancellationToken = default)
        {
            var list = (lorebooks ?? []).Select(e =>
            {
                var vec = new float[768];
                vec[0] = 0.9f;
                return e with { Embedding = vec };
            }).ToList();
            return Task.FromResult(list);
        }
    }

    [Fact]
    public void Sanitizers_EnforcePromptEngineeringGuidelines_NoXmlOrMarkdownPollution()
    {
        // 1. Worldview Sanitizer: <master_setting> 중복 태그, 임의 XML 태그, ### 헤더, **볼드** 제거 검증
        string dirtyWorldview = """
```xml
<master_setting>
### 1. 배경 및 세계의 핵심 법칙
- **아르카디아 학원 도시**: 표면적으로는 **자율 학원**이나 지하에는 <secret>고대 유적 실험실</secret>이 가동 중이다.
### [2. 세력 구도 및 사회적 현실]
- 학생회와 <faction>심연 연구회</faction>의 대립.
</master_setting>
```
""";
        string cleanWorldview = SessionArchitectService.SanitizeWorldview(dirtyWorldview);
        output.WriteLine("=== [정제된 Worldview] ===\n" + cleanWorldview);

        Assert.DoesNotContain("<master_setting>", cleanWorldview);
        Assert.DoesNotContain("<secret>", cleanWorldview);
        Assert.DoesNotContain("<faction>", cleanWorldview);
        Assert.DoesNotContain("###", cleanWorldview);
        Assert.DoesNotContain("**", cleanWorldview);
        Assert.DoesNotContain("[1.", cleanWorldview);
        Assert.Contains("1. 배경 및 세계의 핵심 법칙", cleanWorldview);
        Assert.Contains("2. 세력 구도 및 사회적 현실", cleanWorldview);

        // 2. Lorebook Sanitizer: XML 태그, **볼드**, [인물] 대괄호 제거 및 대표 대사("") 보존 검증
        string dirtyLoreJson = """
[
  {
    "name": "유즈하 리코",
    "keywords": ["#유즈하", "**리코**", "과학부장"],
    "content": "<character>**[인물]** 괴짜 천재 과학부장\n  * **신분 및 외양**: 백가운에 사탕을 물고 다님\n  * **일상 태도 및 내면의 입체성**: 평소엔 능글맞고 장난스럽지만 지하 실험의 부작용을 홀로 감당함\n  * **대표 대사**: \"어라, 후배 군? 여긴 출입 금지인데~\" / \"...이 이상의 실험 기록은 보지 않는 편이 좋아.\"</character>",
    "category": "인물",
    "priority": 0
  }
]
""";
        var dummyService = new SessionArchitectService(
            new MockArchitectApiService(),
            new InMemorySessionService(),
            new InMemoryPresetService(),
            new PassThroughLorebookService());

        var parsedLores = dummyService.ParseLorebooks(dirtyLoreJson);
        Assert.NotNull(parsedLores);
        Assert.Single(parsedLores!);

        var riko = parsedLores[0];
        output.WriteLine("\n=== [정제된 Lorebook Content] ===\n" + riko.Content);
        Assert.Equal(0, riko.Priority); // priority 0 (최우선 핵심) 보존 검증
        Assert.DoesNotContain("<character>", riko.Content);
        Assert.DoesNotContain("**", riko.Content);
        Assert.DoesNotContain("[인물]", riko.Content);
        Assert.Contains("분류(인물): 괴짜 천재 과학부장", riko.Content);
        Assert.Contains("\"어라, 후배 군? 여긴 출입 금지인데~\"", riko.Content);
        Assert.Contains("유즈하 리코", riko.Keywords);
        Assert.Contains("유즈하", riko.Keywords);
        Assert.DoesNotContain("#유즈하", riko.Keywords);

        // 3. Scenario Sanitizer: 마크다운 * 이탤릭/볼드, # 헤더, > 인용구, XML 태그 제거 및 대사("")/독백(「」) 보존 검증
        string dirtyScenario = """
<scenario>
# 제1장: 방과 후의 실험실
*창밖으로 노을이 지는 과학실.* 리코가 시험관을 흔들며 돌아보았다.
> 그녀의 눈동자 깊은 곳에 스치는 피로감.
"어서 와, 후배 군. 딱 좋은 타이밍이야."
「과연 이 후배에게 진실을 말해도 될까.」
</scenario>
""";
        string cleanScenario = SessionArchitectService.SanitizeScenario(dirtyScenario);
        output.WriteLine("\n=== [정제된 Scenario] ===\n" + cleanScenario);
        Assert.DoesNotContain("<scenario>", cleanScenario);
        Assert.DoesNotContain("*", cleanScenario);
        Assert.DoesNotContain("#", cleanScenario);
        Assert.DoesNotContain(">", cleanScenario);
        Assert.Contains("\"어서 와, 후배 군. 딱 좋은 타이밍이야.\"", cleanScenario);
        Assert.Contains("「과연 이 후배에게 진실을 말해도 될까.」", cleanScenario);
    }

    [Fact]
    public void CompileHybridSystemPrompt_EnforcesZeroNumeric_AndPreservesAllCorePrinciples()
    {
        var plan = new AgentPlan
        {
            SessionTitle = "창백한 아카데미의 방과 후",
            SurfaceTone = "밝고 경쾌한 학원 동아리 코미디와 티키타카",
            CoreWeight = "학원 도시 지하에 봉인된 구시대의 병기 실험과 학생들의 비밀",
            PromptOutline = "일상 파트에서는 경쾌한 템포로 대화를 주고받되, 지하 구역 단서가 등장할 때는 건조하고 서늘한 묘사로 급반전시킬 것."
        };

        // LLM이 실수로 스탯 업데이트 지침을 섞어 반환한 상황 시뮬레이션
        string rawLlmGenreDirectives = """
1. [문체 및 공기감]: 방과 후의 따스한 햇살과 지하 연구실의 차가운 금속성 냄새를 대비시켜 묘사하라.
2. [일상과 핵심 갈등의 완급 조절]: 동아리실에서는 유쾌한 농담과 티키타카를 유지하다가, 금기 키워드가 언급되면 순간적인 침묵과 시선 회피로 긴장감을 조성하라.
3. [상태창 업데이트 지침 준수]: 매 턴 호감도 수치 증감과 uiBadges 클램핑 규칙을 프롬프트 하단에서 계산하라.
""";

        string compiled = SessionArchitectService.CompileHybridSystemPrompt(rawLlmGenreDirectives, plan);
        output.WriteLine("=== [컴파일된 최종 SystemPrompt] ===\n" + compiled);

        // 1. 순수 XML 태그(<system_directive> + <rules>) 래핑 및 대괄호([]) 혼용 제거 검증
        Assert.StartsWith("<system_directive>", compiled);
        Assert.EndsWith("</rules>", compiled);
        Assert.DoesNotContain("[Role]", compiled);
        Assert.DoesNotContain("[Core Directives]", compiled);

        // 2. 가이드라인 원칙 1 (1줄 Role 정의) 및 원칙 16 (불필요한 소제목 라벨 없이 직결 명령문으로 통합) 검증
        Assert.Contains("Role: 3인칭 전지적 게임 마스터", compiled);
        Assert.Contains("1. 추상적 감정 라벨링 및 시스템 수치 언급 배제.", compiled);
        Assert.Contains("2. 유저 캐릭터(PC)의 대사, 행동, 내면의 생각이나 감정을 절대 임의로 서술하거나 단정하지 말 것.", compiled);
        Assert.Contains("3. 시간 임의 요약(Time-skip) 금지.", compiled);
        Assert.Contains("4. 무조건적 호의나 작위적 성공 배제.", compiled);

        // 3. 원칙 2 (Zero-Numeric): 오염된 3번 줄(상태창 업데이트/uiBadges/클램핑)이 물리적으로 제거되었는지 검증
        Assert.DoesNotContain("상태창 업데이트", compiled);
        Assert.DoesNotContain("uiBadges", compiled);
        Assert.DoesNotContain("클램핑", compiled);

        // 4. 불필요한 앞부분 소제목 라벨(문체 및 공기감:)까지 제거되고 `<rules>` 5~6번 직결 명령문으로 통합되었는지 검증
        Assert.DoesNotContain("문체 및 공기감", compiled);
        Assert.Contains("5. 방과 후의 따스한 햇살과 지하 연구실의 차가운 금속성 냄새를 대비시켜 묘사하라.", compiled);
        Assert.Contains("6. 동아리실에서는 유쾌한 농담과 티키타카를 유지하다가", compiled);
    }

    [Fact]
    public async Task GenerateStepContentAsync_LorebookGen_SplitsIntoMicroBatchesInParallel_WithProModel()
    {
        var mockApi = new MockArchitectApiService
        {
            ResponseGenerator = (req, tier) =>
            {
                string userPrompt = req.Contents[0].Parts[0].Text ?? "";
                // 요청에 포함된 로어북 기획 항목 이름들을 추출해 배치 응답 생성
                var matches = Regex.Matches(userPrompt, @"""name"":\s*""([^""]+)""");
                var items = matches.Select(m => new
                {
                    name = m.Groups[1].Value,
                    keywords = new[] { m.Groups[1].Value, "핵심키워드" },
                    content = $"[인물] {m.Groups[1].Value} 상세 설정\n  * 신분 및 외양: 외양 디테일\n  * 일상 태도 및 내면의 입체성: 입체적 내면\n  * 대표 대사: \"첫 번째 대사\" / \"두 번째 대사\"",
                    category = "인물",
                    priority = 90
                }).ToList();

                return JsonSerializer.Serialize(items);
            }
        };

        var service = new SessionArchitectService(
            mockApi,
            new InMemorySessionService(),
            new InMemoryPresetService(),
            new PassThroughLorebookService());

        // 7개의 로어북 기획 항목 -> 크기 3의 마이크로 배치 [3, 3, 1] 총 3회 병렬 Pro 호출 검증
        var plan = new AgentPlan
        {
            SurfaceTone = "밝은 학원물",
            CoreWeight = "이면의 미스터리",
            LorebookPlan = Enumerable.Range(1, 7).Select(i => new LorebookPlanItem
            {
                Name = $"등장인물_{i}",
                Category = "인물",
                Brief = $"요약 {i}",
                Priority = 90
            }).ToList()
        };

        var session = new ArchitectSession
        {
            Plan = plan,
            GeneratedWorldview = "[1. 배경 및 세계의 핵심 법칙]\n- 학원 도시 설정"
        };

        string rawJson = "";
        await foreach (var chunk in service.GenerateStepContentAsync(AgentStep.LorebookGen, plan, session))
        {
            rawJson += chunk;
        }

        var lorebooks = service.ParseLorebooks(rawJson);
        Assert.NotNull(lorebooks);
        Assert.Equal(7, lorebooks!.Count);

        // 3개 배치로 분할 호출되었으며 모두 ModelTier.Pro를 사용했는지 검증
        Assert.Equal(3, mockApi.CapturedCalls.Count);
        Assert.All(mockApi.CapturedCalls, call => Assert.Equal(ModelTier.Pro, call.Tier));
    }

    [Fact]
    public async Task EndToEnd_SessionCreation_To_CognitivePromptAssembler_ProducesGuidelineCompliantRuntimePrompt()
    {
        var mockApi = new MockArchitectApiService();
        var sessionStorage = new InMemorySessionService();
        var presetStorage = new InMemoryPresetService();
        var lorebookService = new PassThroughLorebookService();

        var architectService = new SessionArchitectService(
            mockApi, sessionStorage, presetStorage, lorebookService);

        var plan = new AgentPlan
        {
            Concept = "겉은 밝은 학원 동아리물이지만 지하 미궁의 비밀을 파헤치는 아카데미 판타지",
            SessionTitle = "네온 학원과 심연의 열쇠",
            SurfaceTone = "유쾌하고 템포 빠른 동아리 부원들의 만담과 학원 일상",
            CoreWeight = "학원 지하에 매장된 구문명 유적의 침식과 학생들의 감춰진 과거",
            PcInitialPosition = "신임 동아리 고문 교사(또는 전학생 리더)",
            ScenarioOutline = "방과 후 동아리실에서 부장 아리스가 수상한 고대 열쇠를 책상 위에 올려놓는 장면"
        };

        var statusJson = """
{
  "stats": {
    "아리스 신뢰도": "40/100",
    "유적 침식률": "5/100"
  },
  "guide": "- [아리스 신뢰도]: 장난을 받아주거나 위기에서 보호할 때 +5~+10 상승, 비밀을 강압적으로 추궁할 때 -10 하락 (0~100)\n- [유적 침식률]: 지하 유적 심층부 탐색 시 +5~+15 상승 (0~100)",
  "initialChars": {
    "아리스": "[초기: 새로 온 주인공에게 장난기 어린 호기심을 보이며 시험해 보려는 태도] | 헐렁한 카디건 차림으로 책상에 걸터앉아 열쇠를 만지작거림"
  },
  "initialPlaces": [
    "구교사 3층 특활부 동아리실"
  ],
  "initialItems": [
    "동아리실 마스터키",
    "구교사 안내도"
  ]
}
""";
        var statusPayload = architectService.ParseStatusDesignPayload(statusJson);
        Assert.NotNull(statusPayload);

        var architectSession = new ArchitectSession
        {
            Plan = plan,
            GeneratedWorldview = """
[1. 배경 및 세계의 핵심 법칙]
- 키보토스급 거대 학원 도시 에덴: 지상에서는 학생들이 총기나 마도구를 휴대하며 밝은 학원 자치를 누린다.
- 심연 유적의 침식 법칙: 도시 지하의 구문명 유적은 마력을 흡수하며, 침식률이 임계치를 넘으면 공간 왜곡이 발생한다.

[2. 세력 구도 및 사회적 현실]
- 중앙 학생회: 도시의 평화와 밝은 일상을 유지하기 위해 지하 유적 관련 소문을 통제한다.
- 특활부: 폐부 위기에 몰린 괴짜 동아리이나 실제로는 지하 유적의 이상 징후를 비공식 조사한다.
""",
            GeneratedLorebooks =
            [
                new LorebookEntry(
                    Name: "아리스",
                    Keywords: ["아리스", "특활부장", "은발소녀"],
                    Content: "[인물] 특활부 부장이자 구문명 유적의 유일한 생존자\n  * 신분 및 외양: 은빛 단발머리에 늘 막대사탕을 물고 있는 2학년 소녀.\n  * 일상 태도 및 내면의 입체성: 평소에는 만사를 장난처럼 대하며 엉뚱한 농담을 던지지만, 지하 유적의 위험에 동료가 휘말리는 것만큼은 극도로 경계한다.\n  * 주인공과의 관계 및 태도: 주인공의 실력과 성품을 떠보며 능글맞게 대한다.\n  * 행동 양식 및 발화 특징: 말끝을 살짝 늘리는 경쾌한 반존대.\n  * 대표 대사: \"선생님~ 오늘도 농땡이 치러 오신 건가요?\" / \"...그 문 너머로 가시겠다면, 저도 더는 웃고만 있을 순 없어요.\"",
                    Category: "인물",
                    Priority: 100
                )
            ],
            GeneratedStats = statusPayload!.Stats,
            GeneratedStatusGuide = statusPayload.Guide,
            GeneratedInitialChars = statusPayload.InitialChars,
            GeneratedInitialPlaces = statusPayload.InitialPlaces,
            GeneratedInitialItems = statusPayload.InitialItems,
            GeneratedScenario = "석양이 비치는 구교사 3층 특활부 동아리실. 창문 너머로 운동장 아이들의 활기찬 외침이 아스라이 들려왔다.\n\n책상 위에 걸터앉은 아리스가 막대사탕을 입에 문 채 푸른빛이 감도는 낡은 금속 열쇠를 가볍게 튕겼다. 짤랑거리는 맑은 금속음이 조용한 실내를 울렸다.\n\n\"선생님~ 오늘도 평화로운 동아리 활동을 기대하셨다면 조금 유감이에요. 방금 지하 보일러실 쪽에서 꽤 재밌는 걸 주웠거든요.\" 아리스가 눈을 반달처럼 접어 웃으며 당신의 눈앞에 열쇠를 내밀었다.",
            GeneratedSystemPrompt = SessionArchitectService.CompileHybridSystemPrompt(
                "1. [문체 및 공기감]: 학원 일상의 청량한 묘사와 지하 유적의 서늘한 금속성 긴장감을 선명하게 대비시켜라.\n2. [일상과 갈등의 완급 조절]: 평소 대화에서는 아리스 특유의 경쾌한 티키타카를 살리되, 유적과 관련된 핵심 단서를 건드릴 때만 찰나의 정적과 진지한 눈빛 변화를 묘사하라.",
                plan)
        };

        // Act 1: 새 세션 생성 적용
        string savedFileName = await architectService.ApplyToNewSessionAsync(architectSession);
        var savedPreset = presetStorage.SavedPresets[savedFileName];
        var savedSession = sessionStorage.SavedSessions[savedFileName];

        // Assert 1: Turn 0 ChapterContext가 빈 배열이 아니라 완벽히 시딩되었는지 검증
        Assert.Contains("아리스", savedSession.CurrentContext.Chars.Keys);
        Assert.Contains("[초기:", savedSession.CurrentContext.Chars["아리스"]);
        Assert.Contains("구교사 3층 특활부 동아리실", savedSession.CurrentContext.Places);
        Assert.Contains("동아리실 마스터키", savedSession.CurrentContext.Items);
        Assert.NotNull(savedPreset.Lorebooks![0].Embedding);

        // Act 2: 실제 메인 서사 파이프라인(MemoryManagerService + CognitivePromptAssembler)에 넣고 Turn 1 프롬프트 조립
        var fakeSettings = new MemoryManagerServiceTests.FakeAppSettingsService();
        var memoryManager = new MemoryManagerService(mockApi, fakeSettings);
        memoryManager.RestoreSession(savedSession);

        var turn1UserMsg = new ChatMessage("user", "아리스가 내민 열쇠를 받아 들며 어디서 주웠는지 묻는다.", DateTime.Now);
        var narrativeRequest = await memoryManager.BuildNarrativeRequestAsync(turn1UserMsg, savedPreset);
        var statusRequest = memoryManager.BuildStatusRequest(
            turn1UserMsg.Text,
            "아리스는 살짝 어깨를 으쓱하며 열쇠의 문양을 가리켰다.",
            savedPreset.StatusUpdateGuide,
            BlockThreshold.BLOCK_NONE);

        string assembledSystemInstruction = narrativeRequest.SystemInstruction!.Parts[0].Text!;
        string assembledDynamicUserPrompt = narrativeRequest.Contents.Last().Parts[0].Text!;
        string assembledStatusSystemPrompt = statusRequest.SystemInstruction!.Parts[0].Text!;

        output.WriteLine("=== [런타임 메인 서사 API: SystemInstruction 전문] ===\n" + assembledSystemInstruction);
        output.WriteLine("\n=== [런타임 메인 서사 API: Turn 1 Dynamic Context 전문] ===\n" + assembledDynamicUserPrompt);
        output.WriteLine("\n=== [런타임 상태창 API: SystemInstruction 전문] ===\n" + assembledStatusSystemPrompt);

        // Assert 2: AI_Prompt_Engineering_Guidelines 100% 충족 검증
        // (1) SystemInstruction 최상단에 <system_directive> + <rules> 블록, 중단 <master_setting>, 하단 [문체 및 서술 기본 원칙] 배치
        Assert.StartsWith("<system_directive>", assembledSystemInstruction);
        Assert.Contains("<rules>", assembledSystemInstruction);
        Assert.Contains("<master_setting>", assembledSystemInstruction);
        Assert.Contains("1. 배경 및 세계의 핵심 법칙", assembledSystemInstruction);
        // (2) SystemInstruction 내 스탯/수치 규칙 오염 0건 (원칙 2 Zero-Numeric)
        Assert.DoesNotContain("침식률: 지하 유적", assembledSystemInstruction);
        // (3) Turn 1부터 BiTemporalEntityProjector(<entity_relationship>)와 HybridLorebookRetriever(<world_lore>)가 즉시 가동됨!
        Assert.Contains("<entity_relationship>", assembledDynamicUserPrompt);
        Assert.Contains("- 인물(아리스):", assembledDynamicUserPrompt);
        Assert.Contains("<world_lore>", assembledDynamicUserPrompt);
        Assert.Contains("선생님~ 오늘도 농땡이 치러 오신 건가요?", assembledDynamicUserPrompt);
    }

    [Fact]
    public async Task ViewModel_AutoMode_DefaultsToTrue_AndRendersSpoilerFreeSummaries()
    {
        var mockApi = new MockArchitectApiService
        {
            ResponseGenerator = (req, tier) =>
            {
                string sys = req.SystemInstruction?.Parts[0].Text ?? "";
                if (sys.Contains("Worldbuilding Reference Specification Engine"))
                {
                    return "[1. 배경]\n- 절대 화면에 미리 노출되면 안 되는 심층 세계관 스포일러 텍스트";
                }
                if (sys.Contains("Narrative Session Architecture & Worldbuilding Blueprint Engine"))
                {
                    return """
                    {
                      "sessionTitle": "스포일러 프리 테스트 세션",
                      "surfaceTone": "밝고 유쾌한 학원 일상",
                      "coreWeight": "어두운 지하 미궁의 비밀",
                      "pcInitialPosition": "전학생",
                      "worldviewOutline": "절대 노출되면 안 되는 세계관 극비 스포일러 내용",
                      "lorebookPlan": [
                        { "name": "히로인A", "category": "인물", "brief": "요약", "priority": 0 }
                      ],
                      "statsPlan": ["신뢰도"],
                      "scenarioOutline": "교실에서의 첫 만남",
                      "promptOutline": "경쾌한 문체"
                    }
                    """;
                }
                if (sys.Contains("High-Density Lorebook Entity Synthesis Engine"))
                {
                    return """
                    [
                      {
                        "name": "히로인A",
                        "keywords": ["히로인A"],
                        "content": "[인물] 사실 흑막인 히로인 스포일러 설정",
                        "category": "인물",
                        "priority": 0
                      }
                    ]
                    """;
                }
                if (sys.Contains("TRPG State & Turn-0 Context Initialization Engine"))
                {
                    return """
                    {
                      "stats": { "신뢰도": "50/100" },
                      "guide": "- [신뢰도]: 대화 시 +5",
                      "initialChars": { "히로인A": "[초기: 반가운 미소] | 교복 차림" },
                      "initialPlaces": ["교실"],
                      "initialItems": ["학생증"]
                    }
                    """;
                }
                if (sys.Contains("Omniscient Game Master"))
                {
                    return "교실 창가로 햇살이 쏟아졌다. \"안녕, 전학생!\" 히로인A가 손을 흔들었다.";
                }
                if (sys.Contains("Prompt Engineering Compliance Auditor"))
                {
                    return """{ "pass": true, "issues": null }""";
                }
                return "1. [문체]: 경쾌한 학원물 톤을 유지하라.";
            }
        };

        var sessionStorage = new InMemorySessionService();
        var presetStorage = new InMemoryPresetService();
        var architectService = new SessionArchitectService(
            mockApi, sessionStorage, presetStorage, new PassThroughLorebookService());

        var vm = new SessionArchitectViewModel(architectService);

        // 1. 기본값이 자동 모드(IsAutoMode == true)인지 검증
        Assert.True(vm.IsAutoMode);

        // 2. 컨셉 입력 후 실행
        vm.InputText = "밝은 학원물이지만 비밀이 있는 아카데미물";
        await vm.SendCommand.ExecuteAsync(null);

        // 3. 자동 완료 후 세션이 정상 저장되었는지 검증
        Assert.Single(presetStorage.SavedPresets);
        var savedPreset = presetStorage.SavedPresets.Values.First();
        Assert.Contains("절대 화면에 미리 노출되면 안 되는 심층 세계관 스포일러 텍스트", savedPreset.Worldview);
        Assert.Contains("사실 흑막인 히로인 스포일러 설정", savedPreset.Lorebooks![0].Content);

        // 4. 반면 아키텍트 UI 채팅창(vm.Messages)에는 스포일러 본문이 노출되지 않고 완료 요약만 출력되었는지 검증!
        string allUiChatText = string.Join("\n", vm.Messages.Select(m => m.Text));
        output.WriteLine("=== [자동 모드 UI 채팅창 출력 내역] ===\n" + allUiChatText);

        Assert.DoesNotContain("절대 화면에 미리 노출되면 안 되는 심층 세계관 스포일러 텍스트", allUiChatText);
        Assert.DoesNotContain("사실 흑막인 히로인 스포일러 설정", allUiChatText);
        Assert.Contains("[1/6] 서사 청사진 수립 완료", allUiChatText);
        Assert.Contains("[2/6] 마스터 세계관 구축 완료", allUiChatText);
        Assert.Contains("[3/6] 로어북 심층 생성 완료", allUiChatText);

        // 5. 캡처된 모든 하드코딩 내부 프롬프트가 AI_Prompt_Engineering_Guidelines.md를 준수했는지 전수 검증
        Assert.NotEmpty(mockApi.CapturedCalls);
        foreach (var (req, _) in mockApi.CapturedCalls)
        {
            string sysText = req.SystemInstruction?.Parts[0].Text ?? "";
            string usrText = req.Contents[0].Parts[0].Text ?? "";

            // 원칙 1: <system_directive> 구조화 및 2인칭 인격화("당신은 ~입니다") 완전 배제
            Assert.StartsWith("<system_directive>", sysText.Trim());
            Assert.DoesNotContain("당신은", sysText);

            // 원칙 1 & 안티패턴 4: 모든 User Prompt 최하단에 <final_instruction> 배치
            Assert.Contains("<final_instruction>", usrText);
            Assert.EndsWith("</final_instruction>", usrText.Trim());

            // 안티패턴 3 & 5: 비표준 [검수 루브릭] 헤더나 서식 부정 지시문("절대 쓰지 마라") 배제
            Assert.DoesNotContain("[검수 루브릭]", usrText);
            Assert.DoesNotContain("절대 쓰지 마라", sysText);

            // 원칙 3: JSON 응답 요청 시 TypeScript interface 사용 검증
            if (req.GenerationConfig?.ResponseMimeType == "application/json")
            {
                Assert.Contains("interface ", sysText);
            }

            // 원칙 8: Thinking 모델 MaxOutputTokens 8192 보장 (JSON 절단 트랩 방어)
            Assert.Equal(8192, req.GenerationConfig?.MaxOutputTokens);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealGeminiPro_SessionArchitectV2_GeneratesHighDensityGuidelineCompliantSession()
    {
        // 실제 Vertex AI / Gemini Pro 모델을 호출하여 생성된 프롬프트 품질을 검증하는 통합 테스트
        string[] candidates =
        [
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "google-credentials.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GRC", "Config", "google-credentials.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GRC", "bin", "Debug", "net8.0-windows", "Config", "google-credentials.json")
        ];

        string? credentialPath = candidates.FirstOrDefault(File.Exists);
        if (credentialPath == null) return;

        string jsonContent = File.ReadAllText(credentialPath);
        var doc = JsonNode.Parse(jsonContent);
        string projectId = doc?["project_id"]?.GetValue<string>() ?? "grc0-494913";

        var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var authService = new GoogleAuthService();
        var settingsService = new MemoryManagerServiceTests.FakeAppSettingsService();
        var realApi = new GeminiApiService(httpClient, new RealTestSettingsService(projectId), authService);

        var sessionStorage = new InMemorySessionService();
        var presetStorage = new InMemoryPresetService();
        var architectService = new SessionArchitectService(
            realApi, sessionStorage, presetStorage, new PassThroughLorebookService());

        string concept = "평소에는 밝고 엉뚱한 만담이 오가는 학원 흥신소 동아리물이지만, 학원 도시 이면에는 실종된 학생들과 구문명 실험체의 어두운 비밀이 도사리고 있는 서브컬처 아카데미 판타지";

        // 1. Stage 1: Planning
        string planJson = "";
        await foreach (var chunk in architectService.GeneratePlanAsync(concept))
        {
            planJson += chunk;
        }
        var plan = architectService.ParsePlan(planJson);
        Assert.NotNull(plan);
        plan!.Concept = concept;
        output.WriteLine($"[Stage 1 기획 완료] 제목: {plan.SessionTitle}\n- 일상 호흡: {plan.SurfaceTone}\n- 핵심 무게감: {plan.CoreWeight}\n- 로어북 기획 수: {plan.LorebookPlan.Count}");

        var session = new ArchitectSession { Plan = plan };

        // 2. Stage 2: Worldview
        string worldview = "";
        await foreach (var chunk in architectService.GenerateStepContentAsync(AgentStep.WorldviewGen, plan, session))
        {
            worldview += chunk;
        }
        session.GeneratedWorldview = worldview;
        output.WriteLine($"\n[Stage 2 세계관 완료]\n{worldview}");
        Assert.DoesNotContain("**", worldview);
        Assert.DoesNotContain("###", worldview);

        // 3. Stage 3: Parallel Lorebooks
        string loreJson = "";
        await foreach (var chunk in architectService.GenerateStepContentAsync(AgentStep.LorebookGen, plan, session))
        {
            loreJson += chunk;
        }
        session.GeneratedLorebooks = architectService.ParseLorebooks(loreJson);
        Assert.NotNull(session.GeneratedLorebooks);
        Assert.NotEmpty(session.GeneratedLorebooks!);
        output.WriteLine($"\n[Stage 3 병렬 로어북 완료: 총 {session.GeneratedLorebooks.Count}건]");
        foreach (var l in session.GeneratedLorebooks)
        {
            int estTokens = HybridLorebookRetriever.EstimateTokens(l.Name) + HybridLorebookRetriever.EstimateTokens(l.Content) + 10;
            output.WriteLine($"- {l.Name} ({l.Category}, Priority {l.Priority}, {l.Content.Length}자 / 약 {estTokens}토큰):\n{l.Content}\n");
            Assert.DoesNotContain("**", l.Content);
            Assert.InRange(l.Priority, 0, 3);
        }

        // 4. Stage 4: Status & Turn 0 Seeding
        string statusJson = "";
        await foreach (var chunk in architectService.GenerateStepContentAsync(AgentStep.StatusGen, plan, session))
        {
            statusJson += chunk;
        }
        var statusPayload = architectService.ParseStatusDesignPayload(statusJson);
        Assert.NotNull(statusPayload);
        session.GeneratedStats = statusPayload!.Stats;
        session.GeneratedStatusGuide = statusPayload.Guide;
        session.GeneratedInitialChars = statusPayload.InitialChars;
        session.GeneratedInitialPlaces = statusPayload.InitialPlaces;
        session.GeneratedInitialItems = statusPayload.InitialItems;

        // 5. Stage 5: Opening Scenario
        string scenario = "";
        await foreach (var chunk in architectService.GenerateStepContentAsync(AgentStep.ScenarioGen, plan, session))
        {
            scenario += chunk;
        }
        session.GeneratedScenario = scenario;
        output.WriteLine($"\n[Stage 5 오프닝 시나리오 완료]\n{scenario}");
        Assert.DoesNotContain("*", scenario);

        // 6. Stage 6: Hybrid SystemPrompt
        string sysPrompt = "";
        await foreach (var chunk in architectService.GenerateStepContentAsync(AgentStep.PromptGen, plan, session))
        {
            sysPrompt += chunk;
        }
        session.GeneratedSystemPrompt = sysPrompt;
        output.WriteLine($"\n[Stage 6 시스템 프롬프트 완료]\n{sysPrompt}");
        Assert.StartsWith("<system_directive>", sysPrompt);
        Assert.EndsWith("</rules>", sysPrompt);
        Assert.DoesNotContain("uiBadges", sysPrompt);

        // 7. ApplyToNewSessionAsync 및 런타임 프롬프트 조립 검증 (AI_Prompt_Engineering_Guidelines.md 16대 원칙 대조)
        string fileName = await architectService.ApplyToNewSessionAsync(session);
        var savedPreset = presetStorage.SavedPresets[fileName];
        var savedChatSession = sessionStorage.SavedSessions[fileName];

        var memoryManager = new MemoryManagerService(realApi, new RealTestSettingsService(projectId));
        memoryManager.RestoreSession(savedChatSession);

        var turn1UserAction = new ChatMessage("user", "주변을 둘러보며 눈앞의 인물에게 말을 건넨다.", DateTime.Now);
        var narrativeRequest = await memoryManager.BuildNarrativeRequestAsync(turn1UserAction, savedPreset);
        string finalSystemInstruction = narrativeRequest.SystemInstruction!.Parts[0].Text!;
        string finalDynamicPrompt = narrativeRequest.Contents.Last().Parts[0].Text!;

        output.WriteLine("\n==================================================================");
        output.WriteLine("[실제 런타임 조립 결과 1: SystemInstruction (Prefix-Frozen)]");
        output.WriteLine("==================================================================");
        output.WriteLine(finalSystemInstruction);

        output.WriteLine("\n==================================================================");
        output.WriteLine("[실제 런타임 조립 결과 2: Turn 1 Dynamic User Prompt]");
        output.WriteLine("==================================================================");
        output.WriteLine(finalDynamicPrompt);

        // 가이드라인 정합성 단언(Assertions)
        Assert.StartsWith("<system_directive>", finalSystemInstruction);
        Assert.Contains("Role: 3인칭 전지적 게임 마스터", finalSystemInstruction);
        Assert.Contains("<rules>", finalSystemInstruction);
        Assert.Contains("<master_setting>", finalSystemInstruction);
        Assert.Contains("</master_setting>", finalSystemInstruction);
        Assert.NotEmpty(savedChatSession.CurrentContext.Chars);
        Assert.NotEmpty(savedChatSession.CurrentContext.Places);
        Assert.Contains("<entity_relationship>", finalDynamicPrompt);
        Assert.Contains("<world_lore>", finalDynamicPrompt);
    }

    private class RealTestSettingsService(string projectId) : IAppSettingsService
    {
        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings(
            ApiKey: "",
            ProjectId: projectId,
            UseVertexAI: true,
            SelectedModel: ModelTier.Pro,
            SafetyThreshold: BlockThreshold.BLOCK_NONE));
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public bool IsCredentialFileExists() => true;
        public Task<bool> CopyCredentialFileAsync(string sourceFilePath) => Task.FromResult(true);
    }
}
