using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GRC.Models;
using GRC.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using static Google.Protobuf.Reflection.SourceCodeInfo.Types;

namespace GRC.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppSettingsService _settingsService;
    private readonly IAudioService _audioService;

    public event Action? RequestGoBack;

    [ObservableProperty] private string _apiKey = string.Empty;

    [ObservableProperty] private string _projectId = string.Empty;
    [ObservableProperty] private string _location = "asia-northeast3";
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
    [ObservableProperty] private TtsLanguage _selectedTtsLanguage;


    // 타이머 취소를 관리할 토큰
    private CancellationTokenSource? _previewCts;

    partial void OnTypingSoundVolumeChanged(double value)
    {
        _audioService.SetTypingSoundVolume(value);
        PlayPreviewTypingSound();
    }

    private async void PlayPreviewTypingSound()
    {
        if (!IsTypingSoundEnabled) return;
        _previewCts?.Cancel();
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
    }

    public ModelTier[] AvailableModelTiers { get; } = (ModelTier[])Enum.GetValues(typeof(ModelTier));
    public BlockThreshold[] AvailableSafetyThresholds { get; } = (BlockThreshold[])Enum.GetValues(typeof(BlockThreshold));
    public BackgroundTheme[] AvailableThemes { get; } = (BackgroundTheme[])Enum.GetValues(typeof(BackgroundTheme));
    public TtsLanguage[] AvailableTtsLanguages { get; } = (TtsLanguage[])Enum.GetValues(typeof(TtsLanguage));


    public SettingsViewModel(IAppSettingsService settingsService, IAudioService audioService)
    {
        _settingsService = settingsService;
        _audioService = audioService;
    }

    // 화면 진입 시 기존 설정 불러오기
    [RelayCommand]
    public async Task LoadSettingsAsync()
    {
        var settings = await _settingsService.LoadSettingsAsync();

        ApiKey = settings.ApiKey;
        ProjectId = settings.ProjectId;
        Location = settings.Location;
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
    }

    // 설정 저장
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        int delayValue = int.TryParse(ChatDelay, out int result) ? result : 25;

        var newSettings = new AppSettings(
            ApiKey,
            ProjectId,
            Location,
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
            SelectedTtsLanguage
        );

        await _settingsService.SaveSettingsAsync(newSettings);
        _audioService.SetBgmState(IsBgmEnabled);

        StatusMessage = "설정이 성공적으로 저장되었습니다.";
        await Task.Delay(2000);
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void GoBack() => RequestGoBack?.Invoke();
}