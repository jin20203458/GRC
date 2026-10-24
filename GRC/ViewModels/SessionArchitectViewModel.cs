using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GRC.Models;
using GRC.Services;

namespace GRC.ViewModels;

public partial class SessionArchitectViewModel : ObservableObject
{
    private readonly ISessionArchitectService _architectService;

    // === UI 바인딩 프로퍼티 ===
    [ObservableProperty]
    private AgentStep _currentStep = AgentStep.Idle;

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = "";  // 하단 스트리밍 영역에 표시할 동적 상태 문구

    [ObservableProperty]
    private bool _editMode;  // 기존 세션 수정 모드 여부

    [ObservableProperty]
    private bool _isAutoMode = true;  // 기본값 true: 원클릭 스포일러 프리 자동 완주 모드

    [ObservableProperty]
    private AgentPlan? _plan; // 파싱된 전체 계획

    public ObservableCollection<ArchitectMessage> Messages { get; } = [];

    // 에이전트 내부 세션 상태
    private readonly ArchitectSession _session = new();
    private CancellationTokenSource? _cts;

    // === 이벤트 ===
    public event Action<string>? SessionCreated;  // MainViewModel 또는 SessionListViewModel에게 알림

    public SessionArchitectViewModel(ISessionArchitectService architectService)
    {
        _architectService = architectService;
        
        // 초기 웰컴 메시지
        Messages.Add(new ArchitectMessage
        {
            Role = "assistant",
            Text = "안녕하세요! AI 세션 아키텍트 2.0입니다. 플레이하고 싶은 TRPG 세션의 장르, 분위기, 시작 컨셉을 자유롭게 입력해 주세요.\n\n예: \"평소엔 밝고 유쾌한 아카데미 학원물이지만, 학원 도시 이면에는 어두운 비밀이 도사린 서브컬처 판타지\"\n\n현재 **자동** 모드가 켜져 있어, 컨셉만 입력하시면 스포일러 없이 세계관·로어북·오프닝까지 최고 품질로 자동 완성 후 바로 시작됩니다. (단계별로 직접 검수하며 수정하려면 우측 하단 **자동** 토글을 꺼주세요.)",
            Timestamp = DateTime.Now
        });
    }

    /// <summary>
    /// 기존 세션 수정을 위해 아키텍트 정보를 초기화합니다.
    /// </summary>
    public void InitializeForEdit(string sessionFileName, CharacterPreset preset)
    {
        EditMode = true;
        _session.ExistingSessionFileName = sessionFileName;
        _session.ExistingPreset = preset;

        // 기존 데이터 로드하여 프리셋 세션에 적재
        _session.GeneratedWorldview = preset.Worldview;
        _session.GeneratedLorebooks = preset.Lorebooks ?? [];
        _session.GeneratedStats = preset.CustomStats ?? [];
        _session.GeneratedStatusGuide = preset.StatusUpdateGuide;
        _session.GeneratedSystemPrompt = preset.SystemPrompt;

        Messages.Clear();
        Messages.Add(new ArchitectMessage
        {
            Role = "assistant",
            Text = $"기존 세션 '{preset.Name}'을 편집하는 모드로 시작합니다. 수정하거나 발전시키고 싶은 방향에 대해 설명해 주세요. 전체 계획을 새로 설계하겠습니다.",
            Timestamp = DateTime.Now
        });
    }

    [RelayCommand]
    public void Cancel()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            IsBusy = false;

