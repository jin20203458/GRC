using GRC.ViewModels;
using GRC.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Specialized;
using System.Windows;

namespace GRC.Views; // 네임스페이스는 실제 환경에 맞게 조정

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // 1. DI 컨테이너에서 ViewModel 가져오기 (App.xaml.cs에 등록해둔 것)
        _viewModel = App.Current.Services.GetRequiredService<MainViewModel>();

        // 2. DataContext 바인딩
        DataContext = _viewModel;

    }

}