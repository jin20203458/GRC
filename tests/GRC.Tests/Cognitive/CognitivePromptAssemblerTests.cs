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
        Assert.Contains("<internal_chronicle_facts", userPayload);
        Assert.Contains("안개 숲에서 야영함", userPayload);
        Assert.Contains("<entity_relationship>", userPayload);
        Assert.Contains("정상 (70%)", userPayload);
        Assert.Contains("<world_lore>", userPayload);
        Assert.Contains("흑철검: 고대 드워프의 명검", userPayload);
        Assert.DoesNotContain("[참조 세계관 지식]", userPayload);
        Assert.Contains("<recalled_history", userPayload);
        Assert.Contains("5턴 전의 중요한 약속", userPayload);
        Assert.Contains("<current_action>", userPayload);
        Assert.Contains("내 질문입니다.", userPayload);
        Assert.Contains("<final_instruction>", userPayload);
        Assert.DoesNotContain("<latent_secrets>", userPayload);
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

        // 2. 상시 칸 헤더에 (현재 제2장 진행 중) 및 속성이 포함되었는지 검증
        Assert.Contains("<internal_chronicle_facts current_chapter=\"2\">", userPayload);
        Assert.DoesNotContain("[최근 사건 흐름", userPayload);

        // 3. 챕터별 그룹 헤더 [제1장], [제2장]이 출력되고 내용이 포함되었는지 검증 (개행 정규화)
        string normalized = userPayload.Replace("\r\n", "\n");
        Assert.Contains("[제1장]\n- 1장 사건 1\n- 1장 사건 2", normalized);
        Assert.Contains("[제2장]\n- 2장 사건 1", normalized);

        // 4. 회상 칸 헤더 및 회상 불릿 검증
        Assert.Contains("<recalled_history priority=\"secondary\">", userPayload);
        Assert.DoesNotContain("[떠오른 과거의 기억", userPayload);
        Assert.Contains("[제1장]\n- 회상 과거 사건", normalized);
    }

    [Fact]
    public void AssemblePrompt_SystemInstruction_ContainsDialogueThoughtAndMarkdownRulesWithExample()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        var emptyContext = new CognitiveContext("", [], [], [], []);

        // Act
        var request = assembler.AssemblePrompt(preset, [], new ChatMessage("user", "테스트", System.DateTime.Now), emptyContext);
        string systemText = request.SystemInstruction?.Parts.FirstOrDefault()?.Text ?? string.Empty;

        // Assert
        Assert.Contains("<style_constraints>", systemText);
        Assert.DoesNotContain("[문체 및 서술 기본 원칙]", systemText);
        Assert.Contains("대사는 큰따옴표 \" \"", systemText);
        Assert.Contains("독백 및 속마음은 낫표 「 」", systemText);
        Assert.Contains("마크다운 강조(*별표* 등)", systemText);
        Assert.Contains("<example>", systemText);
        Assert.Contains("「아무도 없는 건가.」", systemText);
    }

    [Fact]
    public void AssemblePrompt_WhenMetaDirectivePresent_RendersIndependentTagBeforeCurrentAction()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        string meta = "<meta_directive type=\"chapter_transition\">\n새로운 챕터가 시작되었습니다.\n</meta_directive>";
        var context = new CognitiveContext("", [], [], [], [], CurrentChapter: 1, MetaDirective: meta);

        // Act
        var request = assembler.AssemblePrompt(preset, [], new ChatMessage("user", "반지를 건넨다.", System.DateTime.Now), context);
        string userPayload = request.Contents.Last().Parts.FirstOrDefault()?.Text ?? string.Empty;

        // Assert - meta_directive 태그가 current_action 바깥에 독립 태그로 조립되고, current_action은 순수 유저 입력만 포함
        Assert.Contains("<meta_directive type=\"chapter_transition\">", userPayload);
        Assert.Contains("새로운 챕터가 시작되었습니다.", userPayload);
        Assert.Contains("<current_action>\n반지를 건넨다.\n</current_action>", userPayload.Replace("\r\n", "\n"));
        // current_action 내부에 meta_directive가 중첩 유입되지 않았는지 검증
        int metaIndex = userPayload.IndexOf("<meta_directive");
        int currentActionIndex = userPayload.IndexOf("<current_action>");
        int currentActionCloseIndex = userPayload.IndexOf("</current_action>");
        Assert.True(metaIndex < currentActionIndex, "meta_directive는 current_action 이전에 독립 태그로 배치되어야 함");
        Assert.DoesNotContain("<meta_directive", userPayload.Substring(currentActionIndex, currentActionCloseIndex - currentActionIndex));
    }

    [Fact]
    public void AssemblePrompt_WhenMetaDirectiveNull_OmitsMetaDirectiveTag()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        var context = new CognitiveContext("", [], [], [], [], CurrentChapter: 1, MetaDirective: null);

        // Act
        var request = assembler.AssemblePrompt(preset, [], new ChatMessage("user", "일반 대화", System.DateTime.Now), context);
        string userPayload = request.Contents.Last().Parts.FirstOrDefault()?.Text ?? string.Empty;

        // Assert - 평시에는 meta_directive 태그가 전혀 포함되지 않아야 함
        Assert.DoesNotContain("<meta_directive>", userPayload);
        Assert.DoesNotContain("</meta_directive>", userPayload);
    }

    [Fact]
    public void AssemblePrompt_FinalInstruction_SuppressesStyleBleedAndTriggersNarrativeProse()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        var context = new CognitiveContext("", [], [], [], []);

        // Act
        var request = assembler.AssemblePrompt(preset, [], new ChatMessage("user", "테스트", System.DateTime.Now), context);
        string userPayload = request.Contents.Last().Parts.FirstOrDefault()?.Text ?? string.Empty;

        // Assert - 상단 참조 태그 개조식 문체 전염 방지 및 소설 본문 작성 지침 검증
        Assert.Contains("상단에 제공된 참조 태그들의 개조식·요약 형태를 모방하지 말고, <current_action>에 이어지는 다음 장면을 소설 산문 본문으로만 서술하십시오.", userPayload);
        Assert.DoesNotContain("[자가 검증]", userPayload);
        Assert.DoesNotContain("[문체 오염 절대 방지]", userPayload);
        Assert.DoesNotContain("직접적인 원인으로 반영", userPayload);
    }

    [Fact]
    public void AssemblePrompt_WhenStatusProvided_InjectsCurrentStateTag()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        var context = new CognitiveContext(
            MacroChronicle: "",
            LedgerFacts: [],
            Entities: [],
            RetrievedLores: [],
            RecalledHistory: [],
            CurrentChapter: 1,
            MetaDirective: null,
            CustomStats: new Dictionary<string, string> { ["체력"] = "80/100", ["골드"] = "500" },
            Items: new List<string> { "흑철검", "회복약" },
            Places: new List<string> { "북부 성채" }
        );

        // Act
        var request = assembler.AssemblePrompt(preset, [], new ChatMessage("user", "행동", System.DateTime.Now), context);
        string userPayload = request.Contents.Last().Parts.FirstOrDefault()?.Text ?? string.Empty;

        // Assert
        Assert.Contains("<current_state>", userPayload);
        Assert.Contains("- 현재 위치: 북부 성채", userPayload);
        Assert.Contains("- 주요 수치: 체력: 80/100, 골드: 500", userPayload);
        Assert.Contains("- 소지품: 흑철검, 회복약", userPayload);
        Assert.Contains("</current_state>", userPayload);
    }

    [Fact]
    public void AssemblePrompt_WhenStatusEmpty_OmitsCurrentStateTag()
    {
        // Arrange
        var assembler = new CognitivePromptAssembler();
        var preset = CreateDummyPreset();
        var context = new CognitiveContext("", [], [], [], []);

        // Act
        var request = assembler.AssemblePrompt(preset, [], new ChatMessage("user", "행동", System.DateTime.Now), context);
        string userPayload = request.Contents.Last().Parts.FirstOrDefault()?.Text ?? string.Empty;

        // Assert
        Assert.DoesNotContain("<current_state>", userPayload);
        Assert.DoesNotContain("</current_state>", userPayload);
    }
}

