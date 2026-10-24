using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GRC.Models;
using GRC.Services;
using Xunit;

namespace GRC.Tests;

public class MemoryManagerServiceTests
{
    internal class FakeGeminiApiService : IGeminiApiService
    {
        public string MockResponse { get; set; } = "{\n  \"bullets\": [\"챕터 줄거리 요약본입니다.\"]\n}";
        public int SendMessageCallCount { get; private set; }
        public TaskCompletionSource<bool>? DelayTcs { get; set; }

        public async Task<string> SendMessageAsync(GeminiRequest request, ModelTier? overrideTier = null, CancellationToken cancellationToken = default)
        {
            SendMessageCallCount++;
            if (DelayTcs != null)
            {
                await DelayTcs.Task;
            }
            return MockResponse;
        }

        public async IAsyncEnumerable<string> SendMessageStreamAsync(
            GeminiRequest request,
            ModelTier? overrideTier = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public float[]? MockEmbedding { get; set; }

        public Task<float[]> GetEmbeddingAsync(string text, string? taskType = null, CancellationToken cancellationToken = default)
            => Task.FromResult(MockEmbedding ?? Array.Empty<float>());
    }

    internal class FakeLorebookService : ILorebookService
    {
        public Task<LorebookEntry?> ExtractMemoryToLorebookAsync(string messageText) => Task.FromResult<LorebookEntry?>(null);
        public Task<List<LorebookEntry>> EnsureEmbeddingsAsync(IEnumerable<LorebookEntry>? lorebooks, CancellationToken cancellationToken = default)
            => Task.FromResult(lorebooks?.ToList() ?? new List<LorebookEntry>());
    }

    internal class FakeAppSettingsService : IAppSettingsService
    {
        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings("dummy-api-key"));
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public bool IsCredentialFileExists() => true;
        public Task<bool> CopyCredentialFileAsync(string sourceFilePath) => Task.FromResult(true);
    }

    internal class SpyHybridLorebookRetriever : IHybridLorebookRetriever
    {
        public string? LastUserQuery { get; private set; }
        public float[]? LastQueryEmbedding { get; private set; }
        public List<string>? LastActiveCharNames { get; private set; }
        public List<LorebookEntry> ReturnLorebooks { get; set; } = [];
        public List<LorebookEntry> Retrieve(string userQuery, float[]? queryEmbedding, IEnumerable<LorebookEntry> allLorebooks, IEnumerable<string>? activeCharNames = null, int maxTokenBudget = 3000)
        {
            LastUserQuery = userQuery;
            LastQueryEmbedding = queryEmbedding;
            LastActiveCharNames = activeCharNames?.ToList();
            return ReturnLorebooks;
        }

