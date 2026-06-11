using GRC.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace GRC.Helpers;

public class StreamingState
{
    public bool IsThought { get; set; }
    public bool IsDialogue { get; set; }
    public char LastChar { get; set; } = '\0';
    public TextBlock? CurrentTextBlock { get; set; }
    public Run? CurrentRun { get; set; }
    public FrameworkElement? CurrentUIRoot { get; set; }
    public string PendingBuffer { get; set; } = "";
    public bool IsTagMode { get; set; } = false;

    // 💡 [최적화 핵심] 성능 개선을 위해 추가된 속성들
    // 1글자씩 넘어오는 문자를 임시로 담아둘 스레드 안전한 바구니(Queue)
    public ConcurrentQueue<char> CharQueue { get; } = new ConcurrentQueue<char>();
    // 모니터 주사율(16ms)에 맞춰 일괄 렌더링을 지시할 타이머
    public DispatcherTimer? RenderTimer { get; set; }
    // 메모리(GC) 압박을 줄이기 위해 한 프레임(Tick) 동안 덧붙일 텍스트를 모아두는 버퍼
    public StringBuilder CurrentRunBuffer { get; } = new StringBuilder();
}

public static class StatefulStreamingHelper
{
    private const string DialogueBorderStyleKey = "StreamingDialogueBorderStyle";
    private const string DialogueTextStyleKey = "StreamingDialogueTextStyle";
    private const string ThoughtTextStyleKey = "StreamingThoughtTextStyle";
    private const string NarrativeTextStyleKey = "StreamingNarrativeTextStyle";

    public static readonly DependencyProperty StreamingViewModelProperty =
        DependencyProperty.RegisterAttached(
            "StreamingViewModel",
            typeof(ChatViewModel),
            typeof(StatefulStreamingHelper),
            new PropertyMetadata(null, OnViewModelChanged));

