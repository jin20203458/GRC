using System.Windows;
using System.Windows.Controls;


namespace GRC.Views
{
    /// <summary>
    /// SessionListView.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class SessionListView : UserControl
    {
        public SessionListView()
        {
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SessionListViewModel vm)
                vm.LoadSessionsCommand.Execute(null);
        }

    }
}