        public void ClearSticky() { }
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, int timeoutMs = 7000, int pollIntervalMs = 100)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(pollIntervalMs);
        }
        return condition();
    }

    // 1. 단기 기억 18턴 미만 -> 압축 미발생, 턴 수 정상 누적 검증
    [Fact]
    public async Task BuildNarrativeRequestAsync_BelowThreshold_DoesNotTriggerCompression()
    {
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);
        var preset = new CharacterPreset("Hero", "Fantasy World", "You are a heroic guide.");

        // 17턴 (임계치 18턴 미만) 누적
        for (int i = 1; i <= 17; i++)
        {
            await service.BuildNarrativeRequestAsync(new ChatMessage("user", $"User action {i}", DateTime.Now), preset);
        }

        // 압축이 트리거되지 않았으므로 요약 API 호출 횟수 0
        Assert.Equal(0, fakeApi.SendMessageCallCount);

        var session = service.ExportSession(preset);
        Assert.Equal(17, session.History.Count);
        Assert.Equal("새로운 챕터가 시작되었습니다.", session.CurrentContext.Plot);
    }

    // 2. 단기 기억 18턴 도달 -> 비동기 중기 압축 트리거 및 최상위 8턴 절삭 슬라이딩 윈도우 검증
    [Fact]
    public async Task BuildNarrativeRequestAsync_Reaching18Turns_TriggersCompressionAndTrims8Turns()
    {
        var fakeApi = new FakeGeminiApiService
        {
            MockResponse = "{\n  \"bullets\": [\"성문이 열리고 원정대가 출정했다.\"]\n}"
        };
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings)
        {
            CompressionDelayMs = 10
        };
        var preset = new CharacterPreset("Hero", "Fantasy World", "You are a heroic guide.");

        // 18턴 누적
        for (int i = 1; i <= 18; i++)
        {
            await service.BuildNarrativeRequestAsync(new ChatMessage("user", $"User action {i}", DateTime.Now), preset);
        }

        // 18턴 도달 시 백그라운드 태스크(지연 압축 후 요약) 트리거 -> 슬라이딩 윈도우로 8턴 절삭되어 10턴 잔여
        bool compressed = await WaitForConditionAsync(
            () => service.ExportSession(preset).History.Count == 10,
            timeoutMs: 3000);

        Assert.True(compressed, "18턴 도달 후 지연 압축을 거쳐 8턴이 절삭되고 10턴이 남아야 합니다.");
        Assert.True(fakeApi.SendMessageCallCount >= 1, "중기 요약 API가 1회 이상 호출되어야 합니다.");

        var session = service.ExportSession(preset);
        Assert.Contains("성문이 열리고 원정대가 출정했다.", session.CurrentContext.Plot);
        Assert.StartsWith("[제 1장]", session.CurrentContext.Plot);
    }

    // 3. 요약 통신 지연 중 유저의 Clear() 호출 -> _shortTermVersion 불일치로 지연된 요약 폐기(낙관적 락) 검증
    [Fact]
    public async Task BuildNarrativeRequestAsync_WhenVersionMismatchOccurs_DiscardsStaleSummary()
    {
        var fakeApi = new FakeGeminiApiService
        {
            MockResponse = "{\n  \"bullets\": [\"지연되어 도착한 구버전 줄거리 요약\"]\n}",
            DelayTcs = new TaskCompletionSource<bool>()
        };
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings)
        {
            CompressionDelayMs = 10
        };
        var preset = new CharacterPreset("Hero", "Fantasy World", "You are a heroic guide.");

        // 18턴 주입하여 압축 트리거
        for (int i = 1; i <= 18; i++)
        {
            await service.BuildNarrativeRequestAsync(new ChatMessage("user", $"Turn {i}", DateTime.Now), preset);
        }

        // 백그라운드 태스크가 10ms 딜레이를 통과하고 apiService.SendMessageAsync에 진입할 때까지 대기
        bool reachedApi = await WaitForConditionAsync(() => fakeApi.SendMessageCallCount > 0, timeoutMs: 3000);
        Assert.True(reachedApi, "중기 압축 태스크가 API 통신 단계에 도달해야 합니다.");

        // API 응답 수신 대기 중에 세션 초기화(Clear) 호출 -> _shortTermVersion 증가
        service.Clear();

        // 지연되었던 구버전 API 응답 방출
        fakeApi.DelayTcs.SetResult(true);

        // 비동기 작업이 정리될 때까지 잠시 대기
        await Task.Delay(50);

        // 낙관적 락에 의해 구버전 요약이 버려졌으므로 Clear 상태 유지 검증
        Assert.Equal("[제 1장] 새로운 이야기가 시작되었습니다.", service.CurrentContext.Plot);
        Assert.DoesNotContain("지연되어 도착한 구버전 줄거리 요약", service.CurrentContext.Plot);
        Assert.Empty(service.ExportSession(preset).History);
    }

    // 4. 초기 상황 주입 -> 단기 기억 0번 인덱스에 배치 및 세션 복원 시 보존 검증
    [Fact]
    public void InjectInitialScenario_PlacesScenarioAtBeginning_PreservedInExport()
    {
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);
        var preset = new CharacterPreset("Hero", "Fantasy World", "You are a heroic guide.");

        const string initialScenario = "용사는 마왕성 입구의 거대한 철문 앞에 도달했다.";
        service.InjectInitialScenario(initialScenario);

        var session = service.ExportSession(preset);
        Assert.Equal(2, session.History.Count);

        Assert.Equal("user", session.History[0].Role);
        Assert.Contains(initialScenario, session.History[0].Text);

        Assert.Equal("model", session.History[1].Role);
        Assert.Contains("주어진 세계관과 초기 상황의 흐름을 받아들여", session.History[1].Text);
    }

    // 5. 앵커 플래그 -> 초기값 true, 1회 소비 후 false, Clear 후 true 복원 검증
    [Fact]
    public void ConsumeAnchorFlag_InitialValueIsTrue_ThenSubsequentIsFalse()
    {
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);

        // 1. 초기 상태: 앵커 플래그는 true
        Assert.True(service.ConsumeAnchorFlag());

        // 2. 1회 소비 후: 원자적으로 리셋되어 false
        Assert.False(service.ConsumeAnchorFlag());

        // 3. Clear 호출 시: 앵커 플래그가 다시 1(true)로 복원
        service.Clear();
        Assert.True(service.ConsumeAnchorFlag());
        Assert.False(service.ConsumeAnchorFlag());
    }

    // 6. 챕터 전환 플래그 -> 1회 소비(ConsumeChapterChangedFlag) 시 원자적 리셋(Interlocked.Exchange) 검증
    [Fact]
    public void ConsumeChapterChangedFlag_ExchangesAtomically()
    {
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);

        // 초기 상태: false
        Assert.False(service.ConsumeChapterChangedFlag());

        // Reflection으로 _isChapterChanged = 1 설정하여 챕터 전환 이벤트 모의
        var field = typeof(MemoryManagerService).GetField("_isChapterChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(service, 1);

        // 첫 번째 소비: true 반환 및 0으로 원자적 교환(Interlocked.Exchange)
        Assert.True(service.ConsumeChapterChangedFlag());

        // 두 번째 소비: 이미 0이므로 false 반환
        Assert.False(service.ConsumeChapterChangedFlag());
    }

    // 7. 상태창 갱신 -> CustomStats, Chars, Items, Places 병합 및 대체 모드 검증
    [Fact]
    public void UpdateContextStatus_MergesCustomStatsCharsItemsPlacesCorrectly()
    {
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);

        // 1. 1차 상태 업데이트
        var payload1 = new StatusPayload
        {
            CustomStats = new Dictionary<string, string> { ["HP"] = "100/100", ["MP"] = "50/50" },
            Chars = new Dictionary<string, string> { ["엘리아"] = "정상" },
            Items = new List<string> { "강철검", "회복약" },
            Places = new List<string> { "시작의 마을" }
        };
        service.UpdateContextStatus(payload1, replaceCustomStats: false);

        Assert.Equal("100/100", service.CurrentContext.CustomStats["HP"]);
        Assert.Equal("50/50", service.CurrentContext.CustomStats["MP"]);
        Assert.Equal("정상", service.CurrentContext.Chars["엘리아"]);
        Assert.Contains("강철검", service.CurrentContext.Items);
        Assert.Contains("시작의 마을", service.CurrentContext.Places);

        // 2. 2차 상태 업데이트 (replaceCustomStats: false -> 병합 모드)
        var payload2 = new StatusPayload
        {
            CustomStats = new Dictionary<string, string> { ["HP"] = "80/100", ["Gold"] = "500" },
            Chars = new Dictionary<string, string> { ["엘리아"] = "경미한 부상" },
            Items = new List<string> { "마법 지팡이" },
            Places = new List<string> { "어둠의 숲" }
        };
        service.UpdateContextStatus(payload2, replaceCustomStats: false);

        Assert.Equal("80/100", service.CurrentContext.CustomStats["HP"]); // 갱신
        Assert.Equal("50/50", service.CurrentContext.CustomStats["MP"]);   // 기존 유지
        Assert.Equal("500", service.CurrentContext.CustomStats["Gold"]);   // 신규 추가
        Assert.Equal("경미한 부상", service.CurrentContext.Chars["엘리아"]);
        Assert.Single(service.CurrentContext.Items);
        Assert.Equal("마법 지팡이", service.CurrentContext.Items[0]); // Items는 새 리스트로 대체
        Assert.Single(service.CurrentContext.Places);
        Assert.Equal("어둠의 숲", service.CurrentContext.Places[0]);  // Places는 새 리스트로 대체

        // 3. 3차 상태 업데이트 (replaceCustomStats: true -> 완전 교체 모드)
        var payload3 = new StatusPayload
        {
            CustomStats = new Dictionary<string, string> { ["Status"] = "각성 상태" }
        };
        service.UpdateContextStatus(payload3, replaceCustomStats: true);

        Assert.Single(service.CurrentContext.CustomStats);
        Assert.Equal("각성 상태", service.CurrentContext.CustomStats["Status"]);
        Assert.False(service.CurrentContext.CustomStats.ContainsKey("HP"));

        // 4. 4차 상태 업데이트 (빈 리스트로 갱신 시 인벤토리 정상 비워짐 검증)
        var payload4 = new StatusPayload
        {
            Items = new List<string>()
        };
        service.UpdateContextStatus(payload4, replaceCustomStats: false);
        Assert.Empty(service.CurrentContext.Items);
    }

    [Fact]
    public void UpdateContextStatus_ClampsGaugeStatsCorrectly()
    {
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);

        var payload = new StatusPayload
        {
            CustomStats = new Dictionary<string, string>
            {
                ["OverHP"] = "120/100",
                ["UnderHP"] = "-15/100",
                ["NormalHP"] = "50/100",
                ["NumericGold"] = "500",
                ["TextState"] = "부상"
            }
        };

        service.UpdateContextStatus(payload);

        Assert.Equal("100/100", service.CurrentContext.CustomStats["OverHP"]);
        Assert.Equal("0/100", service.CurrentContext.CustomStats["UnderHP"]);
        Assert.Equal("50/100", service.CurrentContext.CustomStats["NormalHP"]);
        Assert.Equal("500", service.CurrentContext.CustomStats["NumericGold"]);
        Assert.Equal("부상", service.CurrentContext.CustomStats["TextState"]);
    }

    [Fact]
    public async Task BuildNarrativeRequestAsync_DeduplicatesMatchedCharacterStatsFromRemainingCustomStats()
    {
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);

        service.UpdateContextStatus(new StatusPayload
        {
            Chars = new Dictionary<string, string> { ["엘리아스"] = "쇠약" },
            CustomStats = new Dictionary<string, string>
            {
                ["엘리아스"] = "45%",
                ["체력"] = "100/100"
            },
            Items = new List<string> { "흑철검" },
            Places = new List<string> { "북부 성채" }
        });

        var preset = new CharacterPreset(
            Name: "테스트",
            Worldview: "세계관",
            SystemPrompt: "시스템",
            Temperature: 0.7f,
            MaxOutputTokens: 1000
        );

        var request = await service.BuildNarrativeRequestAsync(new ChatMessage("user", "행동", DateTime.Now), preset);
        string userPayload = request.Contents.Last().Parts.FirstOrDefault()?.Text ?? string.Empty;

        // 엘리아스는 엔티티에 바인딩
        Assert.Contains("- 인물(엘리아스): 쇠약 (45%)", userPayload);
        // 체력은 주요 수치에 포함
        Assert.Contains("- 주요 수치: 체력: 100/100", userPayload);
        // 엘리아스는 주요 수치에 중복 포함되지 않아야 함
        Assert.DoesNotContain("- 주요 수치: 엘리아스: 45%", userPayload);
    }

    [Fact]
    public async Task BuildNarrativeRequestAsync_PrefixMatchesPrefixedCharacterStats_AndBindsToEntitySlot()
    {
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);

        service.UpdateContextStatus(new StatusPayload
        {
            Chars = new Dictionary<string, string>
            {
                ["엘레나 발렌타인"] = "단정한 제복 차림으로 관찰 중"
            },
            CustomStats = new Dictionary<string, string>
            {
                ["엘레나 발렌타인 신뢰도"] = "60/100",
                ["체력"] = "100/100"
            },
            Items = new List<string> { "낡은 모험가 단검" },
            Places = new List<string> { "카일의 오두막" }
        });

        var preset = new CharacterPreset(
            Name: "테스트",
            Worldview: "세계관",
            SystemPrompt: "시스템",
            Temperature: 0.7f,
            MaxOutputTokens: 1000
        );

        var request = await service.BuildNarrativeRequestAsync(new ChatMessage("user", "오두막 문을 연다.", DateTime.Now), preset);
        string userPayload = request.Contents.Last().Parts.FirstOrDefault()?.Text ?? string.Empty;

        // 1. 엘레나 발렌타인 신뢰도가 엔티티 슬롯에 (신뢰도: 60/100)으로 바인딩되었는지 검증
        Assert.Contains("- 인물(엘레나 발렌타인): 단정한 제복 차림으로 관찰 중 (신뢰도: 60/100)", userPayload);
        // 2. 주요 수치에는 비인물 스탯(체력)만 남고 엘레나 발렌타인 신뢰도는 중복 제거되었는지 검증
        Assert.Contains("- 주요 수치: 체력: 100/100", userPayload);
        Assert.DoesNotContain("엘레나 발렌타인", userPayload.Split("<current_state>")[1].Split("</current_state>")[0]);
        // 3. 소지품 및 현재 위치가 정상 주입되었는지 검증
        Assert.Contains("- 소지품: 낡은 모험가 단검", userPayload);
        Assert.Contains("- 현재 위치: 카일의 오두막", userPayload);
    }

    [Fact]
    public async Task BuildNarrativeRequestAsync_FirstTokenPrefixMatchesShortenedCharacterStats_AndBindsToEntitySlot()
    {
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);

        service.UpdateContextStatus(new StatusPayload
        {
            Chars = new Dictionary<string, string>
            {
                ["아리아 폰 블랑셰"] = "검자루에 손을 얹고 대련 재촉 중",
                ["시엘 클로버"] = "샌드위치를 내민 채 신경전 중"
            },
            CustomStats = new Dictionary<string, string>
            {
                ["아리아 호감도"] = "25/100",
                ["시엘 호감도"] = "70/100",
                ["파티 골드"] = "250 G"
            },
            Items = new List<string> { "무명의 흑철 마도서" },
            Places = new List<string> { "제1 마도 결투장 슈발리에" }
        });

        var preset = new CharacterPreset(
            Name: "테스트",
            Worldview: "세계관",
            SystemPrompt: "시스템",
            Temperature: 0.7f,
            MaxOutputTokens: 1000
        );

        var request = await service.BuildNarrativeRequestAsync(new ChatMessage("user", "광장을 둘러본다.", DateTime.Now), preset);
        string userPayload = request.Contents.Last().Parts.FirstOrDefault()?.Text ?? string.Empty;

        // 1. 첫 어절 약칭 스탯("아리아 호감도", "시엘 호감도")이 풀네임 인물 슬롯에 정상 바인딩되었는지 검증
        Assert.Contains("- 인물(아리아 폰 블랑셰): 검자루에 손을 얹고 대련 재촉 중 (호감도: 25/100)", userPayload);
        Assert.Contains("- 인물(시엘 클로버): 샌드위치를 내민 채 신경전 중 (호감도: 70/100)", userPayload);

        // 2. 주요 수치에는 비인물 스탯("파티 골드")만 남고 인물 스탯은 중복 제거되었는지 검증
        Assert.Contains("- 주요 수치: 파티 골드: 250 G", userPayload);
        string currentStateBlock = userPayload.Split("<current_state>")[1].Split("</current_state>")[0];
        Assert.DoesNotContain("아리아", currentStateBlock);
        Assert.DoesNotContain("시엘", currentStateBlock);
    }

    [Fact]
    public async Task BuildNarrativeRequestAsync_ExcludesVisible15LedgersFromRAGPool()
    {
        // Arrange
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);

        var preset = new CharacterPreset(
            Name: "테스트",
            Worldview: "세계관",
            SystemPrompt: "시스템",
            Temperature: 0.7f,
            MaxOutputTokens: 1000
        );

        // 현재 챕터에 렛저 10개 추가 (상시 노출 15개 이하)
        for (int i = 1; i <= 10; i++)
        {
            service.CurrentContext.EventLedgers.Add(new EventLedgerItem($"현재 사건 {i}", Chapter: 1));
        }

        // Act - 과거 앵커 질문 ("옛날 약속 기억나?")
        var request = await service.BuildNarrativeRequestAsync(
            new ChatMessage("user", "옛날 그 약속 기억나?", DateTime.Now), preset);

        // Assert - 10개는 상시 노출 영역(<internal_chronicle_facts>)에 있으므로,
        // <recalled_history>에 중복 인출되지 않아야 함 (태그 미생성)
        var userPart = request.Contents[^1].Parts[0].Text!;
        Assert.Contains("<internal_chronicle_facts", userPart);
        Assert.DoesNotContain("<recalled_history", userPart);
    }

    [Fact]
    public async Task BuildNarrativeRequestAsync_WhenLedgersExceed15_IncludesPushedOutLedgersInRAGPool()
    {
        // Arrange
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);

        var preset = new CharacterPreset(
            Name: "테스트",
            Worldview: "세계관",
            SystemPrompt: "시스템",
            Temperature: 0.7f,
            MaxOutputTokens: 1000
        );

        // 현재 챕터에 렛저 18개 추가 (1~3번은 15개 밖으로 밀려남)
        for (int i = 1; i <= 18; i++)
        {
            service.CurrentContext.EventLedgers.Add(new EventLedgerItem($"사건 기록 {i}", Chapter: 1));
        }

        // Act - 유저 입력
        var request = await service.BuildNarrativeRequestAsync(
            new ChatMessage("user", "다음 행동을 진행한다.", DateTime.Now), preset);

        var userPart = request.Contents[^1].Parts[0].Text!;
        // 상시 노출 영역에는 최근 15개(4~18번)만 노출되어야 함 (Turn 메타 태그 제외)
        Assert.DoesNotContain("[Turn", userPart);
        Assert.Contains("- 사건 기록 18", userPart);
        // 1~3번은 상시 노출(최근 15개)에서 밀려났으므로 포함되지 않아야 함
        string normalizedUserPart = userPart.Replace("\r\n", "\n");
        Assert.DoesNotContain("- 사건 기록 1\n", normalizedUserPart);
        Assert.DoesNotContain("- 사건 기록 2\n", normalizedUserPart);
        Assert.DoesNotContain("- 사건 기록 3\n", normalizedUserPart);
    }

    [Fact]
    public async Task BuildNarrativeRequestAsync_WhenUserRecallsPushedOutLedger_RecallsOnlyPushedOutLedgerViaRAG()
    {
        // Arrange - 768차원 임베딩을 제공하는 Fake API 설정
        var targetEmb = new float[768];
        for (int i = 0; i < 768; i++) targetEmb[i] = 1.0f / MathF.Sqrt(768);

        var otherEmb = new float[768];
        otherEmb[0] = 1.0f; // targetEmb와 다른 방향

        var fakeApi = new FakeGeminiApiService { MockEmbedding = targetEmb };
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);

        var preset = new CharacterPreset(
            Name: "테스트",
            Worldview: "세계관",
            SystemPrompt: "시스템",
            Temperature: 0.7f,
            MaxOutputTokens: 1000
        );

        // 1번 렛저: 밀려날 대상이며 targetEmb를 장착 (과거의 펜던트 약속)
        service.CurrentContext.EventLedgers.Add(new EventLedgerItem("루시엘과 맹세한 푸른 펜던트의 서약", targetEmb, Chapter: 1));

        // 2~18번 렛저들: 상시 노출 영역에 들어갈 최근 17개 렛저들 (otherEmb 장착)
        for (int i = 2; i <= 18; i++)
        {
            service.CurrentContext.EventLedgers.Add(new EventLedgerItem($"사건 기록 {i}", otherEmb, Chapter: 1));
        }

        // Act - 유저가 질문형이 아닌 순수 평서문 행동으로 과거 아이템 조작 ("루시엘에게 푸른 펜던트를 건넨다.")
        // 키워드 게이트(ShouldTriggerRAG)를 거치지 않고 순수 시맨틱 임베딩 유사도로 RAG가 직결 발동해야 함
        var request = await service.BuildNarrativeRequestAsync(
            new ChatMessage("user", "루시엘에게 푸른 펜던트를 건넨다.", DateTime.Now), preset);

        var userPart = request.Contents[^1].Parts[0].Text!;

        // Assert 1: RAG 태그(<recalled_history>)가 프롬프트에 정상 생성되어야 함
        Assert.Contains("<recalled_history", userPart);
        Assert.DoesNotContain("[Turn", userPart);

        // Assert 2: 상시 노출에서 밀려났던 1번 렛저가 RAG를 통해 <recalled_history> 안으로 쏙 소환되어야 함!
        Assert.Contains("루시엘과 맹세한 푸른 펜던트의 서약", userPart);

        // Assert 3: 상시 노출 영역(<internal_chronicle_facts>)에는 1번 렛저가 없고, 최신 15개(4~18번)만 존재해야 함
        int internalStartIndex = userPart.IndexOf("<internal_chronicle_facts");
        int internalEndIndex = userPart.IndexOf("</internal_chronicle_facts>");
        string internalSection = userPart.Substring(internalStartIndex, internalEndIndex - internalStartIndex);
        Assert.DoesNotContain("루시엘과 맹세한 푸른 펜던트의 서약", internalSection);
        Assert.Contains("- 사건 기록 18", internalSection);
    }

    [Fact]
    public void RestoreSession_RestoresStateCorrectly()
    {
        // Arrange
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);
        var preset = new CharacterPreset("Hero", "World", "System");

        var currentContext = new ChapterContext
        {
            EventLedgers = [
                new(FactBullet: "1장 불릿 1", Chapter: 1),
                new(FactBullet: "2장 불릿 1", Chapter: 2)
            ]
        };

        var session = new ChatSession(
            Preset: preset,
            History: [new ChatMessage("user", "대화", DateTime.Now)],
            CurrentContext: currentContext,
            LongTermSummary: "요약",
            ChapterCount: 2,
            TotalTurnCount: 3,
            MediumTermUpdateCount: 1
        );

        // Act
        service.RestoreSession(session);

        // Assert
        Assert.Equal(2, service.CurrentContext.EventLedgers.Count);
        Assert.Equal("1장 불릿 1", service.CurrentContext.EventLedgers[0].FactBullet);
        Assert.Equal("2장 불릿 1", service.CurrentContext.EventLedgers[1].FactBullet);
        Assert.Equal("요약", service.ExportSession(preset).LongTermSummary);
    }

    [Fact]
    public async Task BuildNarrativeRequestAsync_CombinesLastModelMessageAndUserQuery_ForLoreScanQuery()
    {
        // Arrange
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var spyLorebook = new SpyHybridLorebookRetriever();
        var service = new MemoryManagerService(
            fakeApi,
            fakeSettings,
            new CognitivePromptAssembler(),
            new EventLedgerExtractor(fakeApi),
            spyLorebook,
            new GatedRAGRetriever()
        );

        var preset = new CharacterPreset(
            Name: "Hero",
            Worldview: "Fantasy",
            SystemPrompt: "System",
            Lorebooks: [new LorebookEntry("성검", ["성검"], "빛의 성검", "아이템")]
        );

        // 이전 턴의 AI 응답 추가
        service.AddModelResponse(new ChatMessage("model", "어둠 속에서 고대의 성검이 희미하게 빛났다.", DateTime.Now));

        // Act - 유저는 성검을 직접 언급하지 않고 지시대명사로 질문
        await service.BuildNarrativeRequestAsync(new ChatMessage("user", "그것을 손에 쥐어본다.", DateTime.Now), preset);

        // Assert - spyLorebook.LastUserQuery에 직전 model 응답과 유저 입력이 결합되어 있어야 함
        Assert.NotNull(spyLorebook.LastUserQuery);
        Assert.Contains("고대의 성검이 희미하게 빛났다.", spyLorebook.LastUserQuery);
        Assert.Contains("그것을 손에 쥐어본다.", spyLorebook.LastUserQuery);
    }

    [Fact]
    public async Task BuildNarrativeRequestAsync_WideWindowActiveChars_IncludesShortTermAndLedgerChars_ExcludesRetiredChars()
    {
        // Arrange
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var spyLorebook = new SpyHybridLorebookRetriever();
        var service = new MemoryManagerService(
            fakeApi,
            fakeSettings,
            new CognitivePromptAssembler(),
            new EventLedgerExtractor(fakeApi),
            spyLorebook,
            new GatedRAGRetriever()
        );

        var preset = new CharacterPreset(
            Name: "Hero",
            Worldview: "Fantasy",
            SystemPrompt: "System",
            Lorebooks: [new LorebookEntry("엘리스", ["엘리스"], "마법사", "인물")]
        );

        // 3명의 인물이 상태창에 등록되어 있음
        service.CurrentContext.Chars["루시엘"] = "동행 중";
        service.CurrentContext.Chars["세레나"] = "대기 중";
        service.CurrentContext.Chars["카일"] = "1장 마을에 잔류함 (퇴장 조연)";

        // 1. 루시엘은 단기 기억(short-term memory) 6개 메시지 전(최근 4개 밖)에 등장
        service.AddModelResponse(new ChatMessage("model", "루시엘이 지팡이를 건넸다.", DateTime.Now));
        service.AddModelResponse(new ChatMessage("user", "고마워.", DateTime.Now));
        service.AddModelResponse(new ChatMessage("model", "그녀는 살짝 웃었다.", DateTime.Now));
        service.AddModelResponse(new ChatMessage("user", "출발하자.", DateTime.Now));
        service.AddModelResponse(new ChatMessage("model", "앞장서서 걸어갔다.", DateTime.Now));

        // 2. 세레나는 단기 기억 원문에는 없으나, 최근 팩트 불릿(EventLedgers.TakeLast(3))에 기록됨
        service.CurrentContext.EventLedgers.Add(new EventLedgerItem("세레나가 북쪽 성문 경비를 지원하기로 합의함", Chapter: 1));

        // 3. 카일은 단기 기억에도 없고, 최근 팩트 불릿에도 전혀 언급되지 않음

        // Act - 유저는 대명사로만 행동
        await service.BuildNarrativeRequestAsync(new ChatMessage("user", "그녀의 뒤를 조용히 따른다.", DateTime.Now), preset);

        // Assert
        Assert.NotNull(spyLorebook.LastActiveCharNames);
        // 단기 기억에 있던 루시엘과 최근 불릿에 있던 세레나는 activeChars에 포함되어야 함
        Assert.Contains("루시엘", spyLorebook.LastActiveCharNames);
        Assert.Contains("세레나", spyLorebook.LastActiveCharNames);
        // 20턴 이상 지나 단기 기억과 최근 불릿 어디에도 없는 카일은 activeChars에서 제외되어야 함
        Assert.DoesNotContain("카일", spyLorebook.LastActiveCharNames);
    }

    [Fact]
    public async Task BuildNarrativeRequestAsync_PopulatesCurrentActiveLorebooks_WithMatchedLorebookObjects()
    {
        // Arrange
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var spyLorebook = new SpyHybridLorebookRetriever();
        var service = new MemoryManagerService(
            fakeApi,
            fakeSettings,
            new CognitivePromptAssembler(),
            new EventLedgerExtractor(fakeApi),
            spyLorebook,
            new GatedRAGRetriever()
        );

        var heroEntry = new LorebookEntry("엘리아스", ["엘리아스"], "차가운 마법사", "인물");
        var itemEntry = new LorebookEntry("성검", ["성검"], "빛의 성검", "아이템");
        spyLorebook.ReturnLorebooks = [heroEntry, itemEntry];

        var preset = new CharacterPreset(
            Name: "Hero",
            Worldview: "Fantasy",
            SystemPrompt: "System",
            Lorebooks: [heroEntry, itemEntry]
        );

        // 초기 상태 검증
        Assert.Empty(service.CurrentActiveLorebooks);

        // Act - 서사 요청 빌드
        await service.BuildNarrativeRequestAsync(new ChatMessage("user", "엘리아스가 성검을 쥔다.", DateTime.Now), preset);

        // Assert - CurrentActiveLorebooks에 인출된 객체 목록이 그대로 동기화되어 있어야 함
        Assert.Equal(2, service.CurrentActiveLorebooks.Count);
        Assert.Same(heroEntry, service.CurrentActiveLorebooks[0]);
        Assert.Same(itemEntry, service.CurrentActiveLorebooks[1]);

        // Clear 호출 시 컬렉션이 안전하게 초기화되는지 검증
        service.Clear();
        Assert.Empty(service.CurrentActiveLorebooks);

        // RestoreSession 호출 시 컬렉션이 안전하게 초기화되는지 검증
        service.RestoreSession(new ChatSession(preset, [], new ChapterContext(), ""));
        Assert.Empty(service.CurrentActiveLorebooks);
    }

    [Fact]
    public void BuildStatusRequest_WithCustomRules_RendersIndependentCustomRulesBlock()
    {
        var service = new MemoryManagerService(
            new FakeGeminiApiService(),
            new FakeAppSettingsService(),
            new CognitivePromptAssembler(),
            new EventLedgerExtractor(new FakeGeminiApiService()),
            new SpyHybridLorebookRetriever(),
            new GatedRAGRetriever()
        );

        string guide = "1. 타락도: 악행 시 5 증가.\n2. 신앙심: 기도 시 5 증가.";
        var req = service.BuildStatusRequest("검을 휘두른다.", "도적이 쓰러졌다.", guide, BlockThreshold.BLOCK_NONE);
        string systemText = req.SystemInstruction?.Parts.FirstOrDefault()?.Text ?? "";

        Assert.Contains("<custom_rules>\n1. 타락도: 악행 시 5 증가.\n2. 신앙심: 기도 시 5 증가.\n</custom_rules>", systemText.Replace("\r\n", "\n"));
        Assert.DoesNotContain("7. 추가 지침:", systemText);
    }

    [Fact]
    public void BuildStatusRequest_WithoutCustomRules_OmitsCustomRulesBlock()
    {
        var service = new MemoryManagerService(
            new FakeGeminiApiService(),
            new FakeAppSettingsService(),
            new CognitivePromptAssembler(),
            new EventLedgerExtractor(new FakeGeminiApiService()),
            new SpyHybridLorebookRetriever(),
            new GatedRAGRetriever()
        );

        var req = service.BuildStatusRequest("검을 휘두른다.", "도적이 쓰러졌다.", "", BlockThreshold.BLOCK_NONE);
        string systemText = req.SystemInstruction?.Parts.FirstOrDefault()?.Text ?? "";

        Assert.DoesNotContain("<custom_rules>", systemText);
        Assert.DoesNotContain("[CUSTOM_RULES]", systemText);
    }

    [Fact]
    public void GetCurrentShortTermMemory_ReturnsSnapshotOfActiveShortTermMessages()
    {
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);
        service.AddModelResponse(new ChatMessage("model", "첫 번째 응답", DateTime.Now));
        service.AddModelResponse(new ChatMessage("user", "두 번째 질문", DateTime.Now));

        var snapshot = service.GetCurrentShortTermMemory();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal("첫 번째 응답", snapshot[0].Text);
        Assert.Equal("두 번째 질문", snapshot[1].Text);
    }
}