    public static void SetStreamingViewModel(DependencyObject obj, ChatViewModel value) => obj.SetValue(StreamingViewModelProperty, value);
    public static ChatViewModel GetStreamingViewModel(DependencyObject obj) => (ChatViewModel)obj.GetValue(StreamingViewModelProperty);

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(StreamingState), typeof(StatefulStreamingHelper));

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StackPanel panel)
        {
            if (e.OldValue is ChatViewModel oldVm)
            {
                if (panel.GetValue(StateProperty) is StreamingState oldState)
                {
                    oldState.RenderTimer?.Stop();
                }
            }

            if (e.NewValue is ChatViewModel newVm)
            {
                var state = new StreamingState();
                panel.SetValue(StateProperty, state);
                panel.Children.Clear();

                //  [최적화 핵심] 16ms(약 60FPS) 주기로 작동하는 렌더링 전용 타이머 세팅
                state.RenderTimer = new DispatcherTimer(DispatcherPriority.Render, panel.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                state.RenderTimer.Tick += (s, args) => ProcessQueue(panel, state);

                string recoveredText = newVm.CurrentStreamingText;
                if (!string.IsNullOrEmpty(recoveredText))
                {
                    foreach (char c in recoveredText)
                    {
                        state.CharQueue.Enqueue(c);
                    }
                    ProcessQueue(panel, state);
                }

                Action<char> streamingHandler = (c) =>
                {
                    if (panel.Visibility != Visibility.Visible) return;

                    // 1글자가 수신되면 화면에 바로 그리지 않고 바구니(Queue)에 조용히 넣기만 함
                    state.CharQueue.Enqueue(c);

                    panel.Dispatcher.InvokeAsync(() =>

                    {

                        if (state.RenderTimer != null && !state.RenderTimer.IsEnabled)

                        {

                            state.RenderTimer.Start();

                        }

                    });
                };

                newVm.OnCharReceived += streamingHandler;

                panel.IsVisibleChanged += (sender, args) =>
                {
                    if ((bool)args.NewValue == false)
                    {
                        newVm.OnCharReceived -= streamingHandler;
                        state.RenderTimer?.Stop();
                    }
                    else
                    {
                        newVm.OnCharReceived -= streamingHandler;
                        newVm.OnCharReceived += streamingHandler;
                        if (!state.CharQueue.IsEmpty) state.RenderTimer?.Start();
                    }
                };

                panel.Unloaded += (sender, args) =>
                {
                    newVm.OnCharReceived -= streamingHandler;
                    state.RenderTimer?.Stop();
                };
            }
        }
    }

    /// <summary>
    /// 16ms마다 한 번씩 타이머에 의해 호출되어 바구니(Queue)의 모든 글자를 일괄 화면에 그립니다.
    /// </summary>
    private static void ProcessQueue(StackPanel panel, StreamingState state)
    {
        if (state.CharQueue.IsEmpty)
        {
            state.RenderTimer?.Stop();
            return;
        }

        // 큐에 쌓여있는 모든 문자를 꺼내서 문맥 판단 및 버퍼에 조립
        while (state.CharQueue.TryDequeue(out char c))
        {
            HandleCharReceived(panel, state, c);
        }

        // 한 프레임(16ms) 치의 조립이 모두 끝나면 화면(UI)에 딱 한 번만 반영
        FlushBuffer(state);
    }

    /// <summary>
    /// 조립된 StringBuilder의 내용을 실제 UI인 Run.Text에 밀어 넣고 버퍼를 비웁니다.
    /// </summary>
    private static void FlushBuffer(StreamingState state)
    {
        if (state.CurrentRun != null && state.CurrentRunBuffer.Length > 0)
        {
            state.CurrentRun.Text += state.CurrentRunBuffer.ToString();
            state.CurrentRunBuffer.Clear();
        }
    }

    private static void HandleCharReceived(StackPanel panel, StreamingState state, char c)
    {
        bool contextChanged = false;
        bool hasMeaningfulText = false;

        if (state.CurrentTextBlock != null)
        {
            foreach (var inline in state.CurrentTextBlock.Inlines)
            {
                if (inline is Run r)
                {
                    // 아직 화면에 안 그려진 버퍼 내용까지 포함해서 의미 있는 텍스트인지 검사
                    string fullText = r == state.CurrentRun
                        ? r.Text + state.CurrentRunBuffer.ToString()
                        : r.Text;

                    if (IsMeaningfulText(fullText))
                    {
                        hasMeaningfulText = true;
                        break;
                    }
                }
            }
        }

        // [방어막 1] 진짜 글자가 시작되기 전의 모든 공백 원천 차단
        if ((c == '\n' || c == '\r' || c == ' ') && !hasMeaningfulText) return;

        if (c == '「' || c == '」')
        {
            if (!state.IsThought)
            {
                if (state.CurrentRun != null)
                {
                    string combined = state.CurrentRun.Text + state.CurrentRunBuffer.ToString();
                    state.CurrentRun.Text = combined.TrimEnd('\r', '\n', ' ');
                    state.CurrentRunBuffer.Clear();
                }
                state.IsThought = true;
                contextChanged = true;
            }
            else
            {
                if (state.CurrentRun != null) state.CurrentRunBuffer.Append(c);
                state.PendingBuffer = "";
                state.IsThought = false;
                state.CurrentTextBlock = null;
                state.LastChar = c;
                return;
            }
        }
        else if (c == '"' || c == '“' || c == '”')
        {
            if (!state.IsDialogue)
            {
                if (state.CurrentRun != null)
                {
                    string combined = state.CurrentRun.Text + state.CurrentRunBuffer.ToString();
                    state.CurrentRun.Text = combined.TrimEnd('\r', '\n', ' ');
                    state.CurrentRunBuffer.Clear();
                }
                state.IsDialogue = true;
                contextChanged = true;
            }
            else
            {
                if (state.CurrentRun != null) state.CurrentRunBuffer.Append(c);
                state.PendingBuffer = "";
                state.IsDialogue = false;
                state.CurrentTextBlock = null;
                state.LastChar = c;
                return;
            }
        }

        state.LastChar = c;

        if (contextChanged || state.CurrentTextBlock == null)
        {
            // 문맥이 바뀔 때 이전 여백 소각
            if (contextChanged) state.PendingBuffer = "";

            // 새 블록을 만들기 전에 모아둔 기존 버퍼 잔여물을 확실히 화면에 밀어넣음
            FlushBuffer(state);

            if (state.CurrentTextBlock != null)
            {
                bool isBlockEmpty = true;
                foreach (var inline in state.CurrentTextBlock.Inlines)
                {
                    // 버퍼는 이미 위에서 Flush 되었으므로 r.Text만 검사해도 안전함
                    if (inline is Run r && IsMeaningfulText(r.Text))
                    {
                        isBlockEmpty = false;
                        break;
                    }
                }
                if (isBlockEmpty && panel.Children.Count > 0) panel.Children.RemoveAt(panel.Children.Count - 1);
            }

            AddNewBlockToPanel(panel, state);
        }

        if (state.CurrentRun == null)
        {
            state.CurrentRun = CreateRun(state);
            state.CurrentTextBlock!.Inlines.Add(state.CurrentRun);
        }

        if (c == '<') state.IsTagMode = true;

        if (c == '\n' || c == '\r' || state.IsTagMode || (state.PendingBuffer.Length > 0 && char.IsWhiteSpace(c)))
        {
            state.PendingBuffer += c;
        }
        else
        {
            if (state.PendingBuffer.Length > 0 && IsMeaningfulChar(c))
            {
                state.CurrentRunBuffer.Append(state.PendingBuffer);
                state.PendingBuffer = "";
            }
            // 기존 1글자마다 직접 UI(Run.Text)를 조작하던 것을 StringBuilder 버퍼에 조립하는 것으로 대체
            state.CurrentRunBuffer.Append(c);
        }

        if (c == '>') state.IsTagMode = false;

        if (state.CurrentUIRoot != null && state.CurrentUIRoot.Visibility == Visibility.Collapsed)
        {
            string combined = state.CurrentRun.Text + state.CurrentRunBuffer.ToString();
            if (IsMeaningfulText(combined))
            {
                state.CurrentUIRoot.Visibility = Visibility.Visible;
            }
        }
    }

    private static bool IsMeaningfulChar(char c)
    {
        if (char.IsWhiteSpace(c)) return false;
        if (c == '"' || c == '“' || c == '”' || c == '「' || c == '」') return false;
        return true;
    }

    private static bool IsMeaningfulText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (char c in text)
        {
            if (IsMeaningfulChar(c)) return true;
        }
        return false;
    }

    private static void AddNewBlockToPanel(StackPanel panel, StreamingState state)
    {
        var tb = new TextBlock();

        state.CurrentTextBlock = tb;
        state.CurrentRun = CreateRun(state);
        tb.Inlines.Add(state.CurrentRun);

        if (state.IsDialogue)
        {
            tb.Style = panel.TryFindResource(DialogueTextStyleKey) as Style;

            var border = new Border { Child = tb };
            border.Style = panel.TryFindResource(DialogueBorderStyleKey) as Style;

            panel.Children.Add(border);
            state.CurrentUIRoot = border;
        }
        else if (state.IsThought)
        {
            tb.Style = panel.TryFindResource(ThoughtTextStyleKey) as Style;

            panel.Children.Add(tb);
            state.CurrentUIRoot = tb;
        }
        else
        {
            tb.Style = panel.TryFindResource(NarrativeTextStyleKey) as Style;

            panel.Children.Add(tb);
            state.CurrentUIRoot = tb;
        }
    }

    private static Run CreateRun(StreamingState state)
    {
        return new Run();
    }
}