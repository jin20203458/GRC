using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GRC.Models;
using GRC.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace GRC.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    // ==========================================
    // [1. 의존성 주입 및 내부 상태 변수]
    // ==========================================
    private readonly IGeminiApiService _apiService;
    private readonly IMemoryManagerService _memoryService;
    private readonly ISessionService _sessionService;
    private readonly IPresetStorageService _presetService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly IReplySuggestionService _suggestionService;
    private readonly IAudioService _audioService;
    private readonly IChatWorkflowService _chatWorkflowService;
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IGoogleTtsService _ttsService;
    private readonly ILorebookService _lorebookService;

    // 현재 열려있는 세션의 파일명을 기억 (자동 저장 시 덮어쓰기 용도)
    private string _currentFileName = string.Empty;

    // 네비게이션 이벤트 (MainViewModel로 '뒤로 가기' 신호 전달)
    public event Action? RequestGoBack;

    // 스트리밍 1글자 전달용 이벤트 (UI 헬퍼가 이를 구독하여 델타 렌더링 수행)
    public event Action<char>? OnCharReceived;

    // ==========================================
    // [2. UI 바인딩용 프로퍼티 (ObservableProperty)]
    // ==========================================

    // 화면에 바인딩될 채팅 내역 리스트 (C# 12 컬렉션 식)
    public ObservableCollection<ChatMessage> ChatHistory { get; } = [];

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private CharacterPreset? _currentPreset;

    [ObservableProperty]
    private int _currentChapter = 1;
    partial void OnCurrentChapterChanged(int value)
    {
        UpdateRandomBackground();
    }

    [ObservableProperty]
    private int _currentTurn = 0;

    // 추천 답변 기능 활성화 여부 (설정에서 토글 가능)
    [ObservableProperty]
    private bool _isSuggestionEnabled = false; // 기본값은 꺼짐

    // 추천 답변 로딩 상태 표시용
    [ObservableProperty]
    private bool _isLoadingSuggestions = false;

    // 화면에 보여질 추천 답변 리스트
    public ObservableCollection<string> SuggestedReplies { get; } = [];

    // 스트리밍 도중에 API 요청을 취소할 때 사용할 CancellationTokenSource
    private CancellationTokenSource? _cancellationTokenSource;
    private CancellationTokenSource? _suggestionCts;

    //  배경 이미지 바인딩용 프로퍼티 (초기 이미지)
    [ObservableProperty]
    private string _currentBackgroundImage = "pack://application:,,,/Resources/Fantasy/bg1.png";


    private List<string> _unusedBackgrounds = new();

    // 캐릭터 상태와 인벤토리 아이템, 현재 위치를 화면에 보여주기 위한 컬렉션 
    public ObservableCollection<CharacterStatusUI> CharacterStatuses { get; } = [];
    public ObservableCollection<InventoryItemUI> InventoryItems { get; } = [];
    public ObservableCollection<string> CurrentPlaces { get; } = [];
    public ObservableCollection<CustomStatUI> CustomStatsUIList { get; } = [];


    // 장기 기억, 이전 챕터, 현재 챕터를 UI에 보여주기 위한 프로퍼티
    [ObservableProperty]
    private string _longTermSummaryText = string.Empty;

    [ObservableProperty]
    private string _prevChapterPlot = string.Empty;

    [ObservableProperty]
    private string _currentChapterPlot = string.Empty;

    // 스킵 활성화 여부를 바인딩할 프로퍼티 
    [ObservableProperty]
    private bool _isFastForwardEnabled = false;

    // 현재 스트리밍 중인 텍스트를 실시간으로 보여주기 위한 프로퍼티 (디버깅 및 상태창용)
    public string CurrentStreamingText { get; private set; } = string.Empty;
    // 임시로 추천 답변을 보관할 변수
    private List<string>? _prefetchedSuggestions = null;
    // ==========================================
    // [3. 생성자]
    // ==========================================
    public ChatViewModel(
        IGeminiApiService apiService,
        IMemoryManagerService memoryService,
        ISessionService sessionService,
        IPresetStorageService presetService,
        IAppSettingsService appSettingsService,
        IReplySuggestionService suggestionService,
        IAudioService audioService,
        IChatWorkflowService chatWorkflowService,
        IThemeService themeService, IDialogService dialogService,
        IGoogleTtsService ttsService, ILorebookService lorebookService)
    {
        _apiService = apiService;
        _memoryService = memoryService;
        _sessionService = sessionService;
        _presetService = presetService;
        _appSettingsService = appSettingsService;
        _suggestionService = suggestionService;
        _audioService = audioService;
        _chatWorkflowService = chatWorkflowService;
        _themeService = themeService;
        _dialogService = dialogService;
        _ttsService = ttsService;
        _lorebookService = lorebookService;
    }

    // ==========================================
    // [4. 초기화 로직 (화면 진입 시 호출)]
    // ==========================================
    public async Task InitializeWithSession(string? fileName, string? customName = null, string? customWorldview = null, string? initialScenario = null, string? initialCustomStats = null)
    {
        IsBusy = true;
        ChatHistory.Clear();

        // 1. 공간 할당 (Provisioning): 단기/중기/장기 메모리를 백지화하고 세션 버전을 올려 이전 요약 무효화
        _memoryService.Clear();

        try
        {
            // [기존 코드 위치 (157~160번째 줄 부근)]
            if (string.IsNullOrEmpty(fileName))
            {
                var basePreset = await _presetService.LoadPresetAsync();
                string finalName = string.IsNullOrWhiteSpace(customName) ? basePreset.Name : customName;
                string finalWorldview = string.IsNullOrWhiteSpace(customWorldview) ? basePreset.Worldview : customWorldview;

                var customStatsDict = GRC.Helpers.ChatDataHelper.ParseCustomStats(initialCustomStats);

                CurrentPreset = basePreset with { Name = finalName, Worldview = finalWorldview };
                _memoryService.UpdateContextStatus(new StatusPayload { CustomStats = customStatsDict });

                _currentFileName = GRC.Helpers.ChatDataHelper.GenerateSessionFileName(finalName);
                GRC.Helpers.TokenLogger.CurrentSessionFileName = _currentFileName;


                await _presetService.SavePresetAsync(_currentFileName, CurrentPreset);
                // 4. 초기 상태 점화 (Ignition): 시작 시나리오가 있다면 시스템 메시지로 주입
                if (!string.IsNullOrWhiteSpace(initialScenario))
                {
                    string scenarioMessage = $"[초기 상황]\n{initialScenario}";

                    // 화면(UI)에 보여주기 위해 채팅 리스트에 추가
                    ChatHistory.Add(new ChatMessage("user", scenarioMessage, DateTime.Now));

                    // 백엔드 메모리(단기 기억)에 강제 주입
                    _memoryService.InjectInitialScenario(scenarioMessage);
                    _ = Task.Run(async () => await GRC.Helpers.FullHistoryLogger.LogMessageAsync(_currentFileName, new ChatMessage("user", scenarioMessage, DateTime.Now)));
                }
                var initialSession = _memoryService.ExportSession(CurrentPreset);
                await _sessionService.SaveSessionAsync(_currentFileName, initialSession);
            }
            else
            {
                _currentFileName = fileName;
                GRC.Helpers.TokenLogger.CurrentSessionFileName = _currentFileName;
                var savedSession = await _sessionService.LoadSessionAsync(fileName);
                if (savedSession != null)
                {

                    var sessionPreset = await _presetService.LoadPresetAsync(_currentFileName);
                    CurrentPreset = sessionPreset;
                    _memoryService.RestoreSession(savedSession);

                    var fullHistory = await GRC.Helpers.FullHistoryLogger.LoadFullHistoryAsync(fileName);
                    if (fullHistory.Count > 0)
                    {
                        foreach (var msg in fullHistory)
                        {
                            ChatHistory.Add(msg);
                        }
                    }
                    else
                    {
                        foreach (var msg in savedSession.History)
                        {
                            ChatHistory.Add(msg);
                        }
                    }
                }
            }
        }
        finally
        {
            if (CurrentPreset != null)
            {
                var currentSession = _memoryService.ExportSession(CurrentPreset);

                CurrentChapter = currentSession.ChapterCount;
                CurrentTurn = currentSession.TotalTurnCount;

                UpdateStatusUI(currentSession.CurrentContext?.CustomStats, currentSession.CurrentContext?.Chars, currentSession.CurrentContext?.Items, currentSession.CurrentContext?.Places);

                LongTermSummaryText = currentSession.LongTermSummary;

                // 여기에 다중 버퍼 로직 적용
                PrevChapterPlot = currentSession.PrevContexts != null && currentSession.PrevContexts.Any()
                    ? string.Join("\n\n", currentSession.PrevContexts.Select(ctx => ctx.Plot))
                    : "기록 없음";

                CurrentChapterPlot = currentSession.CurrentContext?.Plot ?? "기록 없음";
            }
            IsBusy = false;
            //  초기 화면부터 랜덤 배경을 적용
            UpdateRandomBackground();
        }
    }

    [RelayCommand]
    private async Task EditWorldviewAsync()
    {
        if (CurrentPreset == null) return;

        var currentSession = _memoryService.ExportSession(CurrentPreset);
        var latestStats = currentSession.CurrentContext?.CustomStats ?? CurrentPreset.CustomStats;

        // 1. 현재 화면의 ChatHistory에서 기존 초기 시나리오 텍스트 추출
        var firstMsg = ChatHistory.FirstOrDefault();
        string currentScenario = "";
        if (firstMsg != null && firstMsg.Role == "user" && firstMsg.Text.StartsWith("[초기 상황]\n"))
        {
            currentScenario = firstMsg.Text.Substring("[초기 상황]\n".Length);
        }

        // 2. 다이얼로그 호출
        var result = _dialogService.ShowEditWorldviewDialog(CurrentPreset.Worldview, latestStats, currentScenario);

        if (result.HasValue && result.Value.IsSaved)
        {
            var customStatsDict = GRC.Helpers.ChatDataHelper.ParseCustomStats(result.Value.CustomStats);
            CurrentPreset = CurrentPreset with { Worldview = result.Value.Worldview, CustomStats = customStatsDict };

            await _presetService.SavePresetAsync(_currentFileName, CurrentPreset);
            _memoryService.UpdateContextStatus(new StatusPayload { CustomStats = customStatsDict });

            if (!string.IsNullOrWhiteSpace(result.Value.InitialScenario) && result.Value.InitialScenario != currentScenario)
            {
                if (firstMsg != null && firstMsg.Role == "user" && firstMsg.Text.StartsWith("[초기 상황]\n"))
                {
                    // [기존 로직] 시나리오가 이미 존재하면 텍스트만 덮어쓰기
                    firstMsg.Text = $"[초기 상황]\n{result.Value.InitialScenario}";

                    // 💡 [수정된 부분] Timestamp가 아닌 Role과 텍스트 접두사로 확실하게 백엔드 메모리 검색
                    var memMsg = currentSession.History.FirstOrDefault(m =>
                        m.Role == "user" &&
                        (m.Text.StartsWith("[초기 상황]\n") || m.Text.StartsWith("[초기 상황 설정]\n")));

                    if (memMsg != null) memMsg.Text = firstMsg.Text;
                }
                else
                {
                    // [신규 로직] 초기 시나리오가 없던 빈 세션이라면 맨 앞에 새롭게 주입
                    string scenarioMessage = $"[초기 상황]\n{result.Value.InitialScenario}";
                    ChatHistory.Insert(0, new ChatMessage("user", scenarioMessage, DateTime.Now));
                    _memoryService.InjectInitialScenario(result.Value.InitialScenario);
                }

                _ = Task.Run(async () => await GRC.Helpers.ChatDataHelper.SaveBranchedHistoryAsync(_currentFileName, ChatHistory.ToList()));
            }
            var updatedSession = _memoryService.ExportSession(CurrentPreset);
            await _sessionService.SaveSessionAsync(_currentFileName, updatedSession);

            UpdateStatusUI(currentSession.CurrentContext?.CustomStats, currentSession.CurrentContext?.Chars, currentSession.CurrentContext?.Items, currentSession.CurrentContext?.Places);
            ChatHistory.Add(new ChatMessage("system", "세계관, 스탯, 초기 시나리오가 성공적으로 업데이트되었습니다. 다음 턴부터 반영됩니다.", DateTime.Now));
        }
    }

    // ==========================================
    // [5. Commands (버튼 클릭 등 사용자 액션)]
    // ==========================================
    [RelayCommand]
    private void GoBack()
    {
        RequestGoBack?.Invoke();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendMessageAsync()
    {
        if (IsBusy)
        {
            _cancellationTokenSource?.Cancel();
            _audioService.StopVoiceSound();
            return;
        }

        if (CurrentPreset == null) return;

        IsBusy = true;
        _cancellationTokenSource = new CancellationTokenSource();
        bool isContinueMode = string.IsNullOrWhiteSpace(InputText);
        string displayUserText = isContinueMode ? "(스토리가 계속됩니다...)" : InputText;
        var finalUserMessage = new ChatMessage("user", displayUserText, DateTime.Now);

        InputText = string.Empty;
        SuggestedReplies.Clear();
        ChatHistory.Add(finalUserMessage);
        _ = Task.Run(async () => await GRC.Helpers.FullHistoryLogger.LogMessageAsync(_currentFileName, finalUserMessage));

        bool isChapterChanged = _memoryService.ConsumeChapterChangedFlag();

        // 메타 디렉션 추출 위임
        string? metaDirective = _chatWorkflowService.GetMetaDirective(
            isContinueMode ? "" : displayUserText,
            IsFastForwardEnabled,
            _memoryService.CurrentContext?.CustomStats,
            _memoryService.CurrentContext?.TriggeredMetaEvents,
            isChapterChanged);

        ChatMessage aiMessage = new ChatMessage("model", "", DateTime.Now);
        ChatHistory.Add(aiMessage);
        int aiMessageIndex = ChatHistory.Count - 1;

        CurrentStreamingText = string.Empty;
        bool isTypingSoundStarted = false;
        bool isDialogue = false;

        var appSettings = await _appSettingsService.LoadSettingsAsync();
        bool isTtsEnabled = appSettings.IsTtsEnabled;

        // 통신 및 비즈니스 로직 위임
        var result = await _chatWorkflowService.ProcessChatStreamAsync(
            finalUserMessage, CurrentPreset, metaDirective,
            onCharReceived: (c) =>
            {
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (c == '"' || c == '“' || c == '”')
                    {
                        isDialogue = !isDialogue;
                    }

                    bool shouldMuteTyping = isDialogue || (c == '"' || c == '“' || c == '”');
                    if (shouldMuteTyping)
                    {
                        if (isTypingSoundStarted)
                        {
                            _audioService.StopTypingSound();
                            isTypingSoundStarted = false;
                        }
                    }
                    else
                    {
                        if (!isTypingSoundStarted)
                        {
                            _audioService.StartTypingSound();
                            isTypingSoundStarted = true;
                        }
                    }

                    CurrentStreamingText += c;
                    OnCharReceived?.Invoke(c);
                });
            },
     onDialoguePrefetch: async (narration, dialogue) =>
     {
         if (isTtsEnabled && CurrentPreset != null)
         {
             //  현재 메모리에 활성화된 로어북 텍스트 중에서 '인물' 카테고리만 필터링하여 재조립
             var activeCharacterLorebooks = CurrentPreset.Lorebooks?
                 .Where(l => l.Category == "인물" && _memoryService.CurrentLorebookText.Contains($"[{l.Name}]"))
                 .Select(l => $"[{l.Name}]\n{l.Content}");

             string activeLorebook = activeCharacterLorebooks != null && activeCharacterLorebooks.Any()
                 ? string.Join("\n\n", activeCharacterLorebooks)
                 : "인물 정보 없음";

             string targetDialogue = dialogue;

             //  현재 TTS 언어 설정 로드
             var settings = await _appSettingsService.LoadSettingsAsync();

                 if (settings.SelectedTtsLanguage == TtsLanguage.Japanese)
                 {
                     var translationRequest = new GeminiRequest(
       SystemInstruction: new Content("system", [new Part("You are a professional translator. Translate the given dialogue into natural conversational Japanese. Do not add any extra explanations or quotes.")]),
       Contents: [new Content("user", [new Part(dialogue)])],
       SafetySettings: [
        new("HARM_CATEGORY_HARASSMENT", BlockThreshold.BLOCK_NONE),
        new("HARM_CATEGORY_HATE_SPEECH", BlockThreshold.BLOCK_NONE),
        new("HARM_CATEGORY_SEXUALLY_EXPLICIT", BlockThreshold.BLOCK_NONE),
        new("HARM_CATEGORY_DANGEROUS_CONTENT", BlockThreshold.BLOCK_NONE)
       ],
       GenerationConfig: new GenerationConfig(Temperature: 0.7f, MaxOutputTokens: 1024, ResponseMimeType: "text/plain")
   );

                     // FlashLite 모델을 사용해 백그라운드에서 신속하게 번역 (비동기)
                     string translatedText = await _apiService.SendMessageAsync(translationRequest, ModelTier.FlashLite);

                     // 시스템 오류 메세지가 반환되지 않고 정상 번역되었다면 덮어씌움
                     if (!string.IsNullOrWhiteSpace(translatedText) && !translatedText.StartsWith("[System"))
                     {
                         targetDialogue = translatedText.Trim();
                     }
                     //Debug.WriteLine(translatedText);
                 }

                 //  최종적으로 원본 대사 혹은 일본어로 번역된 대사(targetDialogue)를 TTS 엔진으로 전달
                 return await _ttsService.GenerateSpeechAsync(targetDialogue, CurrentPreset.Name, narration, activeLorebook);
             }
             return string.Empty;
         },
            onAudioReady: async (audioFilePath) =>
            {
                Task? playTask = null;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _audioService.StopTypingSound();
                    isTypingSoundStarted = false;
                    playTask = _audioService.PlayVoiceSoundAsync(audioFilePath);
                });

                if (playTask != null)
                {
                    await playTask; // 음성 재생이 끝날 때까지 텍스트 타이핑 멈춤
                }
            },
            onDownloadComplete: async (streamResult) =>
            {
                if (IsSuggestionEnabled && streamResult.IsSuccess)
                {
                    try
                    {
                        // UI 스레드의 ChatHistory를 복사하여 임시 메모리 생성
                        var tempMemory = ChatHistory.Where(m => !m.Text.StartsWith("[시스템")).TakeLast(9).ToList();

                        // 방금 서버에서 받아온 AI의 답변을 임시 메모리에 덧붙임
                        tempMemory.Add(new ChatMessage("model", streamResult.FinalText, DateTime.Now));

                        string currentContext = _memoryService.CurrentContext.ToPromptString();

                        // 백그라운드에서 추천 답변 생성
                        _prefetchedSuggestions = await _suggestionService.GenerateAsync(currentContext, tempMemory);
                    }
                    catch { }
                }
            },
            cancellationToken: _cancellationTokenSource.Token
        );

        _audioService.StopTypingSound();

        if (result.IsSuccess)
        {
            if (result.StatusPayload != null)
            {
                UpdateStatusUI(result.StatusPayload.CustomStats, result.StatusPayload.Chars, result.StatusPayload.Items, result.StatusPayload.Places);
                _memoryService.UpdateContextStatus(result.StatusPayload);
            }

            aiMessage.Text = result.FinalText;
            IsFastForwardEnabled = false;
            _memoryService.AddModelResponse(aiMessage);

            _ = Task.Run(async () => await GRC.Helpers.FullHistoryLogger.LogMessageAsync(_currentFileName, aiMessage));
        }
        else
        {
            ChatHistory.RemoveAt(aiMessageIndex);
            ChatHistory.Add(new ChatMessage("system", $"[시스템 에러]: {result.ErrorMessage}", DateTime.Now));
        }

        IsBusy = false;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        if (CurrentPreset != null)
        {
            var currentSession = _memoryService.ExportSession(CurrentPreset);
            await _sessionService.SaveSessionAsync(_currentFileName, currentSession);

            CurrentChapter = currentSession.ChapterCount;
            CurrentTurn = currentSession.TotalTurnCount;
            LongTermSummaryText = currentSession.LongTermSummary;
            PrevChapterPlot = currentSession.PrevContexts != null && currentSession.PrevContexts.Any()
                ? string.Join("\n\n", currentSession.PrevContexts.Select(ctx => ctx.Plot))
                : "기록 없음";
            CurrentChapterPlot = currentSession.CurrentContext?.Plot ?? "기록 없음";

            // 👇 [신규 추가] 타이핑이 끝난 후 준비된 프리페치 추천 답변 즉시 노출
            if (IsSuggestionEnabled)
            {
                if (_prefetchedSuggestions != null && _prefetchedSuggestions.Count > 0)
                {
                    SuggestedReplies.Clear();
                    foreach (var s in _prefetchedSuggestions) SuggestedReplies.Add(s);
                    _prefetchedSuggestions = null; // 사용 후 초기화
                }
                else
                {
                    // 실패했거나 타이핑이 너무 빨리 끝난 경우 기존 방식(재요청)으로 Fallback
                    await LoadSuggestionsAsync();
                }
            }
        }
    }

    [RelayCommand]
    private void OpenStatusWindow() => _dialogService.ShowStatusWindow(this);

    [RelayCommand]
    private void OpenStoryHistoryWindow() => _dialogService.ShowStoryHistoryWindow(this);


    [RelayCommand]
    private async Task DeleteMessageAsync(ChatMessage message)
    {
        // 스트리밍 중이거나 메시지가 없으면 무시
        if (message == null || IsBusy || CurrentPreset == null) return;

        // 1. UI 컬렉션에서 즉시 제거 (화면에서 스르륵 사라짐)
        ChatHistory.Remove(message);

        // 2. 메모리 서비스(단기 기억 리스트)에서 제거하여 다음 프롬프트에 안 들어가게 함
        _memoryService.DeleteMessage(message);

        // 3. 메시지가 삭제된 최신 상태를 파일에 덮어쓰기 (영구 반영)
        var currentSession = _memoryService.ExportSession(CurrentPreset);
        await _sessionService.SaveSessionAsync(_currentFileName, currentSession);
        await GRC.Helpers.FullHistoryLogger.DeleteMessageAsync(_currentFileName, message);
        CurrentTurn = currentSession.TotalTurnCount;
    }

    /// <summary>
    /// 특정 메시지 내용 복사 (우클릭 메뉴에서 호출)
    /// </summary>
    [RelayCommand]
    private void CopyMessage(ChatMessage message)
    {
        // 메시지가 비어있지 않을 때만 클립보드에 저장
        if (message != null && !string.IsNullOrWhiteSpace(message.Text))
        {
            try
            {
                Clipboard.SetText(message.Text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Clipboard Error]: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task ResendMessageAsync(ChatMessage message)
    {
        // 스트리밍 중이거나, 메시지가 없거나, AI의 메시지인 경우 무시
        if (IsBusy || message == null || message.Role != "user") return;

        // 1. 재전송할 텍스트를 임시 보관
        string textToResend = message.Text;

        // 2. 화면, 단기 기억, 파일 로그에서 기존 메시지를 완벽히 삭제 (중복 방지)
        await DeleteMessageAsync(message);

        // 3. 입력창에 텍스트를 넣고 전송 로직 즉시 실행
        InputText = textToResend;
        await SendMessageAsync();
    }

    [RelayCommand]
    private async Task BranchSessionAsync(ChatMessage targetMessage)
    {
        if (IsBusy || targetMessage == null || CurrentPreset == null) return;

        var currentSession = _memoryService.ExportSession(CurrentPreset);
        int memoryIndex = currentSession.History.FindIndex(m => m.Role == targetMessage.Role && m.Text == targetMessage.Text && m.Timestamp == targetMessage.Timestamp);

        if (memoryIndex == -1)
        {
            _dialogService.ShowAlert("너무 오래된 과거로는 분기할 수 없습니다.\n(이미 장기 서사로 압축된 구간입니다)", "분기 불가");
            return;
        }

        var branchedHistory = currentSession.History.Take(memoryIndex + 1).ToList();
        int cutUserTurns = currentSession.History.Skip(memoryIndex + 1).Count(m => m.Role == "user");

        var branchedSession = currentSession with
        {
            History = branchedHistory,
            TotalTurnCount = Math.Max(0, currentSession.TotalTurnCount - cutUserTurns)
        };

        string newSessionFileName = GRC.Helpers.ChatDataHelper.GenerateSessionFileName(CurrentPreset.Name + "_Branch");

        await _sessionService.SaveSessionAsync(newSessionFileName, branchedSession);
        await _presetService.SavePresetAsync(newSessionFileName, CurrentPreset);

        int globalIndex = ChatHistory.IndexOf(targetMessage);
        if (globalIndex != -1)
        {
            var branchedFullHistory = ChatHistory.Take(globalIndex + 1).ToList();

            await GRC.Helpers.ChatDataHelper.SaveBranchedHistoryAsync(newSessionFileName, branchedFullHistory);
        }

        _dialogService.ShowAlert("성공적으로 평행세계(분기)가 생성되었습니다!\n목록 화면에서 진입할 수 있습니다.", "분기 완료");
    }

    [RelayCommand]
    private async Task SaveMemoryAsync(ChatMessage targetMessage)
    {
        if (CurrentPreset == null || targetMessage == null) return;

        // 1. UI 피드백
        _dialogService.ShowAlert("선택한 대화를 백그라운드에서 기억으로 변환 중입니다...", "기억 추출");

        try
        {
            // 2. Service 계층에 추출 작업 위임 
            var newMemory = await _lorebookService.ExtractMemoryToLorebookAsync(targetMessage.Text);

            if (newMemory != null)
            {
                if (CurrentPreset.Lorebooks == null)
                {
                    CurrentPreset = CurrentPreset with { Lorebooks = new List<LorebookEntry>() };
                }

                var newEntry = newMemory with { Category = "기억", Priority = 1 };
                CurrentPreset.Lorebooks.Add(newEntry);

                await _presetService.SavePresetAsync(_currentFileName, CurrentPreset);

                var updatedSession = _memoryService.ExportSession(CurrentPreset);
                await _sessionService.SaveSessionAsync(_currentFileName, updatedSession);

                _dialogService.RunOnUIThread(() =>
                {
                    _dialogService.ShowAlert($"'{newEntry.Name}'(이)가 로어북에 저장되었습니다.", "저장 완료");
                });
            }
        }
        catch (Exception ex)
        {
            _dialogService.RunOnUIThread(() =>
            {
                _dialogService.ShowAlert($"기억 저장에 실패했습니다.\n\n사유: {ex.Message}", "기억 추출 실패");
            });
        }
    }

    [RelayCommand]
    private async Task ClearChatAsync()
    {
        if (IsBusy || CurrentPreset == null) return;

        if (!_dialogService.ShowConfirm("현재 대화 내역을 모두 지우시겠습니까?\n지워진 데이터는 복구할 수 없습니다.", "대화 비우기 확인"))
            return;

        _audioService.StopVoiceSound();
        string? savedInitialScenario = null;
        var firstMsg = ChatHistory.FirstOrDefault();
        if (firstMsg != null && firstMsg.Role == "user" && firstMsg.Text.StartsWith("[초기 상황]\n"))
        {
            savedInitialScenario = firstMsg.Text.Substring("[초기 상황]\n".Length);
        }

        ChatHistory.Clear();
        _memoryService.Clear();
        CharacterStatuses.Clear();
        InventoryItems.Clear();
        CurrentPlaces.Clear();
        CustomStatsUIList.Clear();

        await GRC.Helpers.FullHistoryLogger.ClearHistoryAsync(_currentFileName);

        if (!string.IsNullOrWhiteSpace(savedInitialScenario))
        {
            string scenarioMessage = $"[초기 상황]\n{savedInitialScenario}";
            var initialMsg = new ChatMessage("user", scenarioMessage, DateTime.Now);
            ChatHistory.Add(initialMsg);
            _memoryService.InjectInitialScenario(savedInitialScenario);
            _ = Task.Run(async () => await GRC.Helpers.FullHistoryLogger.LogMessageAsync(_currentFileName, initialMsg));
        }

        if (CurrentPreset.CustomStats != null)
        {
            _memoryService.UpdateContextStatus(new StatusPayload { CustomStats = new Dictionary<string, string>(CurrentPreset.CustomStats) });
        }

        var emptySession = _memoryService.ExportSession(CurrentPreset);
        await _sessionService.SaveSessionAsync(_currentFileName, emptySession);

        CurrentChapter = emptySession.ChapterCount;
        CurrentTurn = emptySession.TotalTurnCount;
        LongTermSummaryText = emptySession.LongTermSummary;
        PrevChapterPlot = emptySession.PrevContexts != null && emptySession.PrevContexts.Any()
            ? string.Join("\n\n", emptySession.PrevContexts.Select(ctx => ctx.Plot))
            : "기록 없음";
        CurrentChapterPlot = emptySession.CurrentContext?.Plot ?? "기록 없음";

        UpdateStatusUI(emptySession.CurrentContext?.CustomStats, emptySession.CurrentContext?.Chars, emptySession.CurrentContext?.Items, emptySession.CurrentContext?.Places);
        ChatHistory.Add(new ChatMessage("system", "대화가 초기화되었습니다. 새로운 이야기를 시작하세요.", DateTime.Now));
    }

    [RelayCommand]
    private void ToggleSuggestion()
    {
        if (!IsSuggestionEnabled)
        {
            // 유저가 토글을 다시 껐을 때 즉각적으로 백그라운드 통신 취소
            _suggestionCts?.Cancel();
            SuggestedReplies.Clear();
            IsLoadingSuggestions = false;
            return;
        }

        if (ChatHistory.Count > 0 && SuggestedReplies.Count == 0 && !IsBusy)
        {

            _ = LoadSuggestionsAsync();
        }
    }

    private async Task LoadSuggestionsAsync()
    {
        if (CurrentPreset == null) return;

        _suggestionCts?.Cancel();
        _suggestionCts = new CancellationTokenSource();
        var token = _suggestionCts.Token;

        IsLoadingSuggestions = true;
        SuggestedReplies.Clear();

        try
        {
            var currentSession = _memoryService.ExportSession(CurrentPreset);
            string currentContextString = currentSession.CurrentContext.ToPromptString();
            var recentMemory = ChatHistory.Where(m => !m.Text.StartsWith("[시스템...")).TakeLast(10).ToList();

            var suggestions = await _suggestionService.GenerateAsync(currentContextString, recentMemory);

            if (IsBusy || !IsSuggestionEnabled || token.IsCancellationRequested) return;

            foreach (var s in suggestions) { SuggestedReplies.Add(s); }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Suggestion UI Error]: {ex.Message}");
        }
        finally
        {
            // 작업이 유효하게 끝난(또는 에러난) 경우에만 로딩 상태 해제 (중간에 취소된 찌꺼기 작업이 로딩바를 끄는 것 방지)
            if (_suggestionCts != null && !token.IsCancellationRequested)
            {
                IsLoadingSuggestions = false;
            }
        }
    }

    [RelayCommand]
    private void UseSuggestion(string suggestion)
    {
        InputText = suggestion;
        //  클릭 즉시 바로 전송되길 원하신다면 아래 코드를 주석 해제하세요.
        _ = SendMessageAsync();
    }

    private async void UpdateRandomBackground()
    {
        var settings = await _appSettingsService.LoadSettingsAsync();
        CurrentBackgroundImage = _themeService.GetRandomBackground(settings.SelectedTheme, ref _unusedBackgrounds, CurrentBackgroundImage);
    }

    [RelayCommand]
    private async Task EditLorebookAsync()
    {
        if (CurrentPreset == null) return;
        var finalLorebooks = _dialogService.ShowEditLorebookDialog(CurrentPreset.Lorebooks);

        if (finalLorebooks != null)
        {
            CurrentPreset = CurrentPreset with { Lorebooks = finalLorebooks };
            await _presetService.SavePresetAsync(_currentFileName, CurrentPreset);

            var updatedSession = _memoryService.ExportSession(CurrentPreset);
            await _sessionService.SaveSessionAsync(_currentFileName, updatedSession);
            ChatHistory.Add(new ChatMessage("system", "로어북 데이터가 성공적으로 업데이트되었습니다.", DateTime.Now));
        }
    }


    private void UpdateStatusUI(Dictionary<string, string>? customStats, Dictionary<string, string>? chars,
        List<string>? items, List<string>? places)
    {
        _dialogService.RunOnUIThread(() =>
        {
            // 1. 커스텀 스탯 (게이지 바 처리 로직 및 정렬 추가됨)
            CustomStatsUIList.Clear();
            if (customStats != null)
            {
                // 정렬을 위해 임시 리스트에 먼저 담습니다.
                var tempStats = new List<CustomStatUI>();

                foreach (var kvp in customStats)
                {
                    var statItem = new CustomStatUI { Name = kvp.Key, Value = kvp.Value };

                    // "100/100"과 같이 슬래시(/)가 포함된 형태인지 확인하여 분리
                    if (!string.IsNullOrWhiteSpace(kvp.Value) && kvp.Value.Contains("/"))
                    {
                        var parts = kvp.Value.Split('/');
                        if (parts.Length == 2 &&
                            double.TryParse(parts[0].Trim(), out double current) &&
                            double.TryParse(parts[1].Trim(), out double max))
                        {
                            statItem.IsGauge = true;
                            statItem.CurrentValue = current;
                            statItem.MaxValue = max;
                        }
                    }

                    tempStats.Add(statItem);
                }

                // IsGauge가 true인 항목이 리스트의 앞쪽에 오도록 내림차순 정렬 후 실제 UI 리스트에 추가합니다.
                foreach (var statItem in tempStats.OrderByDescending(s => s.IsGauge))
                {
                    CustomStatsUIList.Add(statItem);
                }
            }

            // 2. 캐릭터 서사적 상태
            CharacterStatuses.Clear();
            if (chars != null)
            {
                foreach (var kvp in chars)
                {
                    CharacterStatuses.Add(new CharacterStatusUI { Name = kvp.Key, StatusText = kvp.Value });
                }
            }

            // 3. 인벤토리 아이템
            InventoryItems.Clear();
            if (items != null)
            {
                foreach (var item in items)
                {
                    string name = item;
                    string category = "Item";
                    string color = "#888888"; // 기본 색상 (회색)

                    InventoryItems.Add(new InventoryItemUI { Name = name, Category = category, BadgeColor = color });
                }
            }

            // 4. 현재 위치
            CurrentPlaces.Clear();
            if (places != null)
            {
                foreach (var place in places)
                {
                    CurrentPlaces.Add(place);
                }
            }
        });
    }

    public void Cleanup()
    {
        if (IsBusy) _cancellationTokenSource?.Cancel();
        _audioService.StopVoiceSound();
        _dialogService.CloseAuxiliaryWindows();
    }

}