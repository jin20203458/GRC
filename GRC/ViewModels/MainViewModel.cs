using CommunityToolkit.Mvvm.ComponentModel;
using GRC.Services;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace GRC.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableObject? _currentPage; // 현재 화면 (ViewModel)

    private readonly IServiceProvider _serviceProvider;
    private ChatViewModel? _currentChatVm; // 현재 활성화된 챗 뷰모델 추적용

    // 하위 뷰모델을 직접 주입받지 않고 IServiceProvider 자체를 주입받음
    public MainViewModel(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;

        // 앱 시작 시 세션 리스트 화면으로 초기 네비게이션
        ApplyInitialBgmState();
        NavigateToSessionList();
    }

    private void NavigateToSessionList()
    {
        // 1. 만약 채팅방에서 빠져나오는 것이라면, 백그라운드 작업(API 스트리밍 등) 강제 종료
        _currentChatVm?.Cleanup();
        _currentChatVm = null;

        // 2. 세션 리스트 화면 객체를 컨테이너에서 새로 꺼내기
        var sessionListVm = _serviceProvider.GetRequiredService<SessionListViewModel>();

        // 3. 이벤트 구독(+=) 연결
        sessionListVm.SessionSelected += OnSessionSelected;
        sessionListVm.SettingsRequested += NavigateToSettings;

        CurrentPage = sessionListVm;
    }
    private async void ApplyInitialBgmState()
    {
        var appSettingsService = _serviceProvider.GetRequiredService<IAppSettingsService>();
        var audioService = _serviceProvider.GetRequiredService<IAudioService>();
        var settings = await appSettingsService.LoadSettingsAsync();

        audioService.SetBgmVolume(settings.BgmVolume);
        audioService.SetBgmState(settings.IsBgmEnabled);

        audioService.SetTypingSoundVolume(settings.TypingSoundVolume);
        audioService.SetTypingSoundState(settings.IsTypingSoundEnabled);
    }
    private async void OnSessionSelected(string? fileName, string? presetName, string? worldview, string? scenario, string? customStats, string? statusUpdateGuide)
    {
        try
        {
            // 1. 채팅방에 진입할 때마다 상태가 초기화된 완전한 '새 ChatViewModel' 객체 생성 (핵심)
            _currentChatVm = _serviceProvider.GetRequiredService<ChatViewModel>();

            // 2. 뒤로 가기 이벤트 필수 재연결
            _currentChatVm.RequestGoBack += NavigateToSessionList;

            // 3. 채팅방 초기화 및 화면 전환
            await _currentChatVm.InitializeWithSession(fileName, presetName, worldview, scenario, customStats, statusUpdateGuide);
            CurrentPage = _currentChatVm;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"세션 로드 실패: {ex.Message}");
        }
    }

    private void NavigateToSettings()
    {
        // 설정 화면 뷰모델 새로 생성 및 연결
        var settingsVm = _serviceProvider.GetRequiredService<SettingsViewModel>();
        settingsVm.RequestGoBack += NavigateToSessionList;

        CurrentPage = settingsVm;
    }
}