using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GRC.Models;
using GRC.ViewModels;

namespace GRC.Views;

/// <summary>
/// SessionArchitectWindow.xaml에 대한 상호 작용 논리
/// </summary>
public partial class SessionArchitectWindow : Window
{
    private readonly SessionArchitectViewModel _viewModel;

    public SessionArchitectWindow(SessionArchitectViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // ViewModel 이벤트 바인딩
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.Messages.CollectionChanged += Messages_CollectionChanged;

        // 초기 바 설정
        UpdateProgressBar(_viewModel.CurrentStep);

        // 포커스를 입력창에 둠
        Loaded += (s, e) => InputTextBox.Focus();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionArchitectViewModel.CurrentStep))
        {
            // 백그라운드 스레드에서 변경되더라도 UI 스레드에서 동기화되도록 Dispatcher 사용
            Dispatcher.Invoke(() => UpdateProgressBar(_viewModel.CurrentStep));
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 메시지 추가 시 아래로 스크롤
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.Invoke(() => AutoScrollToBottom());
        }
    }

    private void AutoScrollToBottom()
    {
        if (ChatScrollViewer != null)
        {
            ChatScrollViewer.ScrollToEnd();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// CurrentStep에 따라 진행률 표시 바의 스타일 Tag를 변경하여
    /// XAML 상의 DataTrigger가 작동하게 만듭니다.
    /// </summary>
    private void UpdateProgressBar(AgentStep step)
    {
        // 각 단계의 인덱스를 계산 (1~6)
        int currentStepIndex = 0;

        switch (step)
        {
            case AgentStep.Idle:
                currentStepIndex = 0;
                break;
            case AgentStep.Planning:
            case AgentStep.PlanReview:
                currentStepIndex = 1;
                break;
            case AgentStep.WorldviewGen:
            case AgentStep.WorldviewReview:
                currentStepIndex = 2;
                break;
            case AgentStep.LorebookGen:
            case AgentStep.LorebookReview:
                currentStepIndex = 3;
                break;
            case AgentStep.StatusGen:
            case AgentStep.StatusReview:
                currentStepIndex = 4;
                break;
            case AgentStep.ScenarioGen:
            case AgentStep.ScenarioReview:
                currentStepIndex = 5;
                break;
            case AgentStep.PromptGen:
            case AgentStep.PromptReview:
            case AgentStep.Applying:
            case AgentStep.Complete:
                currentStepIndex = 6;
                break;
        }

        SetStepState(Step1Border, TextStep1, 1, currentStepIndex);
        SetStepState(Step2Border, TextStep2, 2, currentStepIndex);
        SetStepState(Step3Border, TextStep3, 3, currentStepIndex);
        SetStepState(Step4Border, TextStep4, 4, currentStepIndex);
        SetStepState(Step5Border, TextStep5, 5, currentStepIndex);
        SetStepState(Step6Border, TextStep6, 6, currentStepIndex);
    }

    private void SetStepState(Border border, TextBlock text, int stepNum, int currentStepIndex)
    {
        if (border == null || text == null) return;

        if (stepNum < currentStepIndex)
        {
            border.Tag = "Done";
            text.Tag = "Done";
        }
        else if (stepNum == currentStepIndex)
        {
            border.Tag = "Active";
            text.Tag = "Active";
        }
        else
        {
            border.Tag = "Pending";
            text.Tag = "Pending";
        }
    }

    private void AutoModeToggle_Click(object sender, RoutedEventArgs e)
    {
        // IsBusy 상태에서는 모드 전환 방지
        if (_viewModel.IsBusy) return;
        _viewModel.IsAutoMode = !_viewModel.IsAutoMode;
    }
}