            // 자율 모드 중 중단 시 수동 모드로 전환하여 유저에게 제어권 반환
            if (IsAutoMode)
            {
                IsAutoMode = false;
                Messages.Add(new ArchitectMessage
                {
                    Role = "system",
                    Text = "자동 진행이 중단되었습니다. 수동 모드로 전환되었으며, 현재 단계부터 직접 검토하실 수 있습니다.",
                    Timestamp = DateTime.Now
                });

                // 현재 생성 중이던 단계의 검토 상태로 전환하여 승인 버튼 노출
                if (IsGenStep(CurrentStep))
                {
                    CurrentStep = GetReviewStepFromGen(CurrentStep);
                }
            }
            else
            {
                Messages.Add(new ArchitectMessage
                {
                    Role = "system",
                    Text = "작업이 사용자에 의해 중단되었습니다.",
                    Timestamp = DateTime.Now
                });
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    public async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;

        string userMsg = InputText;
        InputText = "";
        IsBusy = true;

        // 유저 메시지 UI 추가
        Messages.Add(new ArchitectMessage
        {
            Role = "user",
            Text = userMsg,
            Timestamp = DateTime.Now
        });

        _cts = new CancellationTokenSource();

        try
        {
            if (CurrentStep == AgentStep.Idle)
            {
                // 1. 계획 설계 시작
                CurrentStep = AgentStep.Planning;
                BusyMessage = GetBusyMessage(AgentStep.Planning);

                var stream = _architectService.GeneratePlanAsync(userMsg, _session.ExistingPreset, _cts.Token);
                var planText = await ConsumeStreamAsync(stream, _cts.Token);

                // JSON 파싱 시도 (1단계에서 청사진과 4대 섹션 마스터 세계관을 동시 확보)
                var parsedPlan = _architectService.ParsePlan(planText);
                if (parsedPlan != null)
                {
                    parsedPlan.Concept = userMsg; // 컨셉 보존
                    Plan = parsedPlan;
                    _session.Plan = parsedPlan;
                    _session.GeneratedWorldview = SessionArchitectService.SanitizeWorldview(parsedPlan.WorldviewOutline);

                    // 자율 모드에서는 스포일러 방지 요약 카드만 표시, 수동 모드에서는 전체 기획안 표시
                    Messages.Add(new ArchitectMessage
                    {
                        Role = "assistant",
                        Text = IsAutoMode ? GetAutoPlanSummaryMarkdown(parsedPlan) : GetPlanSummaryMarkdown(parsedPlan),
                        RelatedStep = AgentStep.PlanReview,
                        HasActionButtons = !IsAutoMode,
                        Timestamp = DateTime.Now
                    });

                    CurrentStep = AgentStep.PlanReview;

                    // 자율 모드: 1단계(청사진·세계관) 완료 후 나머지 3단계(로어북 -> 시나리오 -> 상태창·지시문)를 자동 순회
                    if (IsAutoMode)
                    {
                        await RunAutoLoopAsync();
                    }
                }
                else
                {
                    // 파싱 실패 시 원본 응답 텍스트 출력 후 재시도 유도
                    Messages.Add(new ArchitectMessage
                    {
                        Role = "assistant",
                        Text = $"계획 수립 형식 파싱에 실패했습니다. AI 응답:\n\n{planText}\n\n다른 컨셉이나 명령을 다시 입력해 주세요.",
                        Timestamp = DateTime.Now
                    });
                    CurrentStep = AgentStep.Idle;
                }
            }
            else if (IsReviewStep(CurrentStep))
            {
                // 2. 각 리뷰 단계에서의 수정 요청 (Revision)
                AgentStep originalStep = CurrentStep;
                AgentStep genStep = GetGenStepFromReview(originalStep);
                CurrentStep = genStep;
                BusyMessage = GetBusyMessage(genStep);

                string previousContent = GetCurrentStepContent(originalStep);

                var stream = _architectService.ReviseContentAsync(originalStep, previousContent, userMsg, _session.Plan!, _session, _cts.Token);
                var revisedText = await ConsumeStreamAsync(stream, _cts.Token);

                // 수정 내용 파싱 및 업데이트
                bool success = ProcessStepContent(originalStep, revisedText);
                if (success)
                {
                    DisablePreviousActionButtons(originalStep);

                    Messages.Add(new ArchitectMessage
                    {
                        Role = "assistant",
                        Text = GetStepDisplayContent(originalStep),
                        RelatedStep = originalStep,
                        HasActionButtons = true,
                        Timestamp = DateTime.Now
                    });
                }
                else
                {
                    Messages.Add(new ArchitectMessage
                    {
                        Role = "assistant",
                        Text = $"수정 사항을 파싱하거나 저장하는 데 실패했습니다. AI 응답:\n\n{revisedText}",
                        Timestamp = DateTime.Now
                    });
                }

                CurrentStep = originalStep;
            }
            else
            {
                Messages.Add(new ArchitectMessage
                {
                    Role = "assistant",
                    Text = "현재 단계에서 지원하지 않는 조작입니다. [승인, 다음 단계]를 누르거나 명확한 수정 요청을 입력해주세요.",
                    Timestamp = DateTime.Now
                });
            }
        }
        catch (OperationCanceledException)
        {
            if (CurrentStep == AgentStep.Planning) CurrentStep = AgentStep.Idle;
            else if (IsGenStep(CurrentStep)) CurrentStep = GetReviewStepFromGen(CurrentStep);
        }
        catch (Exception ex)
        {
            Messages.Add(new ArchitectMessage
            {
                Role = "system",
                Text = $"에러 발생: {ex.Message}",
                Timestamp = DateTime.Now
            });
            if (CurrentStep == AgentStep.Planning) CurrentStep = AgentStep.Idle;
            else if (IsGenStep(CurrentStep)) CurrentStep = GetReviewStepFromGen(CurrentStep);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    private bool CanSend() => !IsBusy;

    /// <summary>
    /// 자율 모드: 1단계(PlanReview, 청사진·세계관) 완료 후 나머지 3단계를 인과 순서(로어북 -> 시나리오 -> 상태창·지시문)로 자동 순회합니다.
    /// 중간에 중단(Cancel) 또는 파싱 실패 시 수동 모드로 자동 전환합니다.
    /// </summary>
    private async Task RunAutoLoopAsync(AgentStep startStep = AgentStep.LorebookGen)
    {
        AgentStep[] autoSteps =
        {
            AgentStep.LorebookGen,
            AgentStep.ScenarioGen,
            AgentStep.StatusGen
        };

        var stepsToRun = autoSteps.SkipWhile(s => s != startStep).ToArray();

        foreach (var genStep in stepsToRun)
        {
            if (_cts?.IsCancellationRequested == true) return;

            CurrentStep = genStep;
            BusyMessage = GetBusyMessage(genStep);

            try
            {
                var stream = _architectService.GenerateStepContentAsync(
                    genStep, _session.Plan!, _session, null, _cts!.Token);

                var stepContentText = await ConsumeStreamAsync(stream, _cts!.Token);

                AgentStep reviewStep = GetReviewStepFromGen(genStep);
                bool success = ProcessStepContent(reviewStep, stepContentText);

                if (success)
                {
                    // ── 자가 검토 단계 (Evaluator-Optimizer) ──
                    BusyMessage = GetBusyMessage(reviewStep, isReviewing: true);

                    var (pass, feedback) = await _architectService.ReviewStepContentAsync(
                        reviewStep, GetCurrentStepContent(reviewStep),
                        _session.Plan!, _session, _cts!.Token);

                    if (!pass && feedback != null)
                    {
                        Messages.Add(new ArchitectMessage
                        {
                            Role = "system",
                            Text = $"자가 검수 보완 진행: {feedback}",
                            Timestamp = DateTime.Now
                        });

                        // 1회 자동 리파인 (로어북은 외과수술식 핀포인트 배치 보정)
                        BusyMessage = GetBusyMessage(reviewStep, isRevising: true);
                        var reviseStream = _architectService.ReviseContentAsync(
                            reviewStep, GetCurrentStepContent(reviewStep), feedback,
                            _session.Plan!, _session, _cts!.Token);

                        var revisedContentText = await ConsumeStreamAsync(reviseStream, _cts!.Token);

                        success = ProcessStepContent(reviewStep, revisedContentText);
                        if (!success)
                        {
                            IsAutoMode = false;
                            CurrentStep = reviewStep;
                            Messages.Add(new ArchitectMessage
                            {
                                Role = "system",
                                Text = "자동 수정 후에도 형식 오류가 해결되지 않아 수동 모드로 전환합니다.",
                                Timestamp = DateTime.Now
                            });
                            return;
                        }
                    }

                    // 자율 모드: 스포일러 방지 완료 요약 배지만 출력하고 다음 단계로 즉시 진행
                    Messages.Add(new ArchitectMessage
                    {
                        Role = "assistant",
                        Text = GetAutoStepCompletionSummary(reviewStep),
                        RelatedStep = reviewStep,
                        HasActionButtons = false,
                        Timestamp = DateTime.Now
                    });
                    CurrentStep = reviewStep;
                }
                else
                {
                    Messages.Add(new ArchitectMessage
                    {
                        Role = "system",
                        Text = $"자동 생성 중 파싱 오류가 발생하여 수동 모드로 전환합니다.\n\nAI 응답:\n{stepContentText}",
                        Timestamp = DateTime.Now
                    });
                    IsAutoMode = false;
                    CurrentStep = reviewStep;
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        // 6단계 모두 완료 → 세션 파일 적용
        if (_cts?.IsCancellationRequested != true)
        {
            await ApplySessionAsync();
        }
    }

    /// <summary>
    /// 현재 검토 중인 단계의 생성물을 확정하고 다음 단계 생성을 트리거합니다.
    /// </summary>
    [RelayCommand]
    public async Task ApproveAndProceedAsync()
    {
        if (IsBusy) return;

        AgentStep previousReviewStep = CurrentStep;
        AgentStep nextGenStep = GetNextGenStep(CurrentStep);
        if (nextGenStep == AgentStep.Complete)
        {
            await ApplySessionAsync();
            return;
        }

        DisablePreviousActionButtons(CurrentStep);

        _cts = new CancellationTokenSource();

        if (IsAutoMode)
        {
            IsBusy = true;
            try
            {
                await RunAutoLoopAsync(nextGenStep);
            }
            catch (Exception ex)
            {
                Messages.Add(new ArchitectMessage
                {
                    Role = "system",
                    Text = $"에러 발생: {ex.Message}",
                    Timestamp = DateTime.Now
                });
            }
            finally
            {
                IsBusy = false;
                _cts = null;
            }
            return;
        }

        IsBusy = true;
        CurrentStep = nextGenStep;
        BusyMessage = GetBusyMessage(nextGenStep);

        try
        {
            var stream = _architectService.GenerateStepContentAsync(nextGenStep, _session.Plan!, _session, null, _cts.Token);
            var stepContentText = await ConsumeStreamAsync(stream, _cts.Token);

            AgentStep reviewStep = GetReviewStepFromGen(nextGenStep);
            bool success = ProcessStepContent(reviewStep, stepContentText);
            if (success)
            {
                Messages.Add(new ArchitectMessage
                {
                    Role = "assistant",
                    Text = GetStepDisplayContent(reviewStep),
                    RelatedStep = reviewStep,
                    HasActionButtons = true,
                    Timestamp = DateTime.Now
                });
                CurrentStep = reviewStep;
            }
            else
            {
                Messages.Add(new ArchitectMessage
                {
                    Role = "assistant",
                    Text = $"컨텐츠 생성 파싱에 실패했습니다. AI 응답:\n\n{stepContentText}\n\n요구 사항을 다시 입력하거나 새로 시도해 주세요.",
                    Timestamp = DateTime.Now
                });
                CurrentStep = previousReviewStep;
            }
        }
        catch (OperationCanceledException)
        {
            CurrentStep = previousReviewStep;
        }
        catch (Exception ex)
        {
            Messages.Add(new ArchitectMessage
            {
                Role = "system",
                Text = $"에러 발생: {ex.Message}",
                Timestamp = DateTime.Now
            });
            CurrentStep = previousReviewStep;
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    /// <summary>
    /// 최종 기획 내용을 세션에 물리적으로 적용합니다.
    /// </summary>
    private async Task ApplySessionAsync()
    {
        IsBusy = true;
        CurrentStep = AgentStep.Applying;
        BusyMessage = GetBusyMessage(AgentStep.Applying);

        try
        {
            string fileName;
            if (EditMode)
            {
                await _architectService.ApplyToExistingSessionAsync(_session);
                fileName = _session.ExistingSessionFileName!;
                Messages.Add(new ArchitectMessage
                {
                    Role = "assistant",
                    Text = "기존 세션에 성공적으로 변경 사항이 적용되었습니다. 잠시 후 세션이 리로드됩니다.",
                    Timestamp = DateTime.Now
                });
            }
            else
            {
                fileName = await _architectService.ApplyToNewSessionAsync(_session);
                Messages.Add(new ArchitectMessage
                {
                    Role = "assistant",
                    Text = "새로운 세션이 생성되었습니다. 잠시 후 첫 장면으로 진입합니다.",
                    Timestamp = DateTime.Now
                });
            }

            CurrentStep = AgentStep.Complete;
            
            await Task.Delay(1500);
            SessionCreated?.Invoke(fileName);
        }
        catch (Exception ex)
        {
            Messages.Add(new ArchitectMessage
            {
                Role = "system",
                Text = $"세션 적용 중 치명적 오류 발생: {ex.Message}",
                Timestamp = DateTime.Now
            });
            CurrentStep = AgentStep.PromptReview;
        }
        finally
        {
            IsBusy = false;
        }
    }

    #region Helper Methods

    private bool IsReviewStep(AgentStep step)
    {
        return step == AgentStep.PlanReview ||
               step == AgentStep.WorldviewReview ||
               step == AgentStep.LorebookReview ||
               step == AgentStep.ScenarioReview ||
               step == AgentStep.StatusReview ||
               step == AgentStep.PromptReview;
    }

    private bool IsGenStep(AgentStep step)
    {
        return step == AgentStep.Planning ||
               step == AgentStep.WorldviewGen ||
               step == AgentStep.LorebookGen ||
               step == AgentStep.ScenarioGen ||
               step == AgentStep.StatusGen ||
               step == AgentStep.PromptGen;
    }

    private AgentStep GetGenStepFromReview(AgentStep reviewStep) => reviewStep switch
    {
        AgentStep.PlanReview => AgentStep.Planning,
        AgentStep.WorldviewReview => AgentStep.WorldviewGen,
        AgentStep.LorebookReview => AgentStep.LorebookGen,
        AgentStep.ScenarioReview => AgentStep.ScenarioGen,
        AgentStep.StatusReview => AgentStep.StatusGen,
        AgentStep.PromptReview => AgentStep.PromptGen,
        _ => AgentStep.Idle
    };

    private AgentStep GetReviewStepFromGen(AgentStep genStep) => genStep switch
    {
        AgentStep.Planning => AgentStep.PlanReview,
        AgentStep.WorldviewGen => AgentStep.WorldviewReview,
        AgentStep.LorebookGen => AgentStep.LorebookReview,
        AgentStep.ScenarioGen => AgentStep.ScenarioReview,
        AgentStep.StatusGen => AgentStep.StatusReview,
        AgentStep.PromptGen => AgentStep.PromptReview,
        _ => AgentStep.Idle
    };

    private AgentStep GetNextGenStep(AgentStep currentReviewStep) => currentReviewStep switch
    {
        AgentStep.PlanReview => AgentStep.LorebookGen,
        AgentStep.WorldviewReview => AgentStep.LorebookGen,
        AgentStep.LorebookReview => AgentStep.ScenarioGen,
        AgentStep.ScenarioReview => AgentStep.StatusGen,
        AgentStep.StatusReview => AgentStep.Complete,
        AgentStep.PromptReview => AgentStep.Complete,
        _ => AgentStep.Idle
    };

    /// <summary>
    /// 현재 단계의 데이터(텍스트/JSON)를 가져옵니다.
    /// </summary>
    private string GetCurrentStepContent(AgentStep reviewStep) => reviewStep switch
    {
        AgentStep.PlanReview => JsonSerializer.Serialize(_session.Plan),
        AgentStep.WorldviewReview => _session.GeneratedWorldview ?? "",
        AgentStep.LorebookReview => JsonSerializer.Serialize(_session.GeneratedLorebooks),
        AgentStep.ScenarioReview => _session.GeneratedScenario ?? "",
        AgentStep.StatusReview => JsonSerializer.Serialize(new StatusDesignPayload
        {
            Stats = _session.GeneratedStats ?? new(),
            Guide = _session.GeneratedStatusGuide ?? "",
            InitialChars = _session.GeneratedInitialChars ?? new(),
            InitialPlaces = _session.GeneratedInitialPlaces ?? [],
            InitialItems = _session.GeneratedInitialItems ?? []
        }),
        AgentStep.PromptReview => _session.GeneratedSystemPrompt ?? "",
        _ => ""
    };

    /// <summary>
    /// AI 원본 응답 텍스트를 구조화된 데이터로 변환하여 세션 상태에 반영합니다.
    /// </summary>
    private bool ProcessStepContent(AgentStep reviewStep, string rawResponse)
    {
        switch (reviewStep)
        {
            case AgentStep.PlanReview:
                var plan = _architectService.ParsePlan(rawResponse);
                if (plan != null)
                {
                    plan.Concept = _session.Plan?.Concept ?? "";
                    _session.Plan = plan;
                    Plan = plan;
                    _session.GeneratedWorldview = SessionArchitectService.SanitizeWorldview(plan.WorldviewOutline);
                    return true;
                }
                return false;

            case AgentStep.WorldviewReview:
                _session.GeneratedWorldview = SessionArchitectService.SanitizeWorldview(rawResponse);
                return !string.IsNullOrWhiteSpace(_session.GeneratedWorldview);

            case AgentStep.LorebookReview:
                var lorebooks = _architectService.ParseLorebooks(rawResponse);
                if (lorebooks != null && lorebooks.Count > 0)
                {
                    _session.GeneratedLorebooks = lorebooks;
                    return true;
                }
                return false;

            case AgentStep.ScenarioReview:
                _session.GeneratedScenario = SessionArchitectService.SanitizeScenario(rawResponse);
                return !string.IsNullOrWhiteSpace(_session.GeneratedScenario);

            case AgentStep.StatusReview:
                var fullPayload = _architectService.ParseStatusDesignPayload(rawResponse, _session.GeneratedLorebooks);
                if (fullPayload != null)
                {
                    _session.GeneratedStats = fullPayload.Stats;
                    _session.GeneratedStatusGuide = fullPayload.Guide;
                    _session.GeneratedInitialChars = fullPayload.InitialChars;
                    _session.GeneratedInitialPlaces = fullPayload.InitialPlaces;
                    _session.GeneratedInitialItems = fullPayload.InitialItems;

                    string rawDirectives = fullPayload.GenreDirectives != null && fullPayload.GenreDirectives.Count > 0
                        ? string.Join("\n", fullPayload.GenreDirectives)
                        : "";
                    _session.GeneratedSystemPrompt = SessionArchitectService.CompileHybridSystemPrompt(rawDirectives, _session.Plan);
                    return true;
                }
                var legacyDesign = _architectService.ParseStatusDesign(rawResponse);
                if (legacyDesign != null)
                {
                    _session.GeneratedStats = legacyDesign.Value.Stats;
                    _session.GeneratedStatusGuide = legacyDesign.Value.Guide;
                    _session.GeneratedSystemPrompt = SessionArchitectService.CompileHybridSystemPrompt("", _session.Plan);
                    return true;
                }
                return false;

            case AgentStep.PromptReview:
                _session.GeneratedSystemPrompt = rawResponse.Trim().StartsWith("<system>", StringComparison.OrdinalIgnoreCase)
                    ? rawResponse.Trim()
                    : SessionArchitectService.CompileHybridSystemPrompt(rawResponse, _session.Plan);
                return !string.IsNullOrWhiteSpace(_session.GeneratedSystemPrompt);

            default:
                return false;
        }
    }

    /// <summary>
    /// 자율 모드 실행 시 스포일러 없이 진행 현황만 간결하게 안내하는 마크다운을 생성합니다.
    /// </summary>
    private string GetAutoPlanSummaryMarkdown(AgentPlan plan)
    {
        var sb = new StringBuilder();
        string title = !string.IsNullOrWhiteSpace(plan.SessionTitle) ? plan.SessionTitle : "신규 롤플레잉 세션";
        sb.AppendLine($"### [1/4] 서사 청사진 및 마스터 세계관 구축 완료: {title}");
        if (!string.IsNullOrWhiteSpace(plan.SurfaceTone))
            sb.AppendLine($"- **일상/표면 호흡:** {plan.SurfaceTone}");
        if (!string.IsNullOrWhiteSpace(plan.CoreWeight))
            sb.AppendLine($"- **서사/갈등 무게감:** {plan.CoreWeight}");
        sb.AppendLine($"- **설계 규모:** 4대 거시 세계관 섹션, 로어북 **{plan.LorebookPlan.Count}건**, 추적 스탯 **{plan.StatsPlan.Count}건**");
        sb.AppendLine("\n*(스포일러 방지를 위해 세부 로어북과 심층 세계관 본문은 화면에 미리 노출하지 않고 백그라운드에서 생성합니다.)*");
        return sb.ToString();
    }

    private string GetAutoStepCompletionSummary(AgentStep reviewStep) => reviewStep switch
    {
        AgentStep.WorldviewReview => "### [1/4] 마스터 세계관 구축 완료\n- `<master_setting>` 전용 4대 섹션 고밀도 레퍼런스가 생성 및 정제되었습니다.",
        AgentStep.LorebookReview => $"### [2/4] 2-Wave 앙상블 로어북 심층 생성 완료\n- 총 **{_session.GeneratedLorebooks?.Count ?? 0}개**의 배경/인물 로어북과 상호 관계망이 2-Wave 통합 앙상블 파이프라인으로 생성되었습니다.",
        AgentStep.ScenarioReview => "### [3/4] 오프닝 시나리오 집필 완료\n- 세계관과 등장인물 외양·말투가 반영된 첫 직면 장면이 준비되었습니다.",
        AgentStep.StatusReview => $"### [4/4] 상태창·Turn 0 시딩 및 시스템 지시문 컴파일 완료\n- 오프닝 장면과 100% 동기화된 추적 스탯 **{_session.GeneratedStats?.Count ?? 0}개**, 초기 컨텍스트 및 하이브리드 시스템 지시문이 완성되었습니다.",
        AgentStep.PromptReview => "### [4/4] 하이브리드 시스템 지시문 컴파일 완료\n- 코어 행동 헌법과 전수 컨텍스트 기반 장르 연출 수칙이 결합되었습니다.",
        _ => "단계 완료"
    };

    /// <summary>
    /// 수동 검토 모드에서 UI에 렌더링할 단계별 상세 마크다운/텍스트를 구성합니다.
    /// </summary>
    private string GetStepDisplayContent(AgentStep reviewStep) => reviewStep switch
    {
        AgentStep.PlanReview => GetPlanSummaryMarkdown(_session.Plan!),
        AgentStep.WorldviewReview => $"### 세계관 설정 구성\n\n{_session.GeneratedWorldview}",
        AgentStep.LorebookReview => GetLorebookSummaryMarkdown(_session.GeneratedLorebooks!),
        AgentStep.ScenarioReview => $"### 초기 시나리오 오프닝\n\n{_session.GeneratedScenario}",
        AgentStep.StatusReview => GetStatusSummaryMarkdown(_session.GeneratedStats!, _session.GeneratedStatusGuide!),
        AgentStep.PromptReview => $"### 시스템 지시문 (System Instruction)\n\n```xml\n{_session.GeneratedSystemPrompt}\n```",
        _ => ""
    };

    private string GetPlanSummaryMarkdown(AgentPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### [1/4] AI 설계 청사진 및 마스터 세계관: {plan.SessionTitle}");
        if (!string.IsNullOrWhiteSpace(plan.SurfaceTone))
            sb.AppendLine($"- **일상/표면 호흡:** {plan.SurfaceTone}");
        if (!string.IsNullOrWhiteSpace(plan.CoreWeight))
            sb.AppendLine($"- **배경/갈등 무게감:** {plan.CoreWeight}");
        if (!string.IsNullOrWhiteSpace(plan.PcInitialPosition))
            sb.AppendLine($"- **주인공 초기 입지:** {plan.PcInitialPosition}");
        sb.AppendLine($"\n#### 마스터 세계관 (`<master_setting>`)\n{plan.WorldviewOutline}\n");
        sb.AppendLine("#### 생성 예정 로어북 항목 (그물망 연결 구조)");
        foreach (var item in plan.LorebookPlan)
        {
            string conn = !string.IsNullOrWhiteSpace(item.ConnectedEntities) ? $" *(연결망: {item.ConnectedEntities})*" : "";
            sb.AppendLine($"- **{item.Name}** ({item.Category}, 우선순위 {item.Priority}): {item.Brief}{conn}");
        }
        sb.AppendLine("\n#### 설계 예정 스탯창 구성");
        sb.AppendLine(string.Join(", ", plan.StatsPlan.Select(s => $"`{s}`")));
        sb.AppendLine($"\n#### 시나리오 방향성: {plan.ScenarioOutline}");
        sb.AppendLine($"#### 시스템 프롬프트 구성: {plan.PromptOutline}");
        return sb.ToString();
    }

    private string GetLorebookSummaryMarkdown(List<LorebookEntry> lorebooks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### [2/4] 생성된 로어북 데이터 세트");
        sb.AppendLine($"총 **{lorebooks.Count}**개의 백과사전 항목이 생성되었습니다.\n");
        foreach (var item in lorebooks)
        {
            sb.AppendLine($"#### {item.Name} ({item.Category})");
            sb.AppendLine($"- **키워드:** {string.Join(", ", item.Keywords)}");
            sb.AppendLine($"{item.Content}\n");
        }
        return sb.ToString();
    }

    private string GetStatusSummaryMarkdown(Dictionary<string, string> stats, string guide)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### [4/4] 상태창·Turn 0 시딩 및 시스템 지시문 설계");
        sb.AppendLine("#### [초기 스탯]");
        foreach (var stat in stats)
        {
            sb.AppendLine($"- **{stat.Key}**: `{stat.Value}`");
        }
        if (_session.GeneratedInitialChars != null && _session.GeneratedInitialChars.Count > 0)
        {
            sb.AppendLine("\n#### [Turn 0 등장인물 초기 상태]");
            foreach (var ch in _session.GeneratedInitialChars)
            {
                sb.AppendLine($"- **{ch.Key}**: {ch.Value}");
            }
        }
        sb.AppendLine("\n#### [스탯 업데이트 가이드라인]");
        sb.AppendLine(guide);
        if (!string.IsNullOrWhiteSpace(_session.GeneratedSystemPrompt))
        {
            sb.AppendLine("\n#### [컴파일된 하이브리드 시스템 지시문]");
            sb.AppendLine($"```xml\n{_session.GeneratedSystemPrompt}\n```");
        }
        return sb.ToString();
    }

    private void DisablePreviousActionButtons(AgentStep step)
    {
        foreach (var msg in Messages.Where(m => m.RelatedStep == step))
        {
            msg.HasActionButtons = false;
        }
        for (int i = 0; i < Messages.Count; i++)
        {
            if (Messages[i].RelatedStep == step)
            {
                var oldMsg = Messages[i];
                Messages[i] = new ArchitectMessage
                {
                    Role = oldMsg.Role,
                    Text = oldMsg.Text,
                    RelatedStep = oldMsg.RelatedStep,
                    HasActionButtons = false,
                    Timestamp = oldMsg.Timestamp
                };
            }
        }
    }

    /// <summary>
    /// 현재 동작에 맞는 하단 상태 문구를 반환합니다.
    /// </summary>
    private string GetBusyMessage(AgentStep step, bool isReviewing = false, bool isRevising = false) =>
        (step, isReviewing, isRevising) switch
        {
            (_, _, true)                       => "AI가 검토 피드백을 바탕으로 외과수술식 보정을 수행하는 중",
            (_, true, _)                       => "생성된 데이터의 구조 및 정합성을 검증하는 중",
            (AgentStep.Planning, _, _)         => "Pro 모델이 그물망 서사 청사진 및 4대 섹션 마스터 세계관을 설계하는 중",
            (AgentStep.WorldviewGen, _, _)     => "Pro 모델이 고밀도 마스터 세계관을 구축하는 중",
            (AgentStep.LorebookGen, _, _)      => "Pro 모델이 2-Wave 통합 앙상블 파이프라인으로 로어북을 심층 생성하는 중",
            (AgentStep.ScenarioGen, _, _)      => "Pro 모델이 세계관과 로어북을 투영해 오프닝 시나리오를 집필하는 중",
            (AgentStep.StatusGen, _, _)        => "Pro 모델이 오프닝 기반 상태창·Turn 0 시딩 및 시스템 지시문을 추출·컴파일하는 중",
            (AgentStep.PromptGen, _, _)        => "전수 컨텍스트 기반 하이브리드 시스템 지시문을 컴파일하는 중",
            (AgentStep.Applying, _, _)         => "768차원 병렬 임베딩 발급 및 세션 파일을 적용하는 중",
            _                                  => "AI가 답변을 구성하는 중"
        };

    /// <summary>
    /// IAsyncEnumerable 스트리밍을 소비하여 최종 완성된 전체 텍스트를 반환합니다.
    /// </summary>
    private async Task<string> ConsumeStreamAsync(
        IAsyncEnumerable<string> stream, CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            var sb = new StringBuilder();

            try
            {
                await foreach (var chunk in stream.WithCancellation(ct).ConfigureAwait(false))
                {
                    sb.Append(chunk);
                }
            }
            catch (OperationCanceledException)
            {
                // 취소 시 무시
            }

            return sb.ToString();
        }, ct);
    }

    #endregion
}
