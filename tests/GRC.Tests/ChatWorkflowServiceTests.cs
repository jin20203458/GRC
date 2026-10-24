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

        public Task<float[]> GetEmbeddingAsync(string text, string? taskType = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<float>());
    }

    private class FakeMemoryManagerService : IMemoryManagerService
    {
        public string CurrentSessionFileName { get; set; } = "session.json";
        public ChapterContext CurrentContext { get; } = new();
        public IReadOnlyList<LorebookEntry> CurrentActiveLorebooks => [];

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
            => new(currentPreset, new List<ChatMessage>(), new ChapterContext(), "");
        public void UpdateContextStatus(StatusPayload payload, bool replaceCustomStats = false) { }
        public IReadOnlyList<ChatMessage> GetCurrentShortTermMemory() => [];
    }

    private class FakeAppSettingsService : IAppSettingsService
    {
        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings("dummy-api-key", ChatDelay: 0));
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

        var directive = service.GetMetaDirective("아무 입력", isFastForwardEnabled: false, isChapterChanged: true);

        Assert.NotNull(directive);
        Assert.Contains("type=\"chapter_transition\"", directive);
    }

    [Fact]
    public void GetMetaDirective_WhenFastForwardEnabled_ReturnsFastForwardDirective()
    {
        var service = CreateService();

        var directive = service.GetMetaDirective("빠른 진행 요청", isFastForwardEnabled: true, isChapterChanged: false);

        Assert.NotNull(directive);
        Assert.Contains("type=\"fast_forward\"", directive);
    }

    [Fact]
    public void GetMetaDirective_WhenInputBlank_ReturnsAutoContinueDirective()
    {
        var service = CreateService();

        var directive = service.GetMetaDirective("", isFastForwardEnabled: false, isChapterChanged: false);

        Assert.NotNull(directive);
        Assert.Contains("type=\"auto_continue\"", directive);
    }

    [Fact]
    public async Task ProcessChatStreamAsync_FiltersMarkdownAsterisksFromStreamAndFinalText()
    {
        // Arrange
        var chunks = new[] { "**\"안녕하세요.\"**", " 그녀가 ", "**미소**", " 지었다." };
        var fakeApi = new FakeStreamingGeminiApiService(chunks);
        var service = new ChatWorkflowService(
            fakeApi,
            new FakeMemoryManagerService(),
            new FakeAppSettingsService(),
            new FakeAudioService()
        );

        var receivedChars = new System.Text.StringBuilder();
        var preset = new CharacterPreset("테스트", "세계관", "시스템");
        var userMsg = new ChatMessage("user", "안녕", DateTime.Now);

        // Act
        var result = await service.ProcessChatStreamAsync(
            userMsg,
            preset,
            onCharReceived: c => receivedChars.Append(c)
        );

        // Assert
        Assert.True(result.IsSuccess);
        // 실시간 스트리밍으로 전달된 문자열에 '*' 기호가 전혀 없어야 함
        Assert.DoesNotContain("*", receivedChars.ToString());
        Assert.Equal("\"안녕하세요.\" 그녀가 미소 지었다.", receivedChars.ToString());

        // 최종 반환 텍스트에도 '*' 기호가 전혀 없어야 함
        Assert.DoesNotContain("*", result.FinalText);
        Assert.Equal("\"안녕하세요.\" 그녀가 미소 지었다.", result.FinalText);
    }

    private class FakeStreamingGeminiApiService(IEnumerable<string> chunks) : IGeminiApiService
    {
        public Task<string> SendMessageAsync(GeminiRequest request, ModelTier? overrideTier = null, CancellationToken cancellationToken = default)
            => Task.FromResult("");

        public async IAsyncEnumerable<string> SendMessageStreamAsync(
            GeminiRequest request,
            ModelTier? overrideTier = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in chunks)
            {
                yield return chunk;
            }
        }

        public Task<float[]> GetEmbeddingAsync(string text, string? taskType = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<float>());
    }
}
