using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GRC.Helpers;

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
                ScrollChatToEnd();
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

        // 전송(종이비행기) 버튼을 클릭했을 때 오토스크롤 강제 복구
        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            ScrollChatToEnd();
        }

        // 스크롤을 맨 아래로 이동시키는 템플릿 탐색 헬퍼 메서드
        private void ScrollChatToEnd()
        {
            ChatHistoryItemsControl.ApplyTemplate();
            if (ChatHistoryItemsControl.Template?.FindName("ChatScrollViewer", ChatHistoryItemsControl) is ScrollViewer sv)
            {
                AutoScrollHelper.ScrollToEnd(sv);
            }
        }
    }
}
