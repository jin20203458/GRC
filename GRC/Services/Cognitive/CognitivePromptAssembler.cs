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
    private const string FormatRule =
        "\n\n[문체 및 서술 기본 원칙]\n" +
        "- 문학적 소설 산문체(대화는 큰따옴표, 행동과 심리는 서술문)로 깊이 있게 묘사하십시오.\n" +
        "- 시스템 메타 수치, 개조식 불릿 기호, 데이터베이스 태그를 본문에 노출하지 마십시오.\n" +
        "- 제공된 배경 맥락과 비밀은 서사의 인과적 일관성을 유지하는 데만 자연스럽게 활용하십시오.";

    private const string FinalInstruction =
        "\n\n<final_instruction>\n" +
        "- 앞서 제공된 배경 맥락, 세계관, 과거 기억, 잠복 비밀을 철저히 숙지하여 서사의 일관성을 유지하십시오.\n" +
        "- [문체 오염 절대 방지]: 요약본이나 개조식 불릿 형태를 절대 모방하지 말고, 순수한 문학적 산문 소설 형식으로 생생하게 이어가십시오.\n" +
        "- 잠복 비밀(<latent_secrets>)은 플레이어가 직접 지목하여 추궁하거나 수색하지 않는 한 절대 자진하여 폭로하거나 입 밖으로 꺼내지 마십시오.\n" +
        "</final_instruction>";

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

        // 3.2 8턴 불변 사건 렛저 (<internal_chronicle_facts>)
        if (cognitiveContext.LedgerFacts != null && cognitiveContext.LedgerFacts.Count > 0)
        {
            dynamicSb.AppendLine("<internal_chronicle_facts>");
            dynamicSb.AppendLine("[지난 사건 기록]");
            foreach (var fact in cognitiveContext.LedgerFacts.TakeLast(15))
            {
                dynamicSb.AppendLine($"- [Turn {fact.Turn}]: {fact.FactBullet}");
            }
            dynamicSb.AppendLine("</internal_chronicle_facts>\n");
        }

        // 3.3 이중 시간 인물 관계 대비 (<entity_relationship>)
        string entityRelationXml = BiTemporalEntityProjector.FormatEntityRelationship(cognitiveContext.Entities);
        if (!string.IsNullOrWhiteSpace(entityRelationXml))
        {
            dynamicSb.AppendLine(entityRelationXml);
            dynamicSb.AppendLine();
        }

        // 3.4 세계관 로어북 (<world_lore>)
        if (cognitiveContext.RetrievedLores != null && cognitiveContext.RetrievedLores.Count > 0)
        {
            dynamicSb.AppendLine("<world_lore>");
            dynamicSb.AppendLine("[참조 세계관 지식]");
            foreach (var lore in cognitiveContext.RetrievedLores)
            {
                dynamicSb.AppendLine($"- {lore.Name}: {lore.Content}");
            }
            dynamicSb.AppendLine("</world_lore>\n");
        }

        // 3.5 게이팅 RAG 회상 사건 (<recalled_history>)
        if (cognitiveContext.RecalledHistory != null && cognitiveContext.RecalledHistory.Count > 0)
        {
            dynamicSb.AppendLine("<recalled_history>");
            dynamicSb.AppendLine("[과거 챕터 회상 기록]");
            foreach (var recalled in cognitiveContext.RecalledHistory)
            {
                dynamicSb.AppendLine($"- [Turn {recalled.Turn}]: {recalled.FactBullet}");
            }
            dynamicSb.AppendLine("</recalled_history>\n");
        }

        // 3.6 잠복 복선 및 미해결 비밀 (<latent_secrets mode="STRICT_DORMANT">)
        if (cognitiveContext.ActiveClues != null && cognitiveContext.ActiveClues.Count > 0)
        {
            dynamicSb.AppendLine("<latent_secrets mode=\"STRICT_DORMANT\">");
            dynamicSb.AppendLine("[극비 잠복 정보 (플레이어가 직접 지목하여 추궁하지 않는 한 절대 자진 발설 금지)]");
            foreach (var clue in cognitiveContext.ActiveClues)
            {
                dynamicSb.AppendLine($"- {clue.Clue}");
            }
            dynamicSb.AppendLine("</latent_secrets>\n");
        }

        // 3.7 Phase 5 확장 훅: 자율 서사 촉매 (<plot_catalyst>)
        if (!string.IsNullOrWhiteSpace(cognitiveContext.PlotCatalyst))
        {
            dynamicSb.AppendLine("<plot_catalyst>");
            dynamicSb.AppendLine(cognitiveContext.PlotCatalyst);
            dynamicSb.AppendLine("</plot_catalyst>\n");
        }

        // 4. 최신 유저 액션 및 빈 입력(HTTP 400) 방어 로직
        string userActionText = latestUserMessage?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userActionText))
        {
            // Auto-Play 또는 빈 입력 시 PlotCatalyst가 있으면 채택, 없으면 안전 Fallback 주입
            userActionText = !string.IsNullOrWhiteSpace(cognitiveContext.PlotCatalyst)
                ? cognitiveContext.PlotCatalyst
                : "(서사를 계속 진행하십시오.)";
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
}
