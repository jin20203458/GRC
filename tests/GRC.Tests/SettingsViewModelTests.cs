using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GRC.Models;
using GRC.Services;
using GRC.ViewModels;
using Xunit;

namespace GRC.Tests;

public class SettingsViewModelTests
{
    private class FakeAppSettingsService : IAppSettingsService
    {
        public bool CredentialExists { get; set; } = true;
        public AppSettings StoredSettings { get; set; } = new AppSettings("");

        public Task<AppSettings> LoadSettingsAsync() =>
            Task.FromResult(StoredSettings);

        public Task SaveSettingsAsync(AppSettings settings)
        {
            StoredSettings = settings;
            return Task.CompletedTask;
        }

        public bool IsCredentialFileExists() => CredentialExists;

        public Task<bool> CopyCredentialFileAsync(string sourceFilePath) => Task.FromResult(true);
    }

    private class FakeAudioService : IAudioService
    {
        public string? LastVoicePlayed { get; private set; }

        public void SetBgmState(bool enabled) { }
        public void SetBgmVolume(double volume) { }
        public void SetTypingSoundState(bool enabled) { }
        public void SetTypingSoundVolume(double volume) { }
        public void StartTypingSound() { }
        public void StopTypingSound() { }
        public void PlayVoiceSound(string filePath) => LastVoicePlayed = filePath;
        public Task PlayVoiceSoundAsync(string filePath)
        {
            LastVoicePlayed = filePath;
            return Task.CompletedTask;
        }
        public void StopVoiceSound() { }
    }

    private class FakeGoogleAuthService : IGoogleAuthService
    {
        public string TokenToReturn { get; set; } = "mock-bearer-token";
        public bool ShouldThrow { get; set; }

        public Task<string> GetGoogleAccessTokenAsync(CancellationToken cancellationToken)
        {
            if (ShouldThrow)
            {
                throw new InvalidOperationException("OAuth2 인증 실패");
            }
            return Task.FromResult(TokenToReturn);
        }
    }

    private class FakeGoogleTtsService : IGoogleTtsService
    {
        public bool ShouldThrow { get; set; }
        public string LastVoiceRequested { get; private set; } = string.Empty;
        public TtsLanguage LastLanguageRequested { get; private set; }

        public Task<string> GenerateSpeechAsync(
            string text,
            string characterName,
            string narration,
            string activeLorebook,
            string? voiceOverride = null,
            TtsLanguage? languageOverride = null,
            CancellationToken cancellationToken = default)
        {
            if (ShouldThrow) throw new HttpRequestException("TTS API 거부");
            LastVoiceRequested = voiceOverride ?? "Zephyr";
            LastLanguageRequested = languageOverride ?? TtsLanguage.Korean;
            return Task.FromResult("dummy_speech.wav");
        }

        public Task<string> PreviewSpeechAsync(
            string text,
            string voiceName,
            TtsLanguage language,
            CancellationToken cancellationToken = default)
        {
            if (ShouldThrow) throw new HttpRequestException("TTS API 거부");
            LastVoiceRequested = voiceName;
            LastLanguageRequested = language;
            return Task.FromResult("dummy_preview.wav");
        }
    }

    private class MockHttpMessageHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task TestConnectionAsync_VertexAi_MissingProjectId_ReturnsFailMessage()
    {
        var settingsService = new FakeAppSettingsService();
        var audioService = new FakeAudioService();
        var authService = new FakeGoogleAuthService();
        var ttsService = new FakeGoogleTtsService();

        var vm = new SettingsViewModel(settingsService, audioService, authService, ttsService)
        {
            UseVertexAI = true,
            ProjectId = ""
        };

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("FAIL", vm.TestStatusMessage);
        Assert.Contains("Project ID", vm.TestStatusMessage);
        Assert.Equal("#EF5350", vm.TestStatusColor);
    }

    [Fact]
    public async Task TestConnectionAsync_VertexAi_MissingCredentials_ReturnsFailMessage()
    {
        var settingsService = new FakeAppSettingsService { CredentialExists = false };
        var audioService = new FakeAudioService();
        var authService = new FakeGoogleAuthService();
        var ttsService = new FakeGoogleTtsService();

        var vm = new SettingsViewModel(settingsService, audioService, authService, ttsService)
        {
            UseVertexAI = true,
            ProjectId = "my-project-123"
        };

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("FAIL", vm.TestStatusMessage);
        Assert.Contains("google-credentials.json", vm.TestStatusMessage);
        Assert.Equal("#EF5350", vm.TestStatusColor);
    }

    [Fact]
    public async Task TestConnectionAsync_Studio_MissingApiKey_ReturnsFailMessage()
    {
        var settingsService = new FakeAppSettingsService();
        var audioService = new FakeAudioService();
        var authService = new FakeGoogleAuthService();
        var ttsService = new FakeGoogleTtsService();

        var vm = new SettingsViewModel(settingsService, audioService, authService, ttsService)
        {
            UseVertexAI = false,
            ApiKey = ""
        };

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("FAIL", vm.TestStatusMessage);
        Assert.Contains("API 키", vm.TestStatusMessage);
        Assert.Equal("#EF5350", vm.TestStatusColor);
    }

