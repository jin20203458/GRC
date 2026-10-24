using GRC.Helpers;
using GRC.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GRC.Services;

/// <summary>
/// 4턴(8메시지) 주기로 Gemini 3.5 Flash-Lite 모델을 호출하여 사건 팩트 불릿을 추출하고
/// Google 768차원 다국어 임베딩을 비동기 부착하는 구현체
/// </summary>
public class EventLedgerExtractor(IGeminiApiService apiService) : IEventLedgerExtractor
{
    private const string ExtractionSystemInstruction = """
<system_directive>
Domain: RPG Narrative History & Causal Fact Bullet Extraction
Task: 최근 대화 이력을 분석하여 핵심 사건, 이동한 장소, 획득/사용한 아이템, 체결된 약속, 중요 단서나 비밀을 1~2문장의 자기완결형 인과 팩트 불릿(최대 3~5개)으로 추출하십시오.
Output Language: Korean
</system_directive>

<rules>
1. 각 항목은 다른 문맥 없이 단독으로 읽혀도 전후 인과관계를 이해할 수 있는 '자기완결형 문장(1~2문장)'이어야 합니다.
2. 모든 주체와 대상은 인물명, 지명, 아이템명 등 명시적인 고유명사로 기술하십시오.
3. (원인/배경 + 명시적 주체 + 행동/사건 + 결과/변화)의 인과관계를 한 문장 안에 유기적으로 압축하십시오.
</rules>

<output_format>
반드시 아래 TypeScript 인터페이스 규격의 순수 JSON으로만 응답하십시오:
interface EventLedgerOutput {
  bullets: string[]; // 인과관계가 완결된 1~2문장의 사건 팩트 불릿 목록 (최대 3~5개)
}
</output_format>
""";

    public async Task<List<EventLedgerItem>> ExtractLedgerFactsAsync(
        IEnumerable<ChatMessage> recentTurns,
        int currentChapter = 1,
        CancellationToken cancellationToken = default)
    {
        if (recentTurns == null) return [];

        var turnList = recentTurns.ToList();
        if (turnList.Count == 0) return [];

        var sb = new StringBuilder();
        sb.AppendLine("<dialogue_history>");
        foreach (var msg in turnList)
        {
            string speaker = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase) ? "플레이어" : "AI";
            sb.AppendLine($"{speaker}: {msg.Text}");
        }
        sb.AppendLine("</dialogue_history>\n");
        sb.AppendLine("<final_instruction>");
        sb.AppendLine("위 <dialogue_history>에서 핵심 인과 사건을 추출하여 <output_format>의 JSON 규격으로만 응답하십시오.");
        sb.AppendLine("</final_instruction>");

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(ExtractionSystemInstruction)]),
            Contents: [new Content("user", [new Part(sb.ToString())])],
            SafetySettings: null,
            GenerationConfig: new GenerationConfig(
                Temperature: null,
                MaxOutputTokens: 1024,
                ResponseMimeType: "application/json"
            )
        );

        try
        {
            // 1. Gemini 3.5 Flash-Lite 모델 호출
            string responseJson = await apiService.SendMessageAsync(request, ModelTier.FlashLite, cancellationToken);
            if (string.IsNullOrWhiteSpace(responseJson)) return [];

            // 2. JSON 파싱
            var bullets = ParseBulletsFromJson(responseJson);
            if (bullets.Count == 0) return [];

            // 3. 각 불릿별 768차원 다국어 임베딩 비동기 부착
            var ledgerItems = new List<EventLedgerItem>();
            foreach (var bullet in bullets)
            {
                float[]? embedding = null;
                try
                {
                    var emb = await apiService.GetEmbeddingAsync(bullet, cancellationToken: cancellationToken);
                    if (emb != null && emb.Length == 768)
                    {
                        embedding = emb;
                    }
                }
                catch
                {
                    // 임베딩 생성 실패 시 null 유지 (Graceful Degradation)
                    embedding = null;
                }

                ledgerItems.Add(new EventLedgerItem(
                    FactBullet: bullet,
                    Embedding: embedding,
                    Chapter: currentChapter
                ));
            }

            return ledgerItems;
        }
        catch
        {
            // LLM 호출 실패 시 대화 흐름을 방해하지 않고 안전하게 빈 리스트 반환
            return [];
        }
    }

    private sealed class LedgerExtractionDto
    {
        public List<string>? Bullets { get; set; }
    }

    private static List<string> ParseBulletsFromJson(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return [];

        try
        {
            // 1. DTO 객체 역직렬화 (마크다운 펜스 제거, 사설 스킵, 잘린 JSON 복구 자동 처리)
            var dto = LlmJsonParser.DeserializeSafe<LedgerExtractionDto>(rawText);
            if (dto?.Bullets != null && dto.Bullets.Count > 0)
            {
                return dto.Bullets
                    .Where(b => !string.IsNullOrWhiteSpace(b))
                    .Select(b => b.Trim())
                    .ToList();
            }

            // 2. 모델이 {"bullets": [...]} 대신 최상위 배열 ["팩트1", "팩트2"]로 응답했을 경우를 위한 Fallback
            var arrayFallback = LlmJsonParser.DeserializeArraySafe<List<string>>(rawText);
            if (arrayFallback != null && arrayFallback.Count > 0)
            {
                return arrayFallback
                    .Where(b => !string.IsNullOrWhiteSpace(b))
                    .Select(b => b.Trim())
                    .ToList();
            }
        }
        catch
        {
            // JSON 파싱 실패 시 안전하게 빈 리스트 반환
        }

        return [];
    }
}
