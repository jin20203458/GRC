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
    private string _streamingText = "";  // AI 실시간 타이핑 버퍼

    [ObservableProperty]
    private bool _editMode;  // 기존 세션 수정 모드 여부

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
            Text = "안녕하세요! AI 세션 아키텍트입니다. 만들고 싶으신 TRPG 세션의 장르, 세계관, 분위기 등의 컨셉을 자유롭게 알려주세요. 제가 세션 기획안 수립부터 파일 생성까지 전 과정을 도와드리겠습니다.\n\n예: \"다크 판타지, 타락한 신전과 성녀, 고딕 분위기, 이단 심판관과 마녀 사냥\"",
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
            StreamingText = "";
            Messages.Add(new ArchitectMessage
            {
                Role = "system",
                Text = "작업이 사용자에 의해 중단되었습니다.",
                Timestamp = DateTime.Now
            });
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
                StreamingText = "";

                var stream = _architectService.GeneratePlanAsync(userMsg, _session.ExistingPreset, _cts.Token);
                await foreach (var chunk in stream)
                {
                    StreamingText += chunk;
                }

                // JSON 파싱 시도
                var parsedPlan = _architectService.ParsePlan(StreamingText);
                if (parsedPlan != null)
                {
                    parsedPlan.Concept = userMsg; // 컨셉 보존
                    Plan = parsedPlan;
                    _session.Plan = parsedPlan;

                    // 계획 요약 카드를 메시지로 추가
                    Messages.Add(new ArchitectMessage
                    {
                        Role = "assistant",
                        Text = GetPlanSummaryMarkdown(parsedPlan),
                        RelatedStep = AgentStep.PlanReview,
                        HasActionButtons = true,
                        Timestamp = DateTime.Now
                    });

                    CurrentStep = AgentStep.PlanReview;
                }
                else
                {
                    // 파싱 실패 시 원본 응답 텍스트 출력 후 재시도 유도
                    Messages.Add(new ArchitectMessage
                    {
                        Role = "assistant",
                        Text = $"계획 수립 형식 파싱에 실패했습니다. AI 응답:\n\n{StreamingText}\n\n다른 컨셉이나 명령을 다시 입력해 주세요.",
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
                StreamingText = "";

                string previousContent = GetCurrentStepContent(originalStep);

                var stream = _architectService.ReviseContentAsync(originalStep, previousContent, userMsg, _session.Plan!, _cts.Token);
                await foreach (var chunk in stream)
                {
                    StreamingText += chunk;
                }

                // 수정 내용 파싱 및 업데이트
                bool success = ProcessStepContent(originalStep, StreamingText);
                if (success)
                {
                    // 기존 동일 단계에 속하는 메시지의 ActionButtons 비활성화 처리
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
                        Text = $"수정 사항을 파싱하거나 저장하는 데 실패했습니다. AI 응답:\n\n{StreamingText}",
                        Timestamp = DateTime.Now
                    });
                }

                CurrentStep = originalStep;
            }
            else
            {
                // 자유 대화 (그 외 예외 상황)
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
            // 중단 시 상태 복구
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
            // 이전 상태 복구
            if (CurrentStep == AgentStep.Planning) CurrentStep = AgentStep.Idle;
            else if (IsGenStep(CurrentStep)) CurrentStep = GetReviewStepFromGen(CurrentStep);
        }
        finally
        {
            StreamingText = "";
            IsBusy = false;
            _cts = null;
        }
    }

    private bool CanSend() => !IsBusy;

    /// <summary>
    /// 현재 검토 중인 단계의 생성물을 확정하고 다음 단계 생성을 트리거합니다.
    /// </summary>
    [RelayCommand]
    public async Task ApproveAndProceedAsync()
    {
        if (IsBusy) return;

        AgentStep nextGenStep = GetNextGenStep(CurrentStep);
        if (nextGenStep == AgentStep.Complete)
        {
            // 모든 검토 완료 -> 최종 세션 파일 적용
            await ApplySessionAsync();
            return;
        }

        // ActionButtons 비활성화
        DisablePreviousActionButtons(CurrentStep);

        IsBusy = true;
        CurrentStep = nextGenStep;
        StreamingText = "";
        _cts = new CancellationTokenSource();

        try
        {
            // 다음 단계 생성 시작
            var stream = _architectService.GenerateStepContentAsync(nextGenStep, _session.Plan!, _session, null, _cts.Token);
            await foreach (var chunk in stream)
            {
                StreamingText += chunk;
            }

            AgentStep reviewStep = GetReviewStepFromGen(nextGenStep);
            bool success = ProcessStepContent(reviewStep, StreamingText);
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
                    Text = $"컨텐츠 생성 파싱에 실패했습니다. AI 응답:\n\n{StreamingText}\n\n요구 사항을 다시 입력하거나 새로 시도해 주세요.",
                    Timestamp = DateTime.Now
                });
                // 이전 검토 단계로 롤백
                CurrentStep = GetReviewStepFromGen(nextGenStep) - 2; // (Gen / Review는 2단계 차이가 나므로 역산)
            }
        }
        catch (OperationCanceledException)
        {
            // 중단 시 이전 검토 단계로 롤백
            CurrentStep = GetReviewStepFromGen(nextGenStep) - 2;
        }
        catch (Exception ex)
        {
            Messages.Add(new ArchitectMessage
            {
                Role = "system",
                Text = $"에러 발생: {ex.Message}",
                Timestamp = DateTime.Now
            });
            // 원래 상태로 복원
            CurrentStep = GetReviewStepFromGen(nextGenStep) - 2;
        }
        finally
        {
            StreamingText = "";
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
                    Text = "🎉 기존 세션에 성공적으로 변경 사항이 적용되었습니다! 잠시 후 세션이 리로드됩니다.",
                    Timestamp = DateTime.Now
                });
            }
            else
            {
                fileName = await _architectService.ApplyToNewSessionAsync(_session);
                Messages.Add(new ArchitectMessage
                {
                    Role = "assistant",
                    Text = "🎉 새로운 세션이 정상적으로 생성되었습니다! 플레이 리스트에서 확인하실 수 있습니다.",
                    Timestamp = DateTime.Now
                });
            }

            CurrentStep = AgentStep.Complete;
            
            // 2초 정도 메시지를 보여준 뒤 화면을 닫고 세션을 전환하게끔 대기
            await Task.Delay(2000);
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
            CurrentStep = AgentStep.PromptReview; // 직전 단계로 복귀
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
               step == AgentStep.StatusReview ||
               step == AgentStep.ScenarioReview ||
               step == AgentStep.PromptReview;
    }

    private bool IsGenStep(AgentStep step)
    {
        return step == AgentStep.Planning ||
               step == AgentStep.WorldviewGen ||
               step == AgentStep.LorebookGen ||
               step == AgentStep.StatusGen ||
               step == AgentStep.ScenarioGen ||
               step == AgentStep.PromptGen;
    }

    private AgentStep GetGenStepFromReview(AgentStep reviewStep) => reviewStep switch
    {
        AgentStep.PlanReview => AgentStep.Planning,
        AgentStep.WorldviewReview => AgentStep.WorldviewGen,
        AgentStep.LorebookReview => AgentStep.LorebookGen,
        AgentStep.StatusReview => AgentStep.StatusGen,
        AgentStep.ScenarioReview => AgentStep.ScenarioGen,
        AgentStep.PromptReview => AgentStep.PromptGen,
        _ => AgentStep.Idle
    };

    private AgentStep GetReviewStepFromGen(AgentStep genStep) => genStep switch
    {
        AgentStep.Planning => AgentStep.PlanReview,
        AgentStep.WorldviewGen => AgentStep.WorldviewReview,
        AgentStep.LorebookGen => AgentStep.LorebookReview,
        AgentStep.StatusGen => AgentStep.StatusReview,
        AgentStep.ScenarioGen => AgentStep.ScenarioReview,
        AgentStep.PromptGen => AgentStep.PromptReview,
        _ => AgentStep.Idle
    };

    private AgentStep GetNextGenStep(AgentStep currentReviewStep) => currentReviewStep switch
    {
        AgentStep.PlanReview => AgentStep.WorldviewGen,
        AgentStep.WorldviewReview => AgentStep.LorebookGen,
        AgentStep.LorebookReview => AgentStep.StatusGen,
        AgentStep.StatusReview => AgentStep.ScenarioGen,
        AgentStep.ScenarioReview => AgentStep.PromptGen,
        AgentStep.PromptReview => AgentStep.Complete, // 끝
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
        AgentStep.StatusReview => JsonSerializer.Serialize(new { stats = _session.GeneratedStats, guide = _session.GeneratedStatusGuide }),
        AgentStep.ScenarioReview => _session.GeneratedScenario ?? "",
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
                    return true;
                }
                return false;

            case AgentStep.WorldviewReview:
                _session.GeneratedWorldview = rawResponse.Trim();
                return !string.IsNullOrWhiteSpace(_session.GeneratedWorldview);

            case AgentStep.LorebookReview:
                var lorebooks = _architectService.ParseLorebooks(rawResponse);
                if (lorebooks != null)
                {
                    _session.GeneratedLorebooks = lorebooks;
                    return true;
                }
                return false;

            case AgentStep.StatusReview:
                var statusDesign = _architectService.ParseStatusDesign(rawResponse);
                if (statusDesign != null)
                {
                    _session.GeneratedStats = statusDesign.Value.Stats;
                    _session.GeneratedStatusGuide = statusDesign.Value.Guide;
                    return true;
                }
                return false;

            case AgentStep.ScenarioReview:
                _session.GeneratedScenario = rawResponse.Trim();
                return !string.IsNullOrWhiteSpace(_session.GeneratedScenario);

            case AgentStep.PromptReview:
                _session.GeneratedSystemPrompt = rawResponse.Trim();
                return !string.IsNullOrWhiteSpace(_session.GeneratedSystemPrompt);

            default:
                return false;
        }
    }

    /// <summary>
    /// UI에 렌더링할 단계별 마크다운/텍스트를 구성합니다.
    /// </summary>
    private string GetStepDisplayContent(AgentStep reviewStep) => reviewStep switch
    {
        AgentStep.PlanReview => GetPlanSummaryMarkdown(_session.Plan!),
        AgentStep.WorldviewReview => $"### 🌍 생성된 세계관 설정\n\n{_session.GeneratedWorldview}",
        AgentStep.LorebookReview => GetLorebookSummaryMarkdown(_session.GeneratedLorebooks!),
        AgentStep.StatusReview => GetStatusSummaryMarkdown(_session.GeneratedStats!, _session.GeneratedStatusGuide!),
        AgentStep.ScenarioReview => $"### 📖 초기 시나리오 오프닝\n\n{_session.GeneratedScenario}",
        AgentStep.PromptReview => $"### ⚙️ 시스템 지시문 (System Instruction)\n\n```xml\n{_session.GeneratedSystemPrompt}\n```",
        _ => ""
    };

    private string GetPlanSummaryMarkdown(AgentPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### 📋 AI 설계 계획안 수립 완료");
        sb.AppendLine($"**테마/개요:** {plan.WorldviewOutline}\n");
        sb.AppendLine("#### 👥 생성 예정 로어북 항목:");
        foreach (var item in plan.LorebookPlan)
        {
            sb.AppendLine($"- **{item.Name}** ({item.Category}): {item.Brief}");
        }
        sb.AppendLine("\n#### 📊 설계 예정 스탯창 구성:");
        sb.AppendLine(string.Join(", ", plan.StatsPlan.Select(s => $"`{s}`")));
        sb.AppendLine($"\n#### 📖 시나리오 방향성: {plan.ScenarioOutline}");
        sb.AppendLine($"#### ⚙️ 시스템 프롬프트 구성: {plan.PromptOutline}");
        return sb.ToString();
    }

    private string GetLorebookSummaryMarkdown(List<LorebookEntry> lorebooks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### 👥 생성된 로어북 데이터 세트");
        sb.AppendLine($"총 **{lorebooks.Count}**개의 백과사전 항목이 생성되었습니다.\n");
        foreach (var item in lorebooks)
        {
            sb.AppendLine($"<details><summary><b>{item.Name}</b> ({item.Category}) - 키워드: {string.Join(", ", item.Keywords)}</summary>");
            sb.AppendLine($"\n{item.Content}\n</details>");
        }
        return sb.ToString();
    }

    private string GetStatusSummaryMarkdown(Dictionary<string, string> stats, string guide)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### 📊 상태창 스탯 및 변동 규칙 설계");
        sb.AppendLine("#### [초기 스탯]");
        foreach (var stat in stats)
        {
            sb.AppendLine($"- **{stat.Key}**: `{stat.Value}`");
        }
        sb.AppendLine("\n#### [스탯 업데이트 가이드라인]");
        sb.AppendLine(guide);
        return sb.ToString();
    }

    private void DisablePreviousActionButtons(AgentStep step)
    {
        foreach (var msg in Messages.Where(m => m.RelatedStep == step))
        {
            msg.HasActionButtons = false;
        }
        // WPF UI 바인딩 갱신용 (ObservableCollection 내부 값 변경 시 알림)
        // 리스트를 리셋하거나 강제 PropertyChanged가 아니라서 DataTemplate 내 바인딩이 작동하게
        // 객체 참조를 교체해 주는 것이 깔끔합니다.
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

    #endregion
}
