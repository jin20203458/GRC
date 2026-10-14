using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GRC.Models;
using GRC.Services;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GRC.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppSettingsService _settingsService;
    private readonly IAudioService _audioService;
    private readonly IGoogleAuthService _googleAuthService;
    private readonly IGoogleTtsService _ttsService;
    private readonly HttpClient? _httpClient;

    public event Action? RequestGoBack;

    [ObservableProperty] private string _apiKey = string.Empty;

    [ObservableProperty] private string _projectId = string.Empty;
    [ObservableProperty] private bool _useVertexAI = true;

    [ObservableProperty] private ModelTier _selectedModel;
    [ObservableProperty] private BlockThreshold _safetyThreshold;
    [ObservableProperty] private BackgroundTheme _selectedTheme;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _chatDelay = "25";

    [ObservableProperty] private bool _isBgmEnabled;
    partial void OnIsBgmEnabledChanged(bool value) => _audioService.SetBgmState(value);

    [ObservableProperty] private double _bgmVolume;
    partial void OnBgmVolumeChanged(double value) => _audioService.SetBgmVolume(value);

    [ObservableProperty] private bool _isTypingSoundEnabled;
    partial void OnIsTypingSoundEnabledChanged(bool value) => _audioService.SetTypingSoundState(value);

    [ObservableProperty] private double _typingSoundVolume;
    [ObservableProperty] private bool _isTtsEnabled;
    partial void OnIsTtsEnabledChanged(bool value) => TestTtsCommand.NotifyCanExecuteChanged();

    [ObservableProperty] private TtsLanguage _selectedTtsLanguage;
    [ObservableProperty] private TtsVoiceOption _selectedTtsVoiceOption;

    [ObservableProperty] private bool _isTestingTts;
    [ObservableProperty] private string _ttsTestStatusMessage = string.Empty;
    [ObservableProperty] private string _ttsTestStatusColor = "#D1D1D1";

    public bool CanTestTts => !IsTestingTts && IsTtsEnabled;

    public IReadOnlyList<TtsVoiceOption> AvailableTtsVoices { get; } = new List<TtsVoiceOption>
    {
        new("Zephyr", "Zephyr (제퍼)", "밝고 또박또박한 활발한 톤, 소꿉친구 느낌"),
        new("Achernar", "Achernar (아케르나르)", "밝음, 친근함, 주근깨 소꿉친구 느낌"),
        new("Aoede", "Aoede (아오에데)", "하이톤, 다정한 누나 느낌"),
        new("Autonoe", "Autonoe (아우토노에)", "독립적이고 강인한 느낌, 확신에 찬 목소리"),
        new("Callirrhoe", "Callirrhoe (칼리로에)", "가장 표준적이고 부드러운 선율 톤"),
        new("Despina", "Despina (데스피나)", "하이톤, 통통 튀는 느낌, 밝은 마법사"),
        new("Erinome", "Erinome (에리노메)", "약간 높은 톤, 단정한 아나운서 느낌"),
        new("Gacrux", "Gacrux (가크룩스)", "약간 높은 톤, 살며시 웃는 다정한 느낌"),
        new("Kore", "Kore (코레)", "차분하고 또렷함, 지적인 느낌"),
        new("Laomedeia", "Laomedeia (라오메데이아)", "부드러움, 약간 높은 교사 톤"),
        new("Leda", "Leda (레다)", "고급스럽고 따뜻하며 부드러운 느낌"),
        new("Pulcherrima", "Pulcherrima (풀케리마)", "세련된 보이시한 톰보이 느낌"),
        new("Sulafat", "Sulafat (술라파트)", "고급스럽고 차가운 지적 느낌"),
        new("Vindemiatrix", "Vindemiatrix (빈데미아트릭스)", "부드럽고 차분한 성숙한 연상 톤")
    };

    [ObservableProperty] private bool _isCredentialRegistered;
    [ObservableProperty] private string _credentialStatusMessage = string.Empty;

    [ObservableProperty] private bool _isTestingConnection;
    [ObservableProperty] private string _testStatusMessage = string.Empty;
    [ObservableProperty] private string _testStatusColor = "#D1D1D1";

    public bool CanTestConnection => !IsTestingConnection;


    // 타이머 취소를 관리할 토큰
    private CancellationTokenSource? _previewCts;

    partial void OnTypingSoundVolumeChanged(double value)
    {
        _audioService.SetTypingSoundVolume(value);
        _ = PlayPreviewTypingSoundAsync();
    }

    private async Task PlayPreviewTypingSoundAsync()
    {
        if (!IsTypingSoundEnabled) return;
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        _audioService.StartTypingSound();
        try
        {
            await Task.Delay(1000, token);
            if (!token.IsCancellationRequested)
            {
                _audioService.StopTypingSound();
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"타이핑 사운드 미리듣기 오류: {ex.Message}");
        }
    }

    public ModelTier[] AvailableModelTiers { get; } = (ModelTier[])Enum.GetValues(typeof(ModelTier));
    public BlockThreshold[] AvailableSafetyThresholds { get; } = (BlockThreshold[])Enum.GetValues(typeof(BlockThreshold));
    public BackgroundTheme[] AvailableThemes { get; } = (BackgroundTheme[])Enum.GetValues(typeof(BackgroundTheme));
    public TtsLanguage[] AvailableTtsLanguages { get; } = (TtsLanguage[])Enum.GetValues(typeof(TtsLanguage));


    public SettingsViewModel(
        IAppSettingsService settingsService,
        IAudioService audioService,
        IGoogleAuthService googleAuthService,
        IGoogleTtsService ttsService,
        HttpClient? httpClient = null)
    {
        _settingsService = settingsService;
        _audioService = audioService;
        _googleAuthService = googleAuthService;
        _ttsService = ttsService;
        _httpClient = httpClient;
        _selectedTtsVoiceOption = AvailableTtsVoices[0];
    }

    // 화면 진입 시 기존 설정 불러오기
    [RelayCommand]
    public async Task LoadSettingsAsync()
    {
        var settings = await _settingsService.LoadSettingsAsync();

        ApiKey = settings.ApiKey;
        ProjectId = settings.ProjectId;
        UseVertexAI = settings.UseVertexAI;

        SelectedModel = settings.SelectedModel;
        SafetyThreshold = settings.SafetyThreshold;
        SelectedTheme = settings.SelectedTheme;
        ChatDelay = settings.ChatDelay.ToString();
        IsBgmEnabled = settings.IsBgmEnabled;
        BgmVolume = settings.BgmVolume;
        IsTypingSoundEnabled = settings.IsTypingSoundEnabled;
        TypingSoundVolume = settings.TypingSoundVolume;
        IsTtsEnabled = settings.IsTtsEnabled;
        SelectedTtsLanguage = settings.SelectedTtsLanguage;

        string voiceId = string.IsNullOrWhiteSpace(settings.SelectedTtsVoice) ? "Zephyr" : settings.SelectedTtsVoice;
        SelectedTtsVoiceOption = AvailableTtsVoices.FirstOrDefault(v => v.Id.Equals(voiceId, StringComparison.OrdinalIgnoreCase)) ?? AvailableTtsVoices[0];

        UpdateCredentialStatus();
    }

    private void UpdateCredentialStatus()
    {
        IsCredentialRegistered = _settingsService.IsCredentialFileExists();
        CredentialStatusMessage = IsCredentialRegistered
            ? "✅ Vertex AI 인증 파일(google-credentials.json)이 등록되어 있습니다."
            : "⚠️ Vertex AI 인증 파일이 등록되지 않았습니다. (TTS/Vertex AI 사용 시 필요)";
    }

    [RelayCommand]
    private void OpenApiKeyGuide()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://aistudio.google.com/apikey",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Open URL Error]: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task BrowseCredentialFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON 서비스 계정 키 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
            Title = "Vertex AI 서비스 계정 키 파일(google-credentials.json) 선택"
        };

        if (dialog.ShowDialog() == true)
        {
            bool success = await _settingsService.CopyCredentialFileAsync(dialog.FileName);
            if (success)
            {
                UpdateCredentialStatus();
                StatusMessage = "인증 파일이 성공적으로 등록되었습니다!";
                await Task.Delay(2000);
                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = "인증 파일 등록 실패. 올바른 JSON 파일인지 확인하세요.";
            }
        }
    }

    // 설정 저장
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        int delayValue = int.TryParse(ChatDelay, out int result) ? Math.Clamp(result, 0, 1000) : 25;

        var newSettings = new AppSettings(
            ApiKey,
            ProjectId,
            UseVertexAI,
            SelectedModel,
            SafetyThreshold,
            SelectedTheme,
            delayValue,
            IsBgmEnabled,
            BgmVolume,
            IsTypingSoundEnabled,
            TypingSoundVolume,
            IsTtsEnabled,
            SelectedTtsLanguage,
            SelectedTtsVoiceOption?.Id ?? "Zephyr"
        );

        await _settingsService.SaveSettingsAsync(newSettings);
        _audioService.SetBgmState(IsBgmEnabled);

        StatusMessage = "설정이 성공적으로 저장되었습니다.";
        await Task.Delay(2000);
        StatusMessage = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    public async Task TestConnectionAsync()
    {
        IsTestingConnection = true;
        TestConnectionCommand.NotifyCanExecuteChanged();
        TestStatusColor = "#FFA726";
        TestStatusMessage = "엔드포인트 연결 시험 중... (통신 확인)";

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var client = _httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            bool disposeClient = _httpClient == null;

            try
            {
                HttpRequestMessage httpRequest;
                string modelDisplayName;

                if (UseVertexAI)
                {
                    if (string.IsNullOrWhiteSpace(ProjectId))
                    {
                        TestStatusColor = "#EF5350";
                        TestStatusMessage = "FAIL: 구글 클라우드 Project ID가 입력되지 않았습니다.";
                        return;
                    }

                    if (!_settingsService.IsCredentialFileExists())
                    {
                        TestStatusColor = "#EF5350";
                        TestStatusMessage = "FAIL: Vertex AI 서비스 계정 키 파일(google-credentials.json)이 등록되지 않았습니다.";
                        return;
                    }

                    string token;
                    try
                    {
                        token = await _googleAuthService.GetGoogleAccessTokenAsync(cts.Token);
                    }
                    catch (Exception ex)
                    {
                        TestStatusColor = "#EF5350";
                        TestStatusMessage = $"FAIL: OAuth2 인증 토큰 획득 실패: {ex.Message}";
                        return;
                    }

                    string vertexModel = GetVertexModelName(SelectedModel);
                    modelDisplayName = $"Vertex AI ({vertexModel}, global)";
                    string requestUri = $"https://aiplatform.googleapis.com/v1beta1/projects/{ProjectId.Trim()}/locations/global/publishers/google/models/{vertexModel}:generateContent";

                    httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri);
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(ApiKey))
                    {
                        TestStatusColor = "#EF5350";
                        TestStatusMessage = "FAIL: Gemini API 키가 입력되지 않았습니다.";
                        return;
                    }

                    string studioModel = GetStudioModelName(SelectedModel);
                    modelDisplayName = $"AI Studio ({studioModel})";
                    string requestUri = $"https://generativelanguage.googleapis.com/v1beta/{studioModel}:generateContent?key={ApiKey.Trim()}";

                    httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri);
                }

                var pingPayload = new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new[] { new { text = "Respond with 'OK'." } }
                        }
                    },
                    generationConfig = new
                    {
                        maxOutputTokens = 16,
                        temperature = 0.0
                    }
                };

                string jsonPayload = JsonSerializer.Serialize(pingPayload);
                httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (httpRequest)
                {
                    using var response = await client.SendAsync(httpRequest, cts.Token);
                    sw.Stop();

                    if (response.IsSuccessStatusCode)
                    {
                        TestStatusColor = "#4CAF50";
                        TestStatusMessage = $"PASS: LLM 연결 성공! 모델: {modelDisplayName} (지연시간: {sw.ElapsedMilliseconds} ms)";
                    }
                    else
                    {
                        string errContent = await response.Content.ReadAsStringAsync(cts.Token);
                        string briefErr = ExtractErrorMessage(errContent);
                        TestStatusColor = "#EF5350";
                        TestStatusMessage = $"FAIL: API 통신 실패 (HTTP {(int)response.StatusCode}): {briefErr}";
                    }
                }
            }
            finally
            {
                if (disposeClient)
                {
                    client.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            TestStatusColor = "#EF5350";
            TestStatusMessage = "FAIL: 15초 타임아웃으로 통신이 중단되었습니다. 네트워크 상태를 확인하세요.";
        }
        catch (Exception ex)
        {
            TestStatusColor = "#EF5350";
            TestStatusMessage = $"FAIL: 통신 예외 발생 ({ex.Message})";
        }
        finally
        {
            IsTestingConnection = false;
            TestConnectionCommand.NotifyCanExecuteChanged();
        }
    }

    private static string GetStudioModelName(ModelTier tier) => tier switch
    {
        ModelTier.Pro => "models/gemini-4-argon",
        ModelTier.Flash => "models/gemini-3.8-flash",
        ModelTier.Flash35 => "models/gemini-3.5-flash",
        ModelTier.FlashLite => "models/gemini-3.5-flash-lite",
        _ => "models/gemini-3.8-flash"
    };

    private static string GetVertexModelName(ModelTier tier) => tier switch
    {
        ModelTier.Pro => "gemini-4-argon",
        ModelTier.Flash => "gemini-3.8-flash",
        ModelTier.Flash35 => "gemini-3.5-flash",
        ModelTier.FlashLite => "gemini-3.5-flash-lite",
        _ => "gemini-3.8-flash"
    };

    private static string ExtractErrorMessage(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return "응답 본문이 비어있습니다.";
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            if (doc.RootElement.TryGetProperty("error", out var errorProp))
            {
                if (errorProp.TryGetProperty("message", out var msgProp))
                {
                    return msgProp.GetString() ?? rawResponse;
                }
            }
        }
        catch { }
        return rawResponse.Length > 200 ? rawResponse[..200] + "..." : rawResponse;
    }

    [RelayCommand(CanExecute = nameof(CanTestTts))]
    public async Task TestTtsAsync()
    {
        IsTestingTts = true;
        TestTtsCommand.NotifyCanExecuteChanged();
        TtsTestStatusColor = "#FFA726";
        TtsTestStatusMessage = "음성 생성 중... (Google Cloud TTS 통신)";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        try
        {
            string sampleText = SelectedTtsLanguage == TtsLanguage.Japanese
                ? "こんにちは。音声合成のテストです。"
                : "안녕하세요. 음성 합성 테스트입니다.";

            string voiceId = SelectedTtsVoiceOption?.Id ?? "Zephyr";
            string filePath = await _ttsService.PreviewSpeechAsync(
                sampleText,
                voiceId,
                SelectedTtsLanguage,
                cts.Token);

            TtsTestStatusColor = "#4CAF50";
            TtsTestStatusMessage = $"음성 재생 중 ({voiceId})";

            await _audioService.PlayVoiceSoundAsync(filePath);

            TtsTestStatusMessage = $"재생 완료 ({voiceId})";
        }
        catch (OperationCanceledException)
        {
            TtsTestStatusColor = "#EF5350";
            TtsTestStatusMessage = "FAIL: TTS 요청 시간 초과 (타임아웃). 네트워크 상태를 확인하세요.";
        }
        catch (Exception ex)
        {
            TtsTestStatusColor = "#EF5350";
            TtsTestStatusMessage = $"FAIL: 음성 생성 실패 ({ex.Message})";
        }
        finally
        {
            IsTestingTts = false;
            TestTtsCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void GoBack() => RequestGoBack?.Invoke();
}