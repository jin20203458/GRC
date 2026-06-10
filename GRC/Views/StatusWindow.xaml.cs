using System.Windows;
using System.Windows.Input;
using GRC.ViewModels;

namespace GRC.Views;

public partial class StatusWindow : Window
{
    // 생성자에서 ChatViewModel을 통째로 받아와 바인딩합니다.
    public StatusWindow(ChatViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 메인 창을 부모로 설정하여 중앙에 뜨게 하고 같이 최소화되도록 설정
        if (Application.Current.MainWindow != this)
        {
            this.Owner = Application.Current.MainWindow;
        }
    }
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 마우스 왼쪽 버튼이 눌린 상태에서 움직이면 창을 같이 이동시킴
        if (e.ChangedButton == MouseButton.Left)
        {
            this.DragMove();
        }
    }
}