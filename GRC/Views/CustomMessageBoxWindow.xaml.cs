using System.Windows;
using System.Windows.Input;

namespace GRC.Views
{
    public partial class CustomMessageBoxWindow : Window
    {
        public string DialogMessage { get; set; }
        public string DialogTitle { get; set; }

        public CustomMessageBoxWindow(string message, string title, bool isConfirmMode)
        {
            InitializeComponent();
            DialogMessage = message;
            DialogTitle = title;
            DataContext = this;

            // 경고(Alert) 모드라면 취소 버튼 숨기기
            if (!isConfirmMode)
            {
                BtnCancel.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                this.DialogResult = false;
                this.Close();
                e.Handled = true;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }
    }
}