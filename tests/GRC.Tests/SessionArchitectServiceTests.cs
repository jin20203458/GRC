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
    public void SanitizeWorldview_SingleLineFlattened_DeterministicallyRestoresNewlines()
    {
        // LLM이 JSON 문자열 내에서 줄바꿈 없이 공백으로 이어붙여 1줄 평탄화된 세계관 출력 시뮬레이션
        string singleLineWorldview = "1. 배경 및 세계의 핵심 법칙 - 대륙의 정세: 흑철 제국과 신성 왕국의 휴전기. - 마법과 기술: 마도 증기 기관의 태동. 2. 세력 구도 및 사회적 현실 - 모험가 길드: 중립 지대 유지. - 암시장: 금지된 마석 유통. 3. 주인공(PC)의 초기 입지 및 직면 환경 - 신분: 은퇴한 A급 모험가. 4. 서사 톤앤매너 및 감각적 디테일 - 공기감: 녹슨 쇳내와 비 젖은 흙냄새.";

        string sanitized = SessionArchitectService.SanitizeWorldview(singleLineWorldview);
        output.WriteLine("=== [복원된 Worldview] ===\n" + sanitized);

        // 번호 섹션 헤더 앞 줄바꿈 검증
        Assert.Contains("\n\n2. 세력 구도 및 사회적 현실", sanitized.Replace("\r\n", "\n"));
        Assert.Contains("\n\n3. 주인공(PC)의 초기 입지 및 직면 환경", sanitized.Replace("\r\n", "\n"));
        Assert.Contains("\n\n4. 서사 톤앤매너 및 감각적 디테일", sanitized.Replace("\r\n", "\n"));

        // 하이픈 불릿 앞 줄바꿈 검증
        Assert.Contains("\n- 대륙의 정세: 흑철 제국과 신성 왕국의 휴전기.", sanitized.Replace("\r\n", "\n"));
        Assert.Contains("\n- 마법과 기술: 마도 증기 기관의 태동.", sanitized.Replace("\r\n", "\n"));
        Assert.Contains("\n- 모험가 길드: 중립 지대 유지.", sanitized.Replace("\r\n", "\n"));
        Assert.Contains("\n- 신분: 은퇴한 A급 모험가.", sanitized.Replace("\r\n", "\n"));
        Assert.Contains("\n- 공기감: 녹슨 쇳내와 비 젖은 흙냄새.", sanitized.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Sanitizers_StripsForeignGlitchScripts_SuchAsDevanagariAndArabic()
    {
        // 다국어 토크나이저 글리치 문자(데바나가리 सिर, 아랍어, 태국어 등) 시뮬레이션
        string rawScenario = "광장의 바람이 불어왔다. सिर에 닿은 시엘의 손끝에서 전해지는 온기와 마력.";
        string sanitizedScenario = SessionArchitectService.SanitizeScenario(rawScenario);
        Assert.DoesNotContain("सिर", sanitizedScenario);
        Assert.Equal("광장의 바람이 불어왔다. 에 닿은 시엘의 손끝에서 전해지는 온기와 마력.", sanitizedScenario);

        string rawWorldview = "1. 배경 및 세계의 핵심 법칙 - 아카데미: 부유섬 \u0938\u093F\u0930 라피스라줄리.";
        string sanitizedWorldview = SessionArchitectService.SanitizeWorldview(rawWorldview);
        Assert.DoesNotContain("\u0938\u093F\u0930", sanitizedWorldview);
    }

    [Fact]
    public void NormalizeInitialCharKeys_StripsInitialAttitudeWrapper_AndNormalizesValue()
    {
        var initialChars = new Dictionary<string, string>
        {
            ["아리아"] = "초기 태도(경계심과 호기심), 결투장 대기실 앞 광장에서 검자루에 손을 얹고 대련 재촉 중",
            ["시엘"] = "초기 태도: 다정한 애정과 견제심, 샌드위치를 내민 채 대화 중"
        };

        var lorebooks = new List<LorebookEntry>
        {
            new("아리아 폰 블랑셰", ["아리아"], "공작영애", "인물"),
            new("시엘 클로버", ["시엘"], "소꿉친구", "인물")
        };

        var normalized = SessionArchitectService.NormalizeInitialCharKeys(initialChars, lorebooks);

        Assert.Equal("경계심과 호기심, 결투장 대기실 앞 광장에서 검자루에 손을 얹고 대련 재촉 중", normalized["아리아 폰 블랑셰"]);
        Assert.Equal("다정한 애정과 견제심, 샌드위치를 내민 채 대화 중", normalized["시엘 클로버"]);
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
        Assert.Contains("1. 추상적 감정 라벨링, 시스템 수치 언급, 문단 끝의 교훈적 요약을 배제하고", compiled);
        Assert.Contains("2. 유저 캐릭터(PC)의 대사, 행동, 내면의 생각·감정을 임의로 대신 서술하지 말고", compiled);
        Assert.Contains("3. 작위적인 주인공 보정(무조건적 호의·기적적 성공)과 억지 실패 보정을 모두 배제하고", compiled);

        // 3. 원칙 2 (Zero-Numeric): 오염된 3번 줄(상태창 업데이트/uiBadges/클램핑)이 물리적으로 제거되었는지 검증
        Assert.DoesNotContain("상태창 업데이트", compiled);
        Assert.DoesNotContain("uiBadges", compiled);
        Assert.DoesNotContain("클램핑", compiled);

        // 4. 불필요한 앞부분 소제목 라벨(문체 및 공기감:)까지 제거되고 `<rules>` 4~5번 직결 명령문으로 통합되었는지 검증
        Assert.DoesNotContain("문체 및 공기감", compiled);
        Assert.Contains("4. 방과 후의 따스한 햇살과 지하 연구실의 차가운 금속성 냄새를 대비시켜 묘사하라.", compiled);
        Assert.Contains("5. 동아리실에서는 유쾌한 농담과 티키타카를 유지하다가", compiled);
    }

    [Fact]
    public async Task GenerateStepContentAsync_LorebookGen_SplitsIntoMicroBatchesInParallel_WithProModel()
    {
        var mockApi = new MockArchitectApiService
        {
            ResponseGenerator = (req, tier) =>
            {
                string userPrompt = req.Contents[0].Parts[0].Text ?? "";
                // 요청에 포함된 로어북 기획 항목 이름들을 추출해 응답 생성
                var matches = Regex.Matches(userPrompt, @"""name"":\s*""([^""]+)""");
                var items = matches.Select(m => new
                {
                    name = m.Groups[1].Value,
                    keywords = new[] { m.Groups[1].Value, "핵심키워드" },
                    content = $"[인물] {m.Groups[1].Value} 상세 설정\n  * 외양/특징: 외양 디테일\n  * 표면/내면: 입체적 내면\n  * PC 관계: 초기 태도\n  * 타 인물/세력 관계: 상호 관계망\n  * 말투/대사: \"첫 번째 대사\" / \"두 번째 대사\"",
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

        // 7개의 인물 로어북 기획 항목 -> 인위적인 3개씩 쪼개기 없이 Wave 2 단일 통합 앙상블 호출(1회) 및 32,768 토큰 헤드룸 검증
        var plan = new AgentPlan
        {
            SurfaceTone = "밝은 학원물",
            CoreWeight = "이면의 미스터리",
            LorebookPlan = Enumerable.Range(1, 7).Select(i => new LorebookPlanItem
            {
                Name = $"등장인물_{i}",
                Category = "인물",
                Brief = $"요약 {i}",
                ConnectedEntities = "다른 인물과의 관계",
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

        // 조각 분할 없이 단 1회의 통합 앙상블 Pro 호출 및 32,768 토큰 헤드룸이 적용되었는지 검증
        Assert.Single(mockApi.CapturedCalls);
        Assert.Equal(ModelTier.Pro, mockApi.CapturedCalls[0].Tier);
        Assert.Equal(SessionArchitectService.ArchitectMaxOutputTokens, mockApi.CapturedCalls[0].Request.GenerationConfig?.MaxOutputTokens);
    }

    [Fact]
    public async Task GenerateStepContentAsync_LorebookGen_ExecutesTwoWaveCausalPipeline_AndReviseSurgically()
    {
        var mockApi = new MockArchitectApiService
        {
            ResponseGenerator = (req, tier) =>
            {
                string userPrompt = req.Contents[0].Parts[0].Text ?? "";
                var matches = Regex.Matches(userPrompt, @"""name"":\s*""([^""]+)""");
                var catMatches = Regex.Matches(userPrompt, @"""category"":\s*""([^""]+)""");

                var items = matches.Select((m, idx) =>
                {
                    string name = m.Groups[1].Value;
                    string category = idx < catMatches.Count ? catMatches[idx].Groups[1].Value : "세계관";
                    bool isRevised = userPrompt.Contains("<user_feedback>");
                    // 초안에서는 유즈하에게만 일부러 큰따옴표 대사를 누락시켜 C# 결정론적 검수 적발 테스트
                    string dialoguePart = isRevised || name != "유즈하"
                        ? "\n  * 말투/대사: 차분한 어조. \"실례합니다.\""
                        : "\n  * 말투/대사: 큰따옴표가 누락된 어조 설명";
                    return new
                    {
                        name,
                        keywords = new[] { name },
                        content = isRevised
                            ? $"분류({category}): [외과수술 보정 완료] {name} 상세 설정{dialoguePart}"
                            : $"분류({category}): [초안] {name} 상세 설정{(category == "인물" ? dialoguePart : "")}",
                        category,
                        priority = 0
                    };
                }).ToList();

                return JsonSerializer.Serialize(items);
            }
        };

        var service = new SessionArchitectService(
            mockApi,
            new InMemorySessionService(),
            new InMemoryPresetService(),
            new PassThroughLorebookService());

        var plan = new AgentPlan
        {
            SurfaceTone = "밝은 학원물",
            CoreWeight = "심연의 비밀",
            LorebookPlan =
            [
                new LorebookPlanItem { Name = "특활부 동아리실", Category = "장소", Brief = "구교사 3층 거점", ConnectedEntities = "아리스, 유즈하", Priority = 1 },
                new LorebookPlanItem { Name = "심연 연구회", Category = "세계관", Brief = "지하 유적 비밀 결사", ConnectedEntities = "유즈하", Priority = 1 },
                new LorebookPlanItem { Name = "아리스", Category = "인물", Brief = "특활부 부장", ConnectedEntities = "특활부 동아리실, 유즈하", Priority = 0 },
                new LorebookPlanItem { Name = "유즈하", Category = "인물", Brief = "과학부장", ConnectedEntities = "심연 연구회, 아리스", Priority = 0 }
            ]
        };

        var session = new ArchitectSession
        {
            Plan = plan,
            GeneratedWorldview = "1. 배경 및 세계의 핵심 법칙\n- 학원 도시 설정"
        };

        // Act 1: 2-Wave 인과 로어북 생성
        string rawJson = "";
        await foreach (var chunk in service.GenerateStepContentAsync(AgentStep.LorebookGen, plan, session))
        {
            rawJson += chunk;
        }

        var lorebooks = service.ParseLorebooks(rawJson);
        Assert.NotNull(lorebooks);
        Assert.Equal(4, lorebooks!.Count);
        Assert.Equal(2, mockApi.CapturedCalls.Count); // Wave 1 (비인물 통합 1회) + Wave 2 (인물 통합 1회)

        // Wave 2 프롬프트에 Wave 1에서 완성된 <established_world_lore>와 <full_blueprint_roster>가 주입되었는지 검증
        string wave2Prompt = mockApi.CapturedCalls[1].Request.Contents[0].Parts[0].Text!;
        Assert.Contains("<full_blueprint_roster>", wave2Prompt);
        Assert.Contains("<established_world_lore>", wave2Prompt);
        Assert.Contains("[초안] 특활부 동아리실 상세 설정", wave2Prompt);
        Assert.Contains("[초안] 심연 연구회 상세 설정", wave2Prompt);

        // Act 1.5: C# 결정론적 검수 관문(ReviewStepContentAsync)이 LLM API 호출 0회로 유즈하의 큰따옴표 대사 누락만 정확히 적발하는지 검증
        mockApi.CapturedCalls.Clear();
        var (pass, feedback) = await service.ReviewStepContentAsync(AgentStep.LorebookReview, rawJson, plan, session);
        Assert.Empty(mockApi.CapturedCalls); // LLM 감사관 호출 0회!
        Assert.False(pass);
        Assert.Contains("[수정 대상: 유즈하]", feedback);

        // Act 2: 적발된 피드백으로 외과수술식 부분 수정 (ReviseContentAsync)
        session.GeneratedLorebooks = lorebooks;

        string revisedJson = "";
        await foreach (var chunk in service.ReviseContentAsync(
            AgentStep.LorebookReview,
            rawJson,
            feedback!,
            plan,
            session))
        {
            revisedJson += chunk;
        }

        var revisedLorebooks = service.ParseLorebooks(revisedJson);
        Assert.NotNull(revisedLorebooks);
        Assert.Equal(4, revisedLorebooks!.Count);
        Assert.Single(mockApi.CapturedCalls); // 오직 유즈하 1건에 대해서만 1회 호출됨

        // 유즈하만 교체되고 나머지 3개 항목은 손실 없이 그대로 보존되었는지 검증
        Assert.Contains("[외과수술 보정 완료] 유즈하", revisedLorebooks.First(l => l.Name == "유즈하").Content);
        Assert.Contains("[초안] 아리스", revisedLorebooks.First(l => l.Name == "아리스").Content);
        Assert.Contains("[초안] 특활부 동아리실", revisedLorebooks.First(l => l.Name == "특활부 동아리실").Content);
    }

    [Fact]
    public async Task ScenarioFirst_Then_StatusGen_ExtractsFromOpeningScenario_AndNormalizesInitialCharKeys()
    {
        var mockApi = new MockArchitectApiService
        {
            ResponseGenerator = (req, tier) =>
            {
                string sys = req.SystemInstruction?.Parts[0].Text ?? "";
                if (sys.Contains("Omniscient Game Master"))
                {
                    return "석양이 비치는 구교사 동아리실. 아리스가 낡은 열쇠를 내밀었다. \"선생님, 이거 보실래요?\"";
                }
                if (sys.Contains("TRPG State"))
                {
                    // 신규 통합 물리적 스키마(KeyValueItem[] 배열 + genreDirectives) 및 직함이 섞인 인물명("특활부장 아리스") 반환 시뮬레이션
                    return """
                    {
                      "stats": [
                        { "name": "아리스 신뢰도", "value": "45/100" }
                      ],
                      "guide": "- 아리스 신뢰도: 협력 시 +5 / 거절 시 -5 (0~100)",
                      "initialChars": [
                        { "name": "특활부장 아리스", "value": "초기 태도(호기심), 낡은 열쇠를 내밀며 미소 짓는 중" }
                      ],
                      "initialPlaces": ["구교사 3층 특활부 동아리실"],
                      "initialItems": ["낡은 열쇠"],
                      "genreDirectives": [
                        "평소에는 동아리실의 경쾌한 만담 호흡을 유지하되, 지하 유적 열쇠가 작동할 때는 서늘한 긴장감으로 전환할 것.",
                        "학원 도시의 자치 학칙과 유적 침식 제약을 엄격히 적용하여 무모한 행동에는 즉각적인 물리적 대가를 부여할 것."
                      ]
                    }
                    """;
                }
                return "{}";
            }
        };

        var service = new SessionArchitectService(
            mockApi,
            new InMemorySessionService(),
            new InMemoryPresetService(),
            new PassThroughLorebookService());

        var plan = new AgentPlan
        {
            SurfaceTone = "경쾌한 일상",
            CoreWeight = "지하 미궁의 비밀",
            PcInitialPosition = "신임 고문 교사",
            ScenarioOutline = "동아리실에서 아리스가 열쇠를 건네는 장면",
            StatsPlan = ["아리스 신뢰도"]
        };

        var session = new ArchitectSession
        {
            Plan = plan,
            GeneratedWorldview = "1. 배경\n- 학원 도시",
            GeneratedLorebooks =
            [
                new LorebookEntry(
                    Name: "아리스",
                    Keywords: ["아리스", "특활부장"],
                    Content: "분류(인물): 특활부 부장",
                    Category: "인물",
                    Priority: 0)
            ]
        };

        // 1. 3단계: ScenarioGen 선행 실행 (레거시 <turn0_state> 블록이 없는지 검증)
        string scenario = "";
        await foreach (var chunk in service.GenerateStepContentAsync(AgentStep.ScenarioGen, plan, session))
        {
            scenario += chunk;
        }
        session.GeneratedScenario = scenario;

        string scenarioUserPrompt = mockApi.CapturedCalls[0].Request.Contents[0].Parts[0].Text!;
        Assert.DoesNotContain("<turn0_state>", scenarioUserPrompt);
        Assert.Contains("<lorebooks>", scenarioUserPrompt);

        // 2. 4단계: StatusGen 후행 실행 (완성된 <opening_scenario> 주입 및 물리적 ResponseSchema 바인딩 검증)
        string statusJson = "";
        await foreach (var chunk in service.GenerateStepContentAsync(AgentStep.StatusGen, plan, session))
        {
            statusJson += chunk;
        }

        var statusCall = mockApi.CapturedCalls[1].Request;
        string statusUserPrompt = statusCall.Contents[0].Parts[0].Text!;
        Assert.Contains("<opening_scenario>", statusUserPrompt);
        Assert.Contains("석양이 비치는 구교사 동아리실. 아리스가 낡은 열쇠를 내밀었다.", statusUserPrompt);
        Assert.NotNull(statusCall.GenerationConfig?.ResponseSchema);

        // 3. KeyValueItem[] 배열, "특활부장 아리스" -> "아리스" 정규화 및 genreDirectives 동시 추출 검증
        var payload = service.ParseStatusDesignPayload(statusJson, session.GeneratedLorebooks);
        Assert.NotNull(payload);
        Assert.Equal("45/100", payload!.Stats["아리스 신뢰도"]);
        Assert.True(payload.InitialChars.ContainsKey("아리스"));
        Assert.False(payload.InitialChars.ContainsKey("특활부장 아리스"));
        Assert.Equal(2, payload.GenreDirectives.Count);
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
                if (sys.Contains("Narrative Session Architecture & Worldbuilding Blueprint Engine"))
                {
                    return """
                    {
                      "sessionTitle": "스포일러 프리 테스트 세션",
                      "surfaceTone": "밝고 유쾌한 학원 일상",
                      "coreWeight": "어두운 지하 미궁의 비밀",
                      "pcInitialPosition": "전학생",
                      "worldviewOutline": "1. 배경 및 세계의 핵심 법칙\n- 절대 화면에 미리 노출되면 안 되는 심층 세계관 스포일러 텍스트",
                      "lorebookPlan": [
                        { "name": "히로인A", "category": "인물", "brief": "요약", "surfaceRole": "표면", "innerDepth": "내면", "connectedEntities": "교실", "keywords": ["히로인A"], "priority": 0 }
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
                        "content": "[인물] 사실 흑막인 히로인 스포일러 설정\n  * 말투/대사: 경쾌한 반말. \"안녕, 전학생!\"",
                        "category": "인물",
                        "priority": 0
                      }
                    ]
                    """;
                }
                if (sys.Contains("TRPG State"))
                {
                    return """
                    {
                      "stats": { "신뢰도": "50/100" },
                      "guide": "- [신뢰도]: 대화 시 +5",
                      "initialChars": { "히로인A": "[초기: 반가운 미소] | 교복 차림" },
                      "initialPlaces": ["교실"],
                      "initialItems": ["학생증"],
                      "genreDirectives": [
                        "평소에는 밝고 경쾌한 학원 일상의 만담 템포를 유지할 것.",
                        "지하 미궁의 금기 규칙을 어길 시 즉각적인 물리적 반동을 판정할 것."
                      ]
                    }
                    """;
                }
                if (sys.Contains("Omniscient Game Master"))
                {
                    return "교실 창가로 햇살이 쏟아졌다. \"안녕, 전학생!\" 히로인A가 손을 흔들었다.";
                }
                return "";
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

        // 3. 자동 완료 후 세션이 정상 저장되었는지 검증 (1단계 청사진에서 세계관이, 4단계 상태창에서 시스템 지시문이 통합 생성됨)
        Assert.Single(presetStorage.SavedPresets);
        var savedPreset = presetStorage.SavedPresets.Values.First();
        Assert.Contains("절대 화면에 미리 노출되면 안 되는 심층 세계관 스포일러 텍스트", savedPreset.Worldview);
        Assert.Contains("사실 흑막인 히로인 스포일러 설정", savedPreset.Lorebooks![0].Content);
        Assert.StartsWith("<system_directive>", savedPreset.SystemPrompt);
        Assert.Contains("4. 평소에는 밝고 경쾌한 학원 일상의 만담 템포를 유지할 것.", savedPreset.SystemPrompt);
        Assert.Contains("5. 지하 미궁의 금기 규칙을 어길 시 즉각적인 물리적 반동을 판정할 것.", savedPreset.SystemPrompt);

        // 4. 반면 아키텍트 UI 채팅창(vm.Messages)에는 스포일러 본문이 노출되지 않고 4단계 완료 요약만 출력되었는지 검증!
        string allUiChatText = string.Join("\n", vm.Messages.Select(m => m.Text));
        output.WriteLine("=== [자동 모드 UI 채팅창 출력 내역] ===\n" + allUiChatText);

        Assert.DoesNotContain("절대 화면에 미리 노출되면 안 되는 심층 세계관 스포일러 텍스트", allUiChatText);
        Assert.DoesNotContain("사실 흑막인 히로인 스포일러 설정", allUiChatText);
        Assert.Contains("[1/4] 서사 청사진 및 마스터 세계관 구축 완료", allUiChatText);
        Assert.Contains("[2/4] 2-Wave 앙상블 로어북 심층 생성 완료", allUiChatText);
        Assert.Contains("[3/4] 오프닝 시나리오 집필 완료", allUiChatText);
        Assert.Contains("[4/4] 상태창·Turn 0 시딩 및 시스템 지시문 컴파일 완료", allUiChatText);

        // 5. 캡처된 모든 하드코딩 내부 프롬프트가 AI_Prompt_Engineering_Guidelines.md를 준수했는지 전수 검증
        // (단일 카테고리 로어북이므로 총 4회 호출: 1. Plan+Worldview -> 2. Lorebook -> 3. Scenario -> 4. Status+Prompt, LLM 감사관 호출 0회!)
        Assert.Equal(4, mockApi.CapturedCalls.Count);
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

            // 원칙 8: Gemini 4 Argon (ThinkingLevel.high) MaxOutputTokens 32768 보장 (JSON 절단 트랩 방어)
            Assert.Equal(SessionArchitectService.ArchitectMaxOutputTokens, req.GenerationConfig?.MaxOutputTokens);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealGeminiPro_SessionArchitectV2_GeneratesHighDensityGuidelineCompliantSession()
    {
        // 실제 Vertex AI / Gemini Pro 모델을 호출하여 4단계 통합 파이프라인 프롬프트 품질을 검증하는 통합 테스트
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
        var realApi = new GeminiApiService(httpClient, new RealTestSettingsService(projectId), authService);

        var sessionStorage = new InMemorySessionService();
        var presetStorage = new InMemoryPresetService();
        var architectService = new SessionArchitectService(
            realApi, sessionStorage, presetStorage, new PassThroughLorebookService());

        string concept = "과거에 전설적인 모험가였지만 모종의 이유로 은퇴후 한량처럼 생활하던 카일이 의뢰중 발견한 거대한 알에서 마물소녀가 태어난다. 마물소녀와 함께하는 일상,가끔 시리어스한 내용";

        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RealSessionArchitectOutput.txt");
        File.WriteAllText(logPath, $"=== Session Architect Live Test Run ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===\nConcept: {concept}\n\n", System.Text.Encoding.UTF8);
        void Log(string msg)
        {
            output.WriteLine(msg);
            File.AppendAllText(logPath, msg + "\n", System.Text.Encoding.UTF8);
        }

        // 1. Stage 1: Planning & Master Worldview (통합 1회 호출)
        string planJson = "";
        await foreach (var chunk in architectService.GeneratePlanAsync(concept))
        {
            planJson += chunk;
        }
        var plan = architectService.ParsePlan(planJson);
        Assert.NotNull(plan);
        plan!.Concept = concept;
        Log($"[Stage 1 청사진 & 마스터 세계관 완료] 제목: {plan.SessionTitle}\n- 일상 호흡: {plan.SurfaceTone}\n- 핵심 무게감: {plan.CoreWeight}\n- 로어북 기획 수: {plan.LorebookPlan.Count}\n- 마스터 세계관:\n{plan.WorldviewOutline}");

        var session = new ArchitectSession
        {
            Plan = plan,
            GeneratedWorldview = SessionArchitectService.SanitizeWorldview(plan.WorldviewOutline)
        };
        Assert.DoesNotContain("**", session.GeneratedWorldview);
        Assert.DoesNotContain("###", session.GeneratedWorldview);

        // 2. Stage 2: 2-Wave Ensemble Causal Lorebooks (통합 2회 호출: Wave 1 무대 + Wave 2 인물 전원)
        string loreJson = "";
        await foreach (var chunk in architectService.GenerateStepContentAsync(AgentStep.LorebookGen, plan, session))
        {
            loreJson += chunk;
        }
        session.GeneratedLorebooks = architectService.ParseLorebooks(loreJson);
        Assert.NotNull(session.GeneratedLorebooks);
        Assert.NotEmpty(session.GeneratedLorebooks!);
        Log($"\n[Stage 2 2-Wave 앙상블 로어북 완료: 총 {session.GeneratedLorebooks.Count}건]");
        foreach (var l in session.GeneratedLorebooks)
        {
            int estTokens = HybridLorebookRetriever.EstimateTokens(l.Name) + HybridLorebookRetriever.EstimateTokens(l.Content) + 10;
            Log($"- {l.Name} ({l.Category}, Priority {l.Priority}, {l.Content.Length}자 / 약 {estTokens}토큰):\n{l.Content}\n");
            Assert.DoesNotContain("**", l.Content);
            Assert.InRange(l.Priority, 0, 3);
        }

        // 3. Stage 3: Opening Scenario (선행 집필 — 순수 산문 text/plain)
        string scenario = "";
        await foreach (var chunk in architectService.GenerateStepContentAsync(AgentStep.ScenarioGen, plan, session))
        {
            scenario += chunk;
        }
        session.GeneratedScenario = scenario;
        Log($"\n[Stage 3 오프닝 시나리오 완료]\n{scenario}");
        Assert.DoesNotContain("*", scenario);

        // 4. Stage 4: Status, Turn 0 Seeding & Hybrid SystemPrompt (통합 1회 호출 + C# 컴파일러)
        string statusJson = "";
        await foreach (var chunk in architectService.GenerateStepContentAsync(AgentStep.StatusGen, plan, session))
        {
            statusJson += chunk;
        }
        var statusPayload = architectService.ParseStatusDesignPayload(statusJson, session.GeneratedLorebooks);
        Assert.NotNull(statusPayload);
        session.GeneratedStats = statusPayload!.Stats;
        session.GeneratedStatusGuide = statusPayload.Guide;
        session.GeneratedInitialChars = statusPayload.InitialChars;
        session.GeneratedInitialPlaces = statusPayload.InitialPlaces;
        session.GeneratedInitialItems = statusPayload.InitialItems;
        session.GeneratedSystemPrompt = SessionArchitectService.CompileHybridSystemPrompt(
            string.Join("\n", statusPayload.GenreDirectives), plan);

        Log($"\n[Stage 4 상태창 & 시스템 프롬프트 통합 완료]\n{session.GeneratedSystemPrompt}");
        Assert.StartsWith("<system_directive>", session.GeneratedSystemPrompt);
        Assert.EndsWith("</rules>", session.GeneratedSystemPrompt);
        Assert.DoesNotContain("uiBadges", session.GeneratedSystemPrompt);

        // 5. ApplyToNewSessionAsync 및 런타임 프롬프트 조립 검증 (AI_Prompt_Engineering_Guidelines.md 16대 원칙 대조)
        string fileName = await architectService.ApplyToNewSessionAsync(session);
        var savedPreset = presetStorage.SavedPresets[fileName];
        var savedChatSession = sessionStorage.SavedSessions[fileName];

        var memoryManager = new MemoryManagerService(realApi, new RealTestSettingsService(projectId));
        memoryManager.RestoreSession(savedChatSession);

        var turn1UserAction = new ChatMessage("user", "주변을 둘러보며 눈앞의 인물에게 말을 건넨다.", DateTime.Now);
        var narrativeRequest = await memoryManager.BuildNarrativeRequestAsync(turn1UserAction, savedPreset);
        string finalSystemInstruction = narrativeRequest.SystemInstruction!.Parts[0].Text!;
        string finalDynamicPrompt = narrativeRequest.Contents.Last().Parts[0].Text!;

        Log("\n==================================================================");
        Log("[실제 런타임 조립 결과 1: SystemInstruction (Prefix-Frozen)]");
        Log("==================================================================");
        Log(finalSystemInstruction);

        Log("\n==================================================================");
        Log("[실제 런타임 조립 결과 2: Turn 1 Dynamic User Prompt]");
        Log("==================================================================");
        Log(finalDynamicPrompt);

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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealGeminiPro_MagicAcademy_GeneratesHighDensityGuidelineCompliantSession()
    {
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

        var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var authService = new GoogleAuthService();
        var realApi = new GeminiApiService(httpClient, new RealTestSettingsService(projectId), authService);

        var sessionStorage = new InMemorySessionService();
        var presetStorage = new InMemoryPresetService();
        var architectService = new SessionArchitectService(
            realApi, sessionStorage, presetStorage, new PassThroughLorebookService());

        string concept = "스탠다드한 마법아카데미물로 매력적인 히로인들 등장인물이 많이 등장하는";

        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RealMagicAcademyOutput.txt");
        File.WriteAllText(logPath, $"=== Session Architect Live Test Run ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===\nConcept: {concept}\n\n", System.Text.Encoding.UTF8);
        void Log(string msg)
        {
            output.WriteLine(msg);
            File.AppendAllText(logPath, msg + "\n", System.Text.Encoding.UTF8);
        }

        // 1. Stage 1: Planning & Master Worldview
        string planJson = "";
        await foreach (var chunk in architectService.GeneratePlanAsync(concept))
        {
            planJson += chunk;
        }
        var plan = architectService.ParsePlan(planJson);
        Assert.NotNull(plan);
        plan!.Concept = concept;
        Log($"[Stage 1 청사진 & 마스터 세계관 완료] 제목: {plan.SessionTitle}\n- 일상 호흡: {plan.SurfaceTone}\n- 핵심 무게감: {plan.CoreWeight}\n- 로어북 기획 수: {plan.LorebookPlan.Count}\n- 마스터 세계관:\n{plan.WorldviewOutline}");

        var session = new ArchitectSession
        {
            Plan = plan,
            GeneratedWorldview = SessionArchitectService.SanitizeWorldview(plan.WorldviewOutline)
        };
        Assert.DoesNotContain("**", session.GeneratedWorldview);
        Assert.DoesNotContain("###", session.GeneratedWorldview);

        // 2. Stage 2: 2-Wave Ensemble Causal Lorebooks
        string loreJson = "";
        await foreach (var chunk in architectService.GenerateStepContentAsync(AgentStep.LorebookGen, plan, session))
        {
            loreJson += chunk;
        }
        session.GeneratedLorebooks = architectService.ParseLorebooks(loreJson);
        Assert.NotNull(session.GeneratedLorebooks);
        Assert.NotEmpty(session.GeneratedLorebooks!);
        Log($"\n[Stage 2 2-Wave 앙상블 로어북 완료: 총 {session.GeneratedLorebooks.Count}건]");
        foreach (var l in session.GeneratedLorebooks)
        {
            int estTokens = HybridLorebookRetriever.EstimateTokens(l.Name) + HybridLorebookRetriever.EstimateTokens(l.Content) + 10;
            Log($"- {l.Name} ({l.Category}, Priority {l.Priority}, {l.Content.Length}자 / 약 {estTokens}토큰):\n{l.Content}\n");
            Assert.DoesNotContain("**", l.Content);
            Assert.InRange(l.Priority, 0, 3);
        }

        // 3. Stage 3: Opening Scenario
        string scenario = "";
        await foreach (var chunk in architectService.GenerateStepContentAsync(AgentStep.ScenarioGen, plan, session))
        {
            scenario += chunk;
        }
        session.GeneratedScenario = scenario;
        Log($"\n[Stage 3 오프닝 시나리오 완료]\n{scenario}");
        Assert.DoesNotContain("*", scenario);

        // 4. Stage 4: Status, Turn 0 Seeding & Hybrid SystemPrompt
        string statusJson = "";
        await foreach (var chunk in architectService.GenerateStepContentAsync(AgentStep.StatusGen, plan, session))
        {
            statusJson += chunk;
        }
        var statusPayload = architectService.ParseStatusDesignPayload(statusJson, session.GeneratedLorebooks);
        Assert.NotNull(statusPayload);
        session.GeneratedStats = statusPayload!.Stats;
        session.GeneratedStatusGuide = statusPayload.Guide;
        session.GeneratedInitialChars = statusPayload.InitialChars;
        session.GeneratedInitialPlaces = statusPayload.InitialPlaces;
        session.GeneratedInitialItems = statusPayload.InitialItems;
        session.GeneratedSystemPrompt = SessionArchitectService.CompileHybridSystemPrompt(
            string.Join("\n", statusPayload.GenreDirectives), plan);

        Log($"\n[Stage 4 상태창 & 시스템 프롬프트 통합 완료]\n{session.GeneratedSystemPrompt}");
        Assert.StartsWith("<system_directive>", session.GeneratedSystemPrompt);
        Assert.EndsWith("</rules>", session.GeneratedSystemPrompt);
        Assert.DoesNotContain("uiBadges", session.GeneratedSystemPrompt);

        // 5. ApplyToNewSessionAsync 및 런타임 프롬프트 조립 검증
        string fileName = await architectService.ApplyToNewSessionAsync(session);
        var savedPreset = presetStorage.SavedPresets[fileName];
        var savedChatSession = sessionStorage.SavedSessions[fileName];

        var memoryManager = new MemoryManagerService(realApi, new RealTestSettingsService(projectId));
        memoryManager.RestoreSession(savedChatSession);

        var turn1UserAction = new ChatMessage("user", "주변을 둘러보며 아카데미 강의실 안의 히로인들을 관찰한다.", DateTime.Now);
        var narrativeRequest = await memoryManager.BuildNarrativeRequestAsync(turn1UserAction, savedPreset);
        string finalSystemInstruction = narrativeRequest.SystemInstruction!.Parts[0].Text!;
        string finalDynamicPrompt = narrativeRequest.Contents.Last().Parts[0].Text!;

        Log("\n==================================================================");
        Log("[실제 런타임 조립 결과 1: SystemInstruction (Prefix-Frozen)]");
        Log("==================================================================");
        Log(finalSystemInstruction);

        Log("\n==================================================================");
        Log("[실제 런타임 조립 결과 2: Turn 1 Dynamic User Prompt]");
        Log("==================================================================");
        Log(finalDynamicPrompt);

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
