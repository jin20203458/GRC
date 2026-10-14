using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GRC.Models;
using GRC.Services;
using GRC.ViewModels;
using Xunit;

namespace GRC.Tests;

public class SessionListViewModelTests
{
    private class FakeSessionService : ISessionService
    {
        public List<string> DeletedFiles { get; } = new();
        public List<string> ExistingFiles { get; set; } = new();

        public Task SaveSessionAsync(string fileName, ChatSession session) => Task.CompletedTask;

        public Task<ChatSession?> LoadSessionAsync(string fileName)
        {
            var preset = new CharacterPreset("Hero", "Fantasy World", "Guide");
            var session = new ChatSession(preset, new List<ChatMessage>(), new ChapterContext(), new List<ChapterContext>(), "");
            return Task.FromResult<ChatSession?>(session);
        }

        public Task<IEnumerable<string>> GetSessionFilesAsync() => Task.FromResult<IEnumerable<string>>(ExistingFiles);

        public Task DeleteSessionAsync(string fileName)
        {
            DeletedFiles.Add(fileName);
            ExistingFiles.Remove(fileName);
            return Task.CompletedTask;
        }
    }

    private class FakeDialogService : IDialogService
    {
        public NewSessionSetupResult? NewSessionResultToReturn { get; set; }
        public bool ConfirmResultToReturn { get; set; } = true;
        public string? ArchitectCreatedFileName { get; set; }

        public NewSessionSetupResult? ShowNewSessionSetupDialog() => NewSessionResultToReturn;

        public (bool IsSaved, string Worldview, string CustomStats, string? InitialScenario, string StatusUpdateGuide)? ShowEditWorldviewDialog(
            string currentWorldview, Dictionary<string, string>? currentStats, string currentScenario, string currentStatusUpdateGuide) => null;

        public List<LorebookEntry>? ShowEditLorebookDialog(List<LorebookEntry>? currentLorebooks) => null;

        public void ShowSessionArchitectWindow(Action<string>? onSessionCreated = null)
        {
            if (ArchitectCreatedFileName != null)
            {
                onSessionCreated?.Invoke(ArchitectCreatedFileName);
            }
        }

        public void ShowSessionArchitectEditWindow(string fileName, CharacterPreset preset, Func<string, Task>? onSessionUpdated = null) { }

        public void ShowStatusWindow(object viewModel) { }
        public void ShowStoryHistoryWindow(object viewModel) { }

        public bool ShowConfirm(string message, string title) => ConfirmResultToReturn;
        public void ShowAlert(string message, string title) { }
        public void RunOnUIThread(Action action) => action();
        public void CloseAuxiliaryWindows() { }
    }

    [Fact]
    public void CreateNewSession_WhenDialogCancelled_DoesNotInvokeSessionSelected()
    {
        var fakeSession = new FakeSessionService();
        var fakeDialog = new FakeDialogService { NewSessionResultToReturn = null };
        var vm = new SessionListViewModel(fakeSession, fakeDialog);

        bool invoked = false;
        vm.SessionSelected += (f, n, w, s, c, u) => invoked = true;

        vm.CreateNewSessionCommand.Execute(null);

        Assert.False(invoked);
    }

    [Fact]
    public void CreateNewSession_WhenDialogConfirmed_InvokesSessionSelectedWithCorrectParameters()
    {
        var fakeSession = new FakeSessionService();
        var fakeDialog = new FakeDialogService
        {
            NewSessionResultToReturn = new NewSessionSetupResult(
                "세션1", "사이버펑크 세계관", "뒷골목에서 시작", "체력: 100/100", "상태창 갱신 가이드"
            )
        };
        var vm = new SessionListViewModel(fakeSession, fakeDialog);

        string? receivedFile = "initial";
        string? receivedName = null;
        string? receivedWorldview = null;
        string? receivedScenario = null;
        string? receivedStats = null;
        string? receivedGuide = null;

        vm.SessionSelected += (file, name, worldview, scenario, stats, guide) =>
        {
            receivedFile = file;
            receivedName = name;
            receivedWorldview = worldview;
            receivedScenario = scenario;
            receivedStats = stats;
            receivedGuide = guide;
        };

        vm.CreateNewSessionCommand.Execute(null);

        Assert.Null(receivedFile);
        Assert.Equal("세션1", receivedName);
        Assert.Equal("사이버펑크 세계관", receivedWorldview);
        Assert.Equal("뒷골목에서 시작", receivedScenario);
        Assert.Equal("체력: 100/100", receivedStats);
        Assert.Equal("상태창 갱신 가이드", receivedGuide);
    }

    [Fact]
    public async Task DeleteSessionAsync_WhenConfirmed_DeletesSessionAndRemovesFromCollection()
    {
        var fakeSession = new FakeSessionService();
        var fakeDialog = new FakeDialogService { ConfirmResultToReturn = true };
        var vm = new SessionListViewModel(fakeSession, fakeDialog);

        var summary = new SessionSummary("session1.json", "Hero", "마지막 대사", DateTime.Now);
        vm.Sessions.Add(summary);

        await vm.DeleteSessionCommand.ExecuteAsync(summary);

        Assert.Contains("session1.json", fakeSession.DeletedFiles);
        Assert.DoesNotContain(summary, vm.Sessions);
        Assert.True(vm.HasNoSessions);
    }

    [Fact]
    public async Task DeleteSessionAsync_WhenCancelled_PreservesSessionInCollection()
    {
        var fakeSession = new FakeSessionService();
        var fakeDialog = new FakeDialogService { ConfirmResultToReturn = false };
        var vm = new SessionListViewModel(fakeSession, fakeDialog);

        var summary = new SessionSummary("session1.json", "Hero", "마지막 대사", DateTime.Now);
        vm.Sessions.Add(summary);

        await vm.DeleteSessionCommand.ExecuteAsync(summary);

        Assert.Empty(fakeSession.DeletedFiles);
        Assert.Contains(summary, vm.Sessions);
        Assert.False(vm.HasNoSessions);
    }

    [Fact]
    public void OpenArchitect_WhenSessionCreated_InvokesSessionSelected()
    {
        var fakeSession = new FakeSessionService();
        var fakeDialog = new FakeDialogService { ArchitectCreatedFileName = "new_architect_session.json" };
        var vm = new SessionListViewModel(fakeSession, fakeDialog);

        string? openedFile = null;
        vm.SessionSelected += (file, n, w, s, c, u) => openedFile = file;

        vm.OpenArchitectCommand.Execute(null);

        Assert.Equal("new_architect_session.json", openedFile);
    }
}
