using System.Windows;
using System.Windows.Input;
using GRC.ViewModels;

namespace GRC.Views;

public partial class StoryHistoryWindow : Window
{
    public StoryHistoryWindow(ChatViewModel viewModel)
    {
        InitializeComponent();

        DataContext = viewModel;

        if (Application.Current.MainWindow != this)
        {
            this.Owner = Application.Current.MainWindow;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            this.DragMove();
        }
    }
}