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
    private class FakeGeminiApiService : IGeminiApiService
    {
        public string MockResponse { get; set; } = "<plot>챕터 줄거리 요약본입니다.</plot>";
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
    }

    private class FakeLorebookService : ILorebookService
    {
        public string BuildLorebookInjection(List<LorebookEntry>? lorebooks, ChapterContext currentContext, IEnumerable<ChatMessage> recentMemory) => "";
        public Task<LorebookEntry?> ExtractMemoryToLorebookAsync(string messageText) => Task.FromResult<LorebookEntry?>(null);
    }

    private class FakeAppSettingsService : IAppSettingsService
    {
        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings("dummy-api-key"));
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public bool IsCredentialFileExists() => true;
        public Task<bool> CopyCredentialFileAsync(string sourceFilePath) => Task.FromResult(true);
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
        var fakeLorebook = new FakeLorebookService();
        var service = new MemoryManagerService(fakeApi, fakeSettings, fakeLorebook);
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
            MockResponse = "<plot>성문이 열리고 원정대가 출정했다.</plot>"
        };
        var fakeSettings = new FakeAppSettingsService();
        var fakeLorebook = new FakeLorebookService();
        var service = new MemoryManagerService(fakeApi, fakeSettings, fakeLorebook)
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
            MockResponse = "<plot>지연되어 도착한 구버전 줄거리 요약</plot>",
            DelayTcs = new TaskCompletionSource<bool>()
        };
        var fakeSettings = new FakeAppSettingsService();
        var fakeLorebook = new FakeLorebookService();
        var service = new MemoryManagerService(fakeApi, fakeSettings, fakeLorebook)
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
        var fakeLorebook = new FakeLorebookService();
        var service = new MemoryManagerService(fakeApi, fakeSettings, fakeLorebook);
        var preset = new CharacterPreset("Hero", "Fantasy World", "You are a heroic guide.");

        const string initialScenario = "용사는 마왕성 입구의 거대한 철문 앞에 도달했다.";
        service.InjectInitialScenario(initialScenario);

        var session = service.ExportSession(preset);
        Assert.Equal(2, session.History.Count);

        Assert.Equal("user", session.History[0].Role);
        Assert.Contains(initialScenario, session.History[0].Text);

        Assert.Equal("model", session.History[1].Role);
        Assert.Contains("[시스템: 해당 세계관과 초기 상황을 완벽히 인지했습니다.", session.History[1].Text);
    }

    // 5. 앵커 플래그 -> 초기값 true, 1회 소비 후 false, Clear 후 true 복원 검증
    [Fact]
    public void ConsumeAnchorFlag_InitialValueIsTrue_ThenSubsequentIsFalse()
    {
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var fakeLorebook = new FakeLorebookService();
        var service = new MemoryManagerService(fakeApi, fakeSettings, fakeLorebook);

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
        var fakeLorebook = new FakeLorebookService();
        var service = new MemoryManagerService(fakeApi, fakeSettings, fakeLorebook);

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
        var fakeLorebook = new FakeLorebookService();
        var service = new MemoryManagerService(fakeApi, fakeSettings, fakeLorebook);

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
    }
}
