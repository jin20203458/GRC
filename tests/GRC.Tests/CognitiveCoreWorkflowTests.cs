using GRC.Models;
using GRC.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static GRC.Tests.MemoryManagerServiceTests;

namespace GRC.Tests;

public class CognitiveCoreWorkflowTests
{
    [Fact]
    public async Task BuildNarrativeRequestAsync_WithMetaDirective_CombinesDirectiveWithUserMessage()
    {
        // Arrange
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);
        var preset = new CharacterPreset("Hero", "Fantasy World", "You are a heroic guide.");
        var userMsg = new ChatMessage("user", "앞으로 나아간다.", DateTime.Now);
        string metaDirective = "<meta_directive>[시스템 개입: 챕터 전환]</meta_directive>";

        // Act
        var request = await service.BuildNarrativeRequestAsync(userMsg, preset, metaDirective);

        // Assert
        Assert.NotNull(request);
        Assert.NotNull(request.GenerationConfig);
        Assert.NotNull(request.SafetySettings);

        // 최신 유저 턴 메시지에 metaDirective 내용이 포함되어 프롬프트에 주입되었는지 검증
        var lastContent = request.Contents.LastOrDefault(c => c.Role == "user");
        Assert.NotNull(lastContent);
        string text = lastContent!.Parts[0].Text ?? "";
        Assert.Contains("앞으로 나아간다.", text);
        Assert.Contains("[시스템 개입: 챕터 전환]", text);
    }

    [Fact]
    public void UpdateContextStatus_WithUnresolvedClues_RegistersToClueTracker()
    {
        // Arrange
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var clueTracker = new ClueTracker();
        var service = new MemoryManagerService(
            fakeApi, fakeSettings,
            new CognitivePromptAssembler(),
            new EventLedgerExtractor(fakeApi),
            clueTracker,
            new HybridLorebookRetriever(),
            new GatedRAGRetriever()
        );

        var payload = new StatusPayload
        {
            UnresolvedClues = ["지하실 바닥의 피 묻은 열쇠"]
        };

        // Act
        service.UpdateContextStatus(payload);

        // Assert
        Assert.Single(clueTracker.ActiveClues);
        Assert.Equal("지하실 바닥의 피 묻은 열쇠", clueTracker.ActiveClues[0].Clue);
    }

    [Fact]
    public void ExportAndRestoreSession_PreservesCluesAndEventLedgers()
    {
        // Arrange
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var clueTracker = new ClueTracker();
        var service = new MemoryManagerService(
            fakeApi, fakeSettings,
            new CognitivePromptAssembler(),
            new EventLedgerExtractor(fakeApi),
            clueTracker,
            new HybridLorebookRetriever(),
            new GatedRAGRetriever()
        );
        var preset = new CharacterPreset("Hero", "Fantasy World", "You are a heroic guide.");

        // 상태 및 단서 등록
        service.UpdateContextStatus(new StatusPayload
        {
            UnresolvedClues = ["오래된 가문의 문장"]
        });
        service.CurrentContext.EventLedgers.Add(new EventLedgerItem(1, "원정대가 숲에 진입함"));

        // Act 1: 세션 Export
        var exportedSession = service.ExportSession(preset);

        Assert.NotNull(exportedSession.ActiveClues);
        Assert.Single(exportedSession.ActiveClues);
        Assert.Equal("오래된 가문의 문장", exportedSession.ActiveClues[0].Clue);

        // Act 2: 신규 서비스 인스턴스에 Restore
        var newClueTracker = new ClueTracker();
        var newService = new MemoryManagerService(
            fakeApi, fakeSettings,
            new CognitivePromptAssembler(),
            new EventLedgerExtractor(fakeApi),
            newClueTracker,
            new HybridLorebookRetriever(),
            new GatedRAGRetriever()
        );

        newService.RestoreSession(exportedSession);

        // Assert
        Assert.Single(newClueTracker.ActiveClues);
        Assert.Equal("오래된 가문의 문장", newClueTracker.ActiveClues[0].Clue);
        Assert.Single(newService.CurrentContext.EventLedgers);
        Assert.Equal("원정대가 숲에 진입함", newService.CurrentContext.EventLedgers[0].FactBullet);
    }

    [Fact]
    public async Task CompressToMediumTermAsync_ExtractsLedgerFacts_AndUpdatesPlotTimeline()
    {
        // Arrange
        var fakeApi = new FakeGeminiApiService
        {
            MockResponse = "{\"bullets\": [\"원정대가 북쪽 관문에 도착했다.\", \"기사단장이 밀서를 전했다.\"]}"
        };
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings)
        {
            CompressionDelayMs = 10
        };
        var preset = new CharacterPreset("Hero", "Fantasy World", "You are a heroic guide.");

        // 18턴 누적 -> 중기 압축 트리거
        for (int i = 1; i <= 18; i++)
        {
            await service.BuildNarrativeRequestAsync(new ChatMessage("user", $"Turn action {i}", DateTime.Now), preset);
        }

        // 압축 완료 대기 (8턴 절삭되어 10턴 잔여)
        bool compressed = false;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(100);
            if (service.ExportSession(preset).History.Count == 10)
            {
                compressed = true;
                break;
            }
        }

        Assert.True(compressed, "18턴 도달 후 8턴이 절삭되고 10턴이 남아야 합니다.");
        var session = service.ExportSession(preset);

        // EventLedgers에 2개의 불릿이 등록되었는지 검증
        Assert.Equal(2, session.CurrentContext.EventLedgers.Count);
        Assert.Equal("원정대가 북쪽 관문에 도착했다.", session.CurrentContext.EventLedgers[0].FactBullet);
        Assert.Equal("기사단장이 밀서를 전했다.", session.CurrentContext.EventLedgers[1].FactBullet);

        // Plot 속성에 타임라인 문자열로 포맷팅되었는지 검증
        Assert.Contains("원정대가 북쪽 관문에 도착했다.", session.CurrentContext.Plot);
        Assert.Contains("[Turn", session.CurrentContext.Plot);
        Assert.StartsWith("[제 1장]", session.CurrentContext.Plot);
    }

    [Fact]
    public void RestoreSession_WithExistingHistory_DoesNotTriggerAnchor_AndEmptyHistory_TriggersAnchor()
    {
        // Arrange
        var fakeApi = new FakeGeminiApiService();
        var fakeSettings = new FakeAppSettingsService();
        var service = new MemoryManagerService(fakeApi, fakeSettings);
        var preset = new CharacterPreset("Hero", "Fantasy World", "You are a heroic guide.");

        // Case 1: 기존 대화 이력이 있는 세션 로드 -> 앵커 비활성화(0) 확인
        var existingSession = new ChatSession(
            preset,
            [new ChatMessage("user", "이전 대화", DateTime.Now)],
            new ChapterContext(),
            [],
            "줄거리",
            1, 1, 0
        );

        service.RestoreSession(existingSession);
        Assert.False(service.ConsumeAnchorFlag(), "기존 대화가 이어지는 세션 복원 시에는 앵커가 발동하지 않아야 합니다.");

        // Case 2: 대화 이력이 없는 빈 신규 세션 로드 -> 첫 톤 확립을 위해 앵커 활성화(1) 확인
        var emptySession = new ChatSession(
            preset,
            [],
            new ChapterContext(),
            [],
            "줄거리",
            1, 0, 0
        );

        service.RestoreSession(emptySession);
        Assert.True(service.ConsumeAnchorFlag(), "대화 이력이 없는 신규 세션 복원 시에는 앵커가 활성화되어야 합니다.");
    }
}
