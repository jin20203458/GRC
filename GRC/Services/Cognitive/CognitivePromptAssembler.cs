using GRC.Helpers;
using GRC.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GRC.Services;

/// <summary>
/// Cognitive Core 3.0 프롬프트 조립기 구현체
/// SystemInstruction 불변 동결 및 최신 턴 유저 프롬프트 직전 6대 인지 태그 패킹
/// </summary>
public class CognitivePromptAssembler : ICognitivePromptAssembler
{
    private const string FormatRule = """


<style_constraints>
1. 대사는 큰따옴표 " ", 독백 및 속마음은 낫표 「 」, 행동과 배경은 기호 없는 순수 평문으로 표기하십시오.
2. 마크다운 강조(*별표* 등), 개조식 불릿, 시스템 메타 수치 및 태그를 본문에 일절 출력하지 마십시오.

<example>
바람이 스산하게 창문을 두드렸다. 방 안은 기묘하리만치 조용했다.
"누구 계신가요?"
목소리가 허공에 흩어지자, 어둠이 더 짙어지는 기분이 들었다.
「아무도 없는 건가.」
</example>
</style_constraints>
""";

    private const string FinalInstruction = """


<final_instruction>
상단에 제공된 참조 태그들의 개조식·요약 형태를 모방하지 말고, <current_action>에 이어지는 다음 장면을 소설 산문 본문으로만 서술하십시오.
</final_instruction>
""";

    public GeminiRequest AssemblePrompt(
        CharacterPreset preset,
        IEnumerable<ChatMessage> shortTermHistory,
        ChatMessage latestUserMessage,
        CognitiveContext cognitiveContext)
    {
        // 1. 100% 불변 동결 SystemInstruction 조립 (로어북 및 동적 컨텍스트 완전 배제)
        var systemSb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(preset.SystemPrompt))
        {
            systemSb.Append(preset.SystemPrompt);
        }

        if (!string.IsNullOrWhiteSpace(preset.Worldview))
        {
            systemSb.AppendLine("\n\n<master_setting>");
            systemSb.Append(preset.Worldview);
            systemSb.Append("\n</master_setting>");
        }

        systemSb.Append(FormatRule);

        var systemInstruction = new Content("system", [new Part(systemSb.ToString())]);

        // 2. 단기 대화 이력 (최근 18~24턴 순수 대화)
        var contents = new List<Content>();
        if (shortTermHistory != null)
        {
            foreach (var msg in shortTermHistory)
            {
                if (msg == latestUserMessage) continue;

                string role = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "model";
                string text = string.IsNullOrWhiteSpace(msg.Text) ? "(침묵)" : msg.Text;
                contents.Add(new Content(role, [new Part(text)]));
            }
        }

        // 3. 최신 유저 턴 Dynamic Context Grounding 6대 요소 조립
        var dynamicSb = new StringBuilder();

        // 3.1 대서사 연대기 (<macro_chronicle>)
        if (!string.IsNullOrWhiteSpace(cognitiveContext.MacroChronicle))
        {
            dynamicSb.AppendLine("<macro_chronicle>");
            dynamicSb.AppendLine(cognitiveContext.MacroChronicle);
            dynamicSb.AppendLine("</macro_chronicle>\n");
        }

        // 3.2 최근 사건 팩트 불릿 상시 노출 (<internal_chronicle_facts>, 최대 15개)
        if (cognitiveContext.LedgerFacts != null && cognitiveContext.LedgerFacts.Count > 0)
        {
            int currentChapter = cognitiveContext.CurrentChapter > 0 ? cognitiveContext.CurrentChapter : 1;
            dynamicSb.AppendLine($"<internal_chronicle_facts current_chapter=\"{currentChapter}\">");
            AppendChapterGroupedFacts(dynamicSb, cognitiveContext.LedgerFacts.TakeLast(15));
            dynamicSb.AppendLine("</internal_chronicle_facts>\n");
        }

