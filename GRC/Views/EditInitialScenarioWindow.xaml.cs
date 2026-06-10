using System.Windows;

namespace GRC.Views
{
    public partial class EditInitialScenarioWindow : Window
    {
        public string InputScenario => ScenarioTextBox.Text;

        public EditInitialScenarioWindow(string currentScenario)
        {
            InitializeComponent();
            ScenarioTextBox.Text = currentScenario;
            if (Application.Current.MainWindow != this)
                this.Owner = Application.Current.MainWindow;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}