    [Fact]
    public async Task TestConnectionAsync_VertexAi_Http200_ReturnsPassMessage()
    {
        var settingsService = new FakeAppSettingsService { CredentialExists = true };
        var audioService = new FakeAudioService();
        var authService = new FakeGoogleAuthService();
        var ttsService = new FakeGoogleTtsService();

        const string mockResponse = """
        {
          "candidates": [
            {
              "content": {
                "parts": [{ "text": "OK" }]
              }
            }
          ]
        }
        """;

        using var mockHttp = new HttpClient(new MockHttpMessageHandler(HttpStatusCode.OK, mockResponse));

        var vm = new SettingsViewModel(settingsService, audioService, authService, ttsService, mockHttp)
        {
            UseVertexAI = true,
            ProjectId = "test-project-456",
            SelectedModel = ModelTier.Flash
        };

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.StartsWith("PASS: LLM 연결 성공!", vm.TestStatusMessage);
        Assert.Contains("Vertex AI", vm.TestStatusMessage);
        Assert.Contains("global", vm.TestStatusMessage);
        Assert.Equal("#4CAF50", vm.TestStatusColor);
    }

    [Fact]
    public async Task TestConnectionAsync_Studio_Http403_ExtractsErrorMessage()
    {
        var settingsService = new FakeAppSettingsService();
        var audioService = new FakeAudioService();
        var authService = new FakeGoogleAuthService();
        var ttsService = new FakeGoogleTtsService();

        const string mockError = """
        {
          "error": {
            "code": 403,
            "message": "API key not valid. Please pass a valid API key.",
            "status": "PERMISSION_DENIED"
          }
        }
        """;

        using var mockHttp = new HttpClient(new MockHttpMessageHandler(HttpStatusCode.Forbidden, mockError));

        var vm = new SettingsViewModel(settingsService, audioService, authService, ttsService, mockHttp)
        {
            UseVertexAI = false,
            ApiKey = "invalid-key-xyz",
            SelectedModel = ModelTier.Flash35
        };

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.StartsWith("FAIL: API 통신 실패 (HTTP 403)", vm.TestStatusMessage);
        Assert.Contains("API key not valid", vm.TestStatusMessage);
        Assert.Equal("#EF5350", vm.TestStatusColor);
    }

    [Fact]
    public async Task TestTtsAsync_Success_PlaysVoiceSoundAndSetsStatusMessage()
    {
        var settingsService = new FakeAppSettingsService();
        var audioService = new FakeAudioService();
        var authService = new FakeGoogleAuthService();
        var ttsService = new FakeGoogleTtsService();

        var vm = new SettingsViewModel(settingsService, audioService, authService, ttsService)
        {
            IsTtsEnabled = true,
            SelectedTtsLanguage = TtsLanguage.Korean
        };
        vm.SelectedTtsVoiceOption = vm.AvailableTtsVoices.First(v => v.Id == "Aoede");

        await vm.TestTtsCommand.ExecuteAsync(null);

        Assert.Equal("#4CAF50", vm.TtsTestStatusColor);
        Assert.Contains("재생 완료 (Aoede)", vm.TtsTestStatusMessage);
        Assert.Equal("Aoede", ttsService.LastVoiceRequested);
        Assert.Equal(TtsLanguage.Korean, ttsService.LastLanguageRequested);
        Assert.Equal("dummy_preview.wav", audioService.LastVoicePlayed);
    }

    [Fact]
    public void TestTtsAsync_WhenTtsDisabled_CanTestTtsReturnsFalse()
    {
        var settingsService = new FakeAppSettingsService();
        var audioService = new FakeAudioService();
        var authService = new FakeGoogleAuthService();
        var ttsService = new FakeGoogleTtsService();

        var vm = new SettingsViewModel(settingsService, audioService, authService, ttsService)
        {
            IsTtsEnabled = false
        };

        Assert.False(vm.CanTestTts);
        Assert.False(vm.TestTtsCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestTtsAsync_Failure_SetsErrorMessage()
    {
        var settingsService = new FakeAppSettingsService();
        var audioService = new FakeAudioService();
        var authService = new FakeGoogleAuthService();
        var ttsService = new FakeGoogleTtsService { ShouldThrow = true };

        var vm = new SettingsViewModel(settingsService, audioService, authService, ttsService)
        {
            IsTtsEnabled = true
        };

        await vm.TestTtsCommand.ExecuteAsync(null);

        Assert.Equal("#EF5350", vm.TtsTestStatusColor);
        Assert.StartsWith("FAIL: 음성 생성 실패", vm.TtsTestStatusMessage);
    }

    [Fact]
    public async Task LoadSettingsAsync_LoadsVoiceOptionCorrectly()
    {
        var settingsService = new FakeAppSettingsService
        {
            StoredSettings = new AppSettings("", SelectedTtsVoice: "Sulafat")
        };
        var audioService = new FakeAudioService();
        var authService = new FakeGoogleAuthService();
        var ttsService = new FakeGoogleTtsService();

        var vm = new SettingsViewModel(settingsService, audioService, authService, ttsService);

        await vm.LoadSettingsCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SelectedTtsVoiceOption);
        Assert.Equal("Sulafat", vm.SelectedTtsVoiceOption.Id);
    }
}