        // 3.3 이중 시간 인물 관계 대비 (<entity_relationship>)
        string entityRelationXml = BiTemporalEntityProjector.FormatEntityRelationship(cognitiveContext.Entities);
        if (!string.IsNullOrWhiteSpace(entityRelationXml))
        {
            dynamicSb.AppendLine(entityRelationXml);
            dynamicSb.AppendLine();
        }

        // 3.4 현재 상태 및 환경 (<current_state>)
        string currentStateXml = FormatCurrentState(cognitiveContext.CustomStats, cognitiveContext.Items, cognitiveContext.Places);
        if (!string.IsNullOrWhiteSpace(currentStateXml))
        {
            dynamicSb.AppendLine(currentStateXml);
            dynamicSb.AppendLine();
        }

        // 3.5 세계관 로어북 (<world_lore>)
        if (cognitiveContext.RetrievedLores != null && cognitiveContext.RetrievedLores.Count > 0)
        {
            dynamicSb.AppendLine("<world_lore>");
            foreach (var lore in cognitiveContext.RetrievedLores)
            {
                dynamicSb.AppendLine($"- {lore.Name}: {lore.Content}");
            }
            dynamicSb.AppendLine("</world_lore>\n");
        }

        // 3.6 게이팅 RAG 회상 사건 (<recalled_history>)
        if (cognitiveContext.RecalledHistory != null && cognitiveContext.RecalledHistory.Count > 0)
        {
            dynamicSb.AppendLine("<recalled_history priority=\"secondary\">");
            AppendChapterGroupedFacts(dynamicSb, cognitiveContext.RecalledHistory);
            dynamicSb.AppendLine("</recalled_history>\n");
        }

        // 3.7 메타 지침 (<meta_directive>) - 이벤트 발동 시에만 독립 태그로 조립
        if (!string.IsNullOrWhiteSpace(cognitiveContext.MetaDirective))
        {
            dynamicSb.AppendLine(cognitiveContext.MetaDirective);
            dynamicSb.AppendLine();
        }

        // 4. 최신 유저 액션 및 빈 입력(HTTP 400) 방어 로직
        string userActionText = latestUserMessage?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userActionText))
        {
            userActionText = "(서사를 계속 진행하십시오.)";
        }

        dynamicSb.AppendLine("<current_action>");
        dynamicSb.AppendLine(userActionText);
        dynamicSb.Append("</current_action>");

        // 5. 문체 오염 억제 명령 결합
        dynamicSb.Append(FinalInstruction);

        // 최신 유저 턴 메시지 결합
        contents.Add(new Content("user", [new Part(dynamicSb.ToString())]));

        return new GeminiRequest(
            SystemInstruction: systemInstruction,
            Contents: contents,
            SafetySettings: null,
            GenerationConfig: null
        );
    }

    private static void AppendChapterGroupedFacts(StringBuilder sb, IEnumerable<EventLedgerItem> items)
    {
        int lastChapter = -1;
        foreach (var item in items)
        {
            if (item.Chapter > 0 && item.Chapter != lastChapter)
            {
                sb.AppendLine($"[제{item.Chapter}장]");
                lastChapter = item.Chapter;
            }
            sb.AppendLine($"- {item.FactBullet}");
        }
    }

    private static string FormatCurrentState(
        Dictionary<string, string>? customStats,
        List<string>? items,
        List<string>? places)
    {
        var sb = new StringBuilder();
        if (places != null && places.Count > 0)
        {
            sb.AppendLine($"- 현재 위치: {string.Join(", ", places)}");
        }
        if (customStats != null && customStats.Count > 0)
        {
            sb.AppendLine($"- 주요 수치: {string.Join(", ", customStats.Select(kv => $"{kv.Key}: {kv.Value}"))}");
        }
        if (items != null && items.Count > 0)
        {
            sb.AppendLine($"- 소지품: {string.Join(", ", items)}");
        }

        if (sb.Length == 0) return string.Empty;

        return $"<current_state>\n{sb.ToString().TrimEnd()}\n</current_state>";
    }
}
