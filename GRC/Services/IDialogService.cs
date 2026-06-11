using GRC.Models;
using GRC.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace GRC.Services;

public interface IDialogService
{
    (bool IsSaved, string Worldview, string CustomStats, string? InitialScenario, string StatusUpdateGuide)? ShowEditWorldviewDialog(string currentWorldview, Dictionary<string, string>? currentStats, string currentScenario, string currentStatusUpdateGuide);
    List<LorebookEntry>? ShowEditLorebookDialog(List<LorebookEntry>? currentLorebooks);
    void ShowStatusWindow(object viewModel);
    void ShowStoryHistoryWindow(object viewModel);
    bool ShowConfirm(string message, string title);
    void ShowAlert(string message, string title);
    void RunOnUIThread(Action action);
    void CloseAuxiliaryWindows();
}

public class DialogService : IDialogService
{
    public (bool IsSaved, string Worldview, string CustomStats, string? InitialScenario, string StatusUpdateGuide)? ShowEditWorldviewDialog(string currentWorldview, Dictionary<string, string>? currentStats, string currentScenario, string currentStatusUpdateGuide)
    {
        var dialog = new EditWorldviewWindow(currentWorldview, currentStats, currentScenario, currentStatusUpdateGuide);
        if (dialog.ShowDialog() == true)
            return (true, dialog.InputWorldview, dialog.InputCustomStats, dialog.ChangedInitialScenario, dialog.InputStatusUpdateGuide);
        return null;
    }

    public List<LorebookEntry>? ShowEditLorebookDialog(List<LorebookEntry>? currentLorebooks)
    {
        var dialog = new EditLorebookWindow(currentLorebooks);
        if (dialog.ShowDialog() == true) return dialog.FinalLorebooks;
        return null;
    }

    public void ShowStatusWindow(object viewModel)
    {
        new StatusWindow((ViewModels.ChatViewModel)viewModel).Show();
    }

    public void ShowStoryHistoryWindow(object viewModel)
    {
        new StoryHistoryWindow((ViewModels.ChatViewModel)viewModel).Show();
    }

    public bool ShowConfirm(string message, string title)
    {
        bool result = false;
        RunOnUIThread(() =>
        {
            var customMsgBox = new CustomMessageBoxWindow(message, title, isConfirmMode: true);
            result = customMsgBox.ShowDialog() == true;
        });
        return result;
    }

    public void ShowAlert(string message, string title)
    {
        RunOnUIThread(() =>
        {
            var customMsgBox = new CustomMessageBoxWindow(message, title, isConfirmMode: false);
            customMsgBox.ShowDialog();
        });
    }

    public void RunOnUIThread(Action action)
    {
        Application.Current.Dispatcher.Invoke(action);
    }

    public void CloseAuxiliaryWindows()
    {
        RunOnUIThread(() =>
        {
            var windowsToClose = Application.Current.Windows.OfType<Window>()
                .Where(w => w is StatusWindow || w is StoryHistoryWindow)
                .ToList();

            foreach (var w in windowsToClose) w.Close();
        });
    }
}