using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GRC.Services;
using GRC.ViewModels;

namespace GRC;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // 전역에서 DI 컨테이너에 접근할 수 있도록 프로퍼티 개방
    // (MainWindow.xaml.cs 등에서 App.Current.Services 로 접근할 때 사용)
    public new static App Current => (App)Application.Current;

    // 앱 전체의 서비스(의존성)를 관리하는 프로퍼티
    public IServiceProvider Services { get; }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 앱이 생성될 때 서비스들을 등록하고 빌드함
        Services = ConfigureServices();
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = Services.GetRequiredService<GRC.Views.MainWindow>();
        mainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatalException("Dispatcher", e.Exception);
        MessageBox.Show($"예기치 않은 UI 오류가 발생했습니다:\n{e.Exception.Message}", "시스템 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // 프로세스 강제 크래시 방지
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogFatalException("AppDomain", ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogFatalException("TaskScheduler", e.Exception);
        e.SetObserved(); // 백그라운드 태스크 예외로 인한 프로세스 다운 방지
    }

    private static void LogFatalException(string source, Exception ex)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDir = Path.Combine(appData, "GRC", "Logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "crash.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:O}] [{source}] {ex}\n\n");
        }
        catch { }
    }
    /// <summary>
    /// 애플리케이션에서 사용할 모든 서비스와 뷰모델을 등록하는 메서드
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        string keyPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "google-credentials.json");
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", keyPath);

        var services = new ServiceCollection();

        // ==================================================
        // 1. Services 계층 등록 (비즈니스 로직 및 외부 통신)
        // ==================================================

        // [핵심] HttpClientFactory를 사용하여 소켓 고갈 방지
        // 제미나이 API 통신 전담 서비스 (앱 수명주기 동안 안전하게 재사용됨)
        services.AddHttpClient<IGeminiApiService, GeminiApiService>(client =>
        {
            // Pro 모델의 긴 생성 시간을 고려하여 타임아웃 제한 완화
            client.Timeout = TimeSpan.FromMinutes(3);
        });

        // 대화 세션의 저장 및 복구를 담당하는 영속성 서비스
        services.AddSingleton<ISessionService, SessionService>();

        // 기억력 관리 및 컨텍스트 제어 서비스
        // 대화 내역(상태)을 계속 유지해야 하므로 Singleton으로 등록 (앱 내 1개만 존재)
        services.AddSingleton<IMemoryManagerService, MemoryManagerService>();
        services.AddSingleton<IReplySuggestionService, ReplySuggestionService>();
        // 프리셋 파일 저장/로드 서비스
        services.AddSingleton<IPresetStorageService, PresetStorageService>();

        // 앱 전체의 설정값(API 키, 모델 정보, 안전 필터 강도 등)을 관리하는 서비스
        services.AddSingleton<IAppSettingsService, AppSettingsService>();

        services.AddSingleton<IGoogleAuthService, GoogleAuthService>();
        services.AddSingleton<IAudioService, AudioService>();
        services.AddSingleton<ILorebookService, LorebookService>();
        services.AddSingleton<IChatWorkflowService, ChatWorkflowService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ISessionArchitectService, SessionArchitectService>();
        services.AddHttpClient<IGoogleTtsService, GeminiTtsService>();
        // ==================================================
        // 2. ViewModels 계층 등록 (UI와 로직의 연결고리)
        // ==================================================

        // 채팅창 뷰모델 등록
        // Transient: 화면이 전환되거나 새로 띄워질 때마다 새 객체를 생성함
        // (만약 탭을 여러 개 띄우지 않고 단일 창만 쓴다면 AddSingleton으로 변경해도 무방함)
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SessionListViewModel>();
        services.AddTransient<ChatViewModel>();
        services.AddTransient<SessionArchitectViewModel>();
        services.AddTransient<SettingsViewModel>();

        // 뷰 계층 등록
        services.AddSingleton<GRC.Views.MainWindow>();
        // 뷰모델과 서비스 조립 완료 후 반환
        return services.BuildServiceProvider();
    }
}