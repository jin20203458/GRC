using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GRC.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GRC.ViewModels;

// [1] 리스트에 보여줄 개별 세션의 요약 정보 (DTO)
public partial class SessionSummary : ObservableObject
{
    public string FileName { get; }
    public string PresetName { get; }
    public string LastMessagePreview { get; }
    public DateTime LastModified { get; }

    public SessionSummary(string fileName, string presetName, string lastMessagePreview, DateTime lastModified)
    {
        FileName = fileName;
        PresetName = presetName;
        LastMessagePreview = lastMessagePreview;
        LastModified = lastModified;
    }
}

// [2] 세션 리스트 화면을 제어하는 메인 뷰모델
public partial class SessionListViewModel : ObservableObject
{
    private readonly ISessionService _sessionService;
    private readonly IDialogService _dialogService;

    // 화면에 그려질 카드 리스트
    public ObservableCollection<SessionSummary> Sessions { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasNoSessions = true;

    // MainViewModel에게 "이 세션 열어줘!" 라고 알리는 이벤트 델리게이트
    public event Action<string?, string?, string?, string?, string?, string?>? SessionSelected;
    // SettingsViewModel으로 "설정 화면 보여줘!" 라고 알리는 이벤트 델리게이트
    public event Action? SettingsRequested;

    public SessionListViewModel(ISessionService sessionService, IDialogService dialogService)
    {
        _sessionService = sessionService;
        _dialogService = dialogService;
        Sessions.CollectionChanged += (s, e) => HasNoSessions = Sessions.Count == 0;
    }

    /// <summary>
    /// 화면이 로드될 때 저장된 파일들을 읽어와 Sessions 컬렉션을 채웁니다.
    /// </summary>
    [RelayCommand]
    public async Task LoadSessionsAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Sessions.Clear();

        try
        {
            // 1. 저장된 모든 파일명 가져오기
            var files = await _sessionService.GetSessionFilesAsync();

            foreach (var file in files)
            {
                // 2. 파일 내용을 읽어서 요약 정보 추출
                var session = await _sessionService.LoadSessionAsync(file);
                if (session != null)
                {
                    // 마지막 대화 30자까지만 자르기
                    var lastMsg = session.History.LastOrDefault()?.Text ?? "대화 내역이 없습니다.";
                    var preview = lastMsg.Length > 30 ? lastMsg[..30] + "..." : lastMsg;

                    // 파일 수정 시간 가져오기
                    string targetFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", file, "Session.json");

                    var fileInfo = new FileInfo(targetFilePath);

                    Sessions.Add(new SessionSummary(
                        file,
                        session.Preset.Name,
                        preview,
                        fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.Now
                    ));
                }
            }

            // 3. 최신순으로 정렬해서 다시 컬렉션에 넣기
            var sortedList = Sessions.OrderByDescending(s => s.LastModified).ToList();
            Sessions.Clear();
            foreach (var item in sortedList)
            {
                Sessions.Add(item);
            }
            HasNoSessions = Sessions.Count == 0;
        }
        finally
        {
            IsLoading = false;
            HasNoSessions = Sessions.Count == 0;
        }
    }

    /// <summary>
    /// '새로운 대화 시작' 버튼 클릭 시
    /// </summary>
    [RelayCommand]
    private void CreateNewSession()
    {
        var result = _dialogService.ShowNewSessionSetupDialog();
        if (result != null)
        {
            SessionSelected?.Invoke(null, result.Name, result.Worldview, result.Scenario, result.CustomStats, result.StatusUpdateGuide);
        }
    }

    /// <summary>
    /// 특정 세션 카드 클릭 시
    /// </summary>
    [RelayCommand]
    private void OpenSession(SessionSummary summary)
    {
        SessionSelected?.Invoke(summary.FileName, null, null, null, null, null);
    }

    /// <summary>
    /// 특정 세션 삭제 시
    /// </summary>
    [RelayCommand]
    private async Task DeleteSessionAsync(SessionSummary summary)
    {
        bool isConfirmed = _dialogService.ShowConfirm(
            $"'{summary.PresetName}' 세션을 정말 삭제하시겠습니까?\n삭제된 데이터는 복구할 수 없습니다.",
            "세션 삭제 확인"
        );

        if (isConfirmed)
        {
            await _sessionService.DeleteSessionAsync(summary.FileName);
            Sessions.Remove(summary);
            HasNoSessions = Sessions.Count == 0;
        }
    }

    /// <summary>
    /// '설정' 버튼 클릭 시
    /// </summary>
    [RelayCommand]
    private void OpenSettings()
    {
        SettingsRequested?.Invoke();
    }

    /// <summary>
    /// 'AI 세션 아키텍트' 버튼 클릭 시
    /// </summary>
    [RelayCommand]
    private void OpenArchitect()
    {
        _dialogService.ShowSessionArchitectWindow((fileName) =>
        {
            SessionSelected?.Invoke(fileName, null, null, null, null, null);
        });
    }
}