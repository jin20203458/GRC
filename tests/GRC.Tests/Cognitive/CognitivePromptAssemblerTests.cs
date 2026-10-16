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
            LedgerFacts: [new("사건 불릿 1")],
            Entities: [new("레이나", "안정", "70%")],
            RetrievedLores: [new("흑철검", ["검"], "고대 검", "아이템")],
            RecalledHistory: [new("회상 팩트")]
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
    }

    [Fact]
    public void AssemblePrompt_PacksAllDynamicContextTagsIntoLatestUserMessage()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        var context = new CognitiveContext(
            MacroChronicle: "대서사 줄거리 텍스트",
            LedgerFacts: [new("안개 숲에서 야영함")],
            Entities: [new("레이나", "정상", "70%")],
            RetrievedLores: [new("흑철검", ["흑철검"], "고대 드워프의 명검", "아이템")],
            RecalledHistory: [new("5턴 전의 중요한 약속")]
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
        Assert.Contains("정상 (70%)", userPayload);
        Assert.Contains("<world_lore>", userPayload);
        Assert.Contains("흑철검: 고대 드워프의 명검", userPayload);
        Assert.Contains("<recalled_history>", userPayload);
        Assert.Contains("5턴 전의 중요한 약속", userPayload);
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
        var emptyContext = new CognitiveContext("", [], [], [], []);

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
    public void AssemblePrompt_GroupsBulletsByChapterHeader_AndExcludesTurnTags()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        var context = new CognitiveContext(
            MacroChronicle: "",
            LedgerFacts: [
                new(FactBullet: "1장 사건 1", Chapter: 1),
                new(FactBullet: "1장 사건 2", Chapter: 1),
                new(FactBullet: "2장 사건 1", Chapter: 2)
            ],
            Entities: [],
            RetrievedLores: [],
            RecalledHistory: [
                new(FactBullet: "회상 과거 사건", Chapter: 1)
            ],
            CurrentChapter: 2
        );

        // Act
        var request = assembler.AssemblePrompt(preset, [], new ChatMessage("user", "행동", System.DateTime.Now), context);
        string userPayload = request.Contents.Last().Parts.FirstOrDefault()?.Text ?? string.Empty;

        // Assert
        // 1. [Turn X] 태그가 완전히 배제되었는지 검증
        Assert.DoesNotContain("[Turn 8]", userPayload);
        Assert.DoesNotContain("[Turn 16]", userPayload);
        Assert.DoesNotContain("[Turn 24]", userPayload);
        Assert.DoesNotContain("[Turn 4]", userPayload);

        // 2. 상시 칸 헤더에 (현재 제2장 진행 중)이 포함되었는지 검증
        Assert.Contains("[최근 사건 흐름 - 위에서 아래로 시간순 (현재 제2장 진행 중)]", userPayload);

        // 3. 챕터별 그룹 헤더 [제1장], [제2장]이 출력되고 내용이 포함되었는지 검증 (개행 정규화)
        string normalized = userPayload.Replace("\r\n", "\n");
        Assert.Contains("[제1장]\n- 1장 사건 1\n- 1장 사건 2", normalized);
        Assert.Contains("[제2장]\n- 2장 사건 1", normalized);

        // 4. 회상 칸 헤더 및 회상 불릿 검증
        Assert.Contains("[떠오른 과거의 기억 - 현재 상태와 상충 시 현재 상태가 우선함]", userPayload);
        Assert.Contains("[제1장]\n- 회상 과거 사건", normalized);
    }
}
