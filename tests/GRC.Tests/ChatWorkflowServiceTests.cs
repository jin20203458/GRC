using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GRC.Models;
using GRC.Services;
using Xunit;

namespace GRC.Tests;

public class ChatWorkflowServiceTests
{
    private class FakeGeminiApiService : IGeminiApiService
    {
        public Task<string> SendMessageAsync(GeminiRequest request, ModelTier? overrideTier = null, CancellationToken cancellationToken = default)
            => Task.FromResult("");

        public async IAsyncEnumerable<string> SendMessageStreamAsync(
            GeminiRequest request,
            ModelTier? overrideTier = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<float>());
    }

    private class FakeMemoryManagerService : IMemoryManagerService
    {
        public string CurrentSessionFileName { get; set; } = "session.json";
        public ChapterContext CurrentContext { get; } = new();
        public string CurrentLorebookText => "";

        public Task<GeminiRequest> BuildNarrativeRequestAsync(ChatMessage userMessage, CharacterPreset preset, string? metaDirective = null)
            => Task.FromResult(new GeminiRequest(null, new List<Content>(), null, null));

        public GeminiRequest BuildStatusRequest(string userAction, string modelNarrative, string statusUpdateGuide, BlockThreshold safetyThreshold)
            => new(null, new List<Content>(), null, null);

        public void AddModelResponse(ChatMessage message) { }
        public bool ConsumeAnchorFlag() => false;
        public bool ConsumeChapterChangedFlag() => false;
        public void DeleteMessage(ChatMessage message) { }
        public void Clear() { }
        public void RestoreSession(ChatSession session) { }
        public void InjectInitialScenario(string scenarioText) { }
        public ChatSession ExportSession(CharacterPreset currentPreset)
            => new(currentPreset, new List<ChatMessage>(), new ChapterContext(), new List<ChapterContext>(), "");
        public void UpdateContextStatus(StatusPayload payload, bool replaceCustomStats = false) { }
    }

    private class FakeAppSettingsService : IAppSettingsService
    {
        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings("dummy-api-key"));
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public bool IsCredentialFileExists() => true;
        public Task<bool> CopyCredentialFileAsync(string sourceFilePath) => Task.FromResult(true);
    }

    private class FakeAudioService : IAudioService
    {
        public bool IsTypingSoundStarted { get; private set; }
        public void SetBgmState(bool isEnabled) { }
        public void SetBgmVolume(double volume) { }
        public void SetTypingSoundState(bool isEnabled) { }
        public void SetTypingSoundVolume(double volume) { }
        public void StartTypingSound() => IsTypingSoundStarted = true;
        public void StopTypingSound() => IsTypingSoundStarted = false;
        public void PlayVoiceSound(string filePath) { }
        public Task PlayVoiceSoundAsync(string filePath) => Task.CompletedTask;
        public void StopVoiceSound() { }
    }

    private static ChatWorkflowService CreateService()
    {
        return new ChatWorkflowService(
            new FakeGeminiApiService(),
            new FakeMemoryManagerService(),
            new FakeAppSettingsService(),
            new FakeAudioService()
        );
    }

    [Fact]
    public void GetMetaDirective_WhenChapterChanged_ReturnsChapterDirective()
    {
        var service = CreateService();

        var directive = service.GetMetaDirective("아무 입력", isFastForwardEnabled: false, null, null, isChapterChanged: true);

        Assert.NotNull(directive);
        Assert.Contains("[시스템 개입: 챕터 전환 (New Chapter)]", directive);
    }

    [Fact]
    public void GetMetaDirective_WhenTriggerStatReachesMax_ReturnsMaxDirectiveAndRecordsEvent()
    {
        var service = CreateService();
        var triggeredEvents = new List<string>();
        var stats = new Dictionary<string, string>
        {
            ["호감도"] = "100/100"
        };

        var directive = service.GetMetaDirective("대화 진행", isFastForwardEnabled: false, stats, triggeredEvents, isChapterChanged: false);

        Assert.NotNull(directive);
        Assert.Contains("[시스템 개입: 극적 상황 발생 (수치 도달)]", directive);
        Assert.Contains("호감도", directive);
        Assert.Contains("100(최대치)", directive);
        Assert.Contains("호감도", triggeredEvents);
    }

    [Fact]
    public void GetMetaDirective_WhenTriggerStatReachesMin_ReturnsMinDirectiveAndRecordsEvent()
    {
        var service = CreateService();
        var triggeredEvents = new List<string>();
        var stats = new Dictionary<string, string>
        {
            ["이성"] = "0/100"
        };

        var directive = service.GetMetaDirective("대화 진행", isFastForwardEnabled: false, stats, triggeredEvents, isChapterChanged: false);

        Assert.NotNull(directive);
        Assert.Contains("[시스템 개입: 극적 상황 발생 (수치 상실 및 소멸)]", directive);
        Assert.Contains("이성", directive);
        Assert.Contains("이성", triggeredEvents);
    }

    [Fact]
    public void GetMetaDirective_WhenFastForwardEnabled_ReturnsFastForwardDirective()
    {
        var service = CreateService();

        var directive = service.GetMetaDirective("빠른 진행 요청", isFastForwardEnabled: true, null, null, isChapterChanged: false);

        Assert.NotNull(directive);
        Assert.Contains("[시스템 개입: 서사 압축 및 고속 전개(Fast-Forward)]", directive);
    }

    [Fact]
    public void GetMetaDirective_WhenInputBlank_ReturnsAutoContinueDirective()
    {
        var service = CreateService();

        var directive = service.GetMetaDirective("", isFastForwardEnabled: false, null, null, isChapterChanged: false);

        Assert.NotNull(directive);
        Assert.Contains("[시스템 명령: 서사 이어쓰기 (Auto-Continue)]", directive);
    }
}
