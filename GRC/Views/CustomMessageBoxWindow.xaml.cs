using System.Windows;

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

            // 소유자 창을 설정하여 중앙에 뜨도록 함
            if (Application.Current.MainWindow != this)
                this.Owner = Application.Current.MainWindow;

            // 경고(Alert) 모드라면 취소 버튼 숨기기
            if (!isConfirmMode)
                BtnCancel.Visibility = Visibility.Collapsed;
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
    }
}