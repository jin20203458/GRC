using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace GRC.Views
{
    public partial class ChatView : UserControl
    {
        public ChatView()
        {
            InitializeComponent();
        }

        // 대사 버튼 클릭
        private void BtnInsertDialogue_Click(object sender, RoutedEventArgs e)
        {
            WrapText("\"", "\"");
        }

        // 속마음 버튼 클릭
        private void BtnInsertThought_Click(object sender, RoutedEventArgs e)
        {
            WrapText("「", "」");
        }

        // 기호 래핑 공통 로직
        private void WrapText(string prefix, string suffix)
        {
            if (InputTextBox.SelectionLength > 0)
            {
                // 1. 드래그한 영역이 있을 경우: 선택한 텍스트 양옆에 기호 감싸기
                string selectedText = InputTextBox.SelectedText;
                InputTextBox.SelectedText = $"{prefix}{selectedText}{suffix}";

                // 커서를 감싸진 텍스트 맨 끝으로 이동시켜 다음 입력 준비
                InputTextBox.SelectionStart += InputTextBox.SelectionLength;
                InputTextBox.SelectionLength = 0;
            }
            else
            {
                // 2. 드래그 없이 그냥 버튼을 누른 경우: 현재 위치에 빈 기호만 생성
                int caretIndex = InputTextBox.SelectionStart;

                // 현재 커서 위치에 빈 기호 문자열 삽입
                InputTextBox.Text = InputTextBox.Text.Insert(caretIndex, $"{prefix}{suffix}");

                // 사용자가 바로 타이핑할 수 있도록 커서를 기호 '가운데'로 이동
                InputTextBox.SelectionStart = caretIndex + prefix.Length;
            }

            // 버튼 클릭 후 다시 입력창으로 포커스 복귀
            InputTextBox.Focus();
        }

        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                ResetAutoScroll();
            }
            // F1 키를 눌렀을 때 (대사)
            if (e.Key == Key.F1)
            {
                BtnInsertDialogue_Click(sender, new RoutedEventArgs());
                e.Handled = true; // F1의 기본 윈도우 기능(도움말 팝업 등)을 막아줌
            }
            // F2 키를 눌렀을 때 (속마음)
            else if (e.Key == Key.F2)
            {
                BtnInsertThought_Click(sender, new RoutedEventArgs());
                e.Handled = true; // F2의 기본 윈도우 기능을 막아줌
            }
        }

        // 💡 [새로 추가] 전송(종이비행기) 버튼을 클릭했을 때 오토스크롤 강제 복구
        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ResetAutoScroll(); // 2. 레이아웃 바운스가 끝난 직후에 스크롤을 다시 맨 아래로 꽂고 ON 상태로 만듦
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // 💡 [새로 추가] 스크롤을 맨 아래로 강제 이동하고 오토스크롤 잠금을 해제하는 핵심 메서드
        private void ResetAutoScroll()
        {
            if (VisualTreeHelper.GetChildrenCount(ChatHistoryItemsControl) > 0)
            {
                // ItemsControl 내부에 있는 ScrollViewer를 찾아서 강제 제어합니다.
                if (VisualTreeHelper.GetChild(ChatHistoryItemsControl, 0) is ScrollViewer sv)
                {
                    sv.Tag = true;        // 1. 오토스크롤 스위치를 강제로 On
                    sv.ScrollToEnd();     // 2. 즉시 화면을 맨 밑으로 꽂아버림
                }
            }
        }

    }
}
