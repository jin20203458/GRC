using GRC.Models;
using GRC.ViewModels;
using GRC.Views;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace GRC.Services;

public record NewSessionSetupResult(
    string Name,
    string Worldview,
    string Scenario,
    string CustomStats,
    string StatusUpdateGuide
);

public interface IDialogService
{
    NewSessionSetupResult? ShowNewSessionSetupDialog();
    (bool IsSaved, string Worldview, string CustomStats, string? InitialScenario, string StatusUpdateGuide)? ShowEditWorldviewDialog(
        string currentWorldview, Dictionary<string, string>? currentStats, string currentScenario, string currentStatusUpdateGuide);
    List<LorebookEntry>? ShowEditLorebookDialog(List<LorebookEntry>? currentLorebooks);
    void ShowSessionArchitectWindow(Action<string>? onSessionCreated = null);
    void ShowSessionArchitectEditWindow(string fileName, CharacterPreset preset, Func<string, Task>? onSessionUpdated = null);
    void ShowStatusWindow(object viewModel);
    void ShowStoryHistoryWindow(object viewModel);
    bool ShowConfirm(string message, string title);
    void ShowAlert(string message, string title);
    void RunOnUIThread(Action action);
    void CloseAuxiliaryWindows();
}

public class DialogService(IServiceProvider serviceProvider) : IDialogService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private static void SetOwner(Window window)
    {
        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow != null && mainWindow != window && mainWindow.IsVisible)
        {
            window.Owner = mainWindow;
        }
    }

    public void RunOnUIThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess() || !dispatcher.Thread.IsAlive || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            action();
        }
        else
        {
            try
            {
                var op = dispatcher.InvokeAsync(action);
                if (!op.Task.Wait(TimeSpan.FromMilliseconds(500)))
                {
                    action();
                }
            }
            catch (Exception)
            {
                action();
            }
        }
    }

    public NewSessionSetupResult? ShowNewSessionSetupDialog()
    {
        NewSessionSetupResult? result = null;
        RunOnUIThread(() =>
        {
            var dialog = new NewSessionSetupWindow();
            SetOwner(dialog);
            if (dialog.ShowDialog() == true)
            {
                result = new NewSessionSetupResult(
                    dialog.InputName,
                    dialog.InputWorldview,
                    dialog.InputScenario,
                    dialog.InputCustomStats,
                    dialog.InputStatusUpdateGuide
                );
            }
        });
        return result;
    }

    public (bool IsSaved, string Worldview, string CustomStats, string? InitialScenario, string StatusUpdateGuide)? ShowEditWorldviewDialog(
        string currentWorldview, Dictionary<string, string>? currentStats, string currentScenario, string currentStatusUpdateGuide)
    {
        (bool IsSaved, string Worldview, string CustomStats, string? InitialScenario, string StatusUpdateGuide)? result = null;
        RunOnUIThread(() =>
        {
            var dialog = new EditWorldviewWindow(currentWorldview, currentStats, currentScenario, currentStatusUpdateGuide);
            SetOwner(dialog);
            if (dialog.ShowDialog() == true)
            {
                result = (true, dialog.InputWorldview, dialog.InputCustomStats, dialog.ChangedInitialScenario, dialog.InputStatusUpdateGuide);
            }
        });
        return result;
    }

    public List<LorebookEntry>? ShowEditLorebookDialog(List<LorebookEntry>? currentLorebooks)
    {
        List<LorebookEntry>? result = null;
        RunOnUIThread(() =>
        {
            var dialog = new EditLorebookWindow(currentLorebooks);
            SetOwner(dialog);
            if (dialog.ShowDialog() == true)
            {
                result = dialog.FinalLorebooks;
            }
        });
        return result;
    }

    public void ShowSessionArchitectWindow(Action<string>? onSessionCreated = null)
    {
        RunOnUIThread(() =>
        {
            var vm = _serviceProvider.GetRequiredService<SessionArchitectViewModel>();
            var window = new SessionArchitectWindow(vm);
            SetOwner(window);
            vm.SessionCreated += (fileName) =>
            {
                window.Close();
                onSessionCreated?.Invoke(fileName);
            };
            window.ShowDialog();
        });
    }

    public void ShowSessionArchitectEditWindow(string fileName, CharacterPreset preset, Func<string, Task>? onSessionUpdated = null)
    {
        RunOnUIThread(() =>
        {
            var vm = _serviceProvider.GetRequiredService<SessionArchitectViewModel>();
            vm.InitializeForEdit(fileName, preset);
            var window = new SessionArchitectWindow(vm);
            SetOwner(window);
            vm.SessionCreated += async (updatedFileName) =>
            {
                window.Close();
                if (onSessionUpdated != null)
                {
                    await onSessionUpdated(updatedFileName);
                }
            };
            window.ShowDialog();
        });
    }

    public void ShowStatusWindow(object viewModel)
    {
        RunOnUIThread(() =>
        {
            var window = new StatusWindow((ChatViewModel)viewModel);
            SetOwner(window);
            window.Show();
        });
    }

    public void ShowStoryHistoryWindow(object viewModel)
    {
        RunOnUIThread(() =>
        {
            var window = new StoryHistoryWindow((ChatViewModel)viewModel);
            SetOwner(window);
            window.Show();
        });
    }

    public bool ShowConfirm(string message, string title)
    {
        bool result = false;
        RunOnUIThread(() =>
        {
            var customMsgBox = new CustomMessageBoxWindow(message, title, isConfirmMode: true);
            SetOwner(customMsgBox);
            result = customMsgBox.ShowDialog() == true;
        });
        return result;
    }

    public void ShowAlert(string message, string title)
    {
        RunOnUIThread(() =>
        {
            var customMsgBox = new CustomMessageBoxWindow(message, title, isConfirmMode: false);
            SetOwner(customMsgBox);
            customMsgBox.ShowDialog();
        });
    }

    public void CloseAuxiliaryWindows()
    {
        RunOnUIThread(() =>
        {
            try
            {
                var windowsToClose = Application.Current?.Windows.OfType<Window>()
                    .Where(w => w is StatusWindow || w is StoryHistoryWindow || w is SessionArchitectWindow)
                    .ToList();

                if (windowsToClose != null)
                {
                    foreach (var w in windowsToClose) w.Close();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DialogService] CloseAuxiliaryWindows exception ignored: {ex.Message}");
            }
        });
    }
}