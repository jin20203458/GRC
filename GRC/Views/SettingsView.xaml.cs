using System.Windows;
using System.Windows.Controls;

namespace GRC.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        // 화면이 로드될 때 실행되는 이벤트
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // DataContext가 SettingsViewModel인지 확인하고, 맞다면 기존 설정값을 불러옵니다.
            if (DataContext is ViewModels.SettingsViewModel vm)
            {
                vm.LoadSettingsCommand.Execute(null);
            }
        }
    }
}