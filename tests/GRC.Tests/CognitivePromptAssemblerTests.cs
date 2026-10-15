using GRC.Models;
using GRC.Services;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace GRC.Tests;

public class CognitivePromptAssemblerTests
{
    private static CharacterPreset CreateDummyPreset() => new(
        Name: "레이나",
        Worldview: "에르데 대륙은 마법과 증기 기관이 공존하는 세계입니다.",
        SystemPrompt: "당신은 냉철하지만 내면에 깊은 상처를 지닌 마법사 레이나입니다."
    );

    [Fact]
    public void AssemblePrompt_SystemInstruction_RemainsStaticWithoutDynamicContext()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        var context = new CognitiveContext(
            MacroChronicle: "1500자 대서사",
            LedgerFacts: [new(1, "사건 불릿 1")],
            Entities: [new("레이나", "70%", "신뢰", "안정", "원수")],
            ActiveClues: [new("c1", "붉은 쪽지", 1, 1)],
            RetrievedLores: [new("흑철검", ["검"], "고대 검", "아이템")],
            RecalledHistory: [new(5, "회상 팩트")]
        );

        // Act
        var request = assembler.AssemblePrompt(preset, [], new ChatMessage("user", "안녕", System.DateTime.Now), context);

        // Assert - SystemInstruction에는 페르소나와 세계관만 들어가고 동적 태그는 절대 포함되지 않아야 함
        string systemText = request.SystemInstruction?.Parts.FirstOrDefault()?.Text ?? string.Empty;
        Assert.Contains(preset.SystemPrompt, systemText);
        Assert.Contains("<master_setting>", systemText);
        Assert.Contains(preset.Worldview, systemText);
        Assert.DoesNotContain("<macro_chronicle>", systemText);
        Assert.DoesNotContain("<internal_chronicle_facts>", systemText);
        Assert.DoesNotContain("<entity_relationship>", systemText);
        Assert.DoesNotContain("<world_lore>", systemText);
        Assert.DoesNotContain("<recalled_history>", systemText);
        Assert.DoesNotContain("<latent_secrets", systemText);
    }

    [Fact]
    public void AssemblePrompt_PacksAll6DynamicContextTagsIntoLatestUserMessage()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        var context = new CognitiveContext(
            MacroChronicle: "대서사 줄거리 텍스트",
            LedgerFacts: [new(10, "안개 숲에서 야영함")],
            Entities: [new("레이나", "70%", "신뢰", "정상", "원초적 증오")],
            ActiveClues: [new("c1", "외투 속 비밀 쪽지", 3, 3)],
            RetrievedLores: [new("흑철검", ["흑철검"], "고대 드워프의 명검", "아이템")],
            RecalledHistory: [new(5, "5턴 전의 중요한 약속")]
        );

        // Act
        var request = assembler.AssemblePrompt(preset, [], new ChatMessage("user", "내 질문입니다.", System.DateTime.Now), context);

        // Assert
        var lastContent = request.Contents.Last();
        Assert.Equal("user", lastContent.Role);
        string userPayload = lastContent.Parts.FirstOrDefault()?.Text ?? string.Empty;

        Assert.Contains("<macro_chronicle>", userPayload);
        Assert.Contains("대서사 줄거리 텍스트", userPayload);
        Assert.Contains("<internal_chronicle_facts>", userPayload);
        Assert.Contains("안개 숲에서 야영함", userPayload);
        Assert.Contains("<entity_relationship>", userPayload);
        Assert.Contains("원초적 증오", userPayload);
        Assert.Contains("<world_lore>", userPayload);
        Assert.Contains("흑철검: 고대 드워프의 명검", userPayload);
        Assert.Contains("<recalled_history>", userPayload);
        Assert.Contains("5턴 전의 중요한 약속", userPayload);
        Assert.Contains("<latent_secrets mode=\"STRICT_DORMANT\">", userPayload);
        Assert.Contains("외투 속 비밀 쪽지", userPayload);
        Assert.Contains("<current_action>", userPayload);
        Assert.Contains("내 질문입니다.", userPayload);
        Assert.Contains("<final_instruction>", userPayload);
    }

    [Fact]
    public void AssemblePrompt_WhenLatestUserTextEmpty_AppliesFallbackToPreventHttp400()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        var emptyContext = new CognitiveContext("", [], [], [], [], []);

        // Act - 빈 문자열 입력 (Auto-Play 상황)
        var request = assembler.AssemblePrompt(preset, [], new ChatMessage("user", "", System.DateTime.Now), emptyContext);

        // Assert - Part Text가 비어있지 않고 Fallback 주입
        var lastContent = request.Contents.Last();
        string userPayload = lastContent.Parts.FirstOrDefault()?.Text ?? string.Empty;

        Assert.Contains("<current_action>", userPayload);
        Assert.Contains("(서사를 계속 진행하십시오.)", userPayload);
        Assert.False(string.IsNullOrWhiteSpace(userPayload));
    }

    [Fact]
    public void AssemblePrompt_WhenPlotCatalystProvided_PacksCatalystTag()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        var context = new CognitiveContext("", [], [], [], [], [], PlotCatalyst: "돌발 위기: 제단에서 마수가 깨어남!");

        // Act
        var request = assembler.AssemblePrompt(preset, [], new ChatMessage("user", "다음 행동은?", System.DateTime.Now), context);

        // Assert
        string userPayload = request.Contents.Last().Parts.FirstOrDefault()?.Text ?? string.Empty;
        Assert.Contains("<plot_catalyst>", userPayload);
        Assert.Contains("돌발 위기: 제단에서 마수가 깨어남!", userPayload);
    }
}
