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
/// 8턴 주기로 Gemini 3.5 Flash-Lite 모델을 호출하여 사건 팩트 불릿을 추출하고
/// Google text-embedding-004 임베딩을 비동기 부착하는 구현체
/// </summary>
public class EventLedgerExtractor(IGeminiApiService apiService) : IEventLedgerExtractor
{
    private const string ExtractionPromptTemplate =
        "당신은 RPG 게임의 객관적 역사 서기관입니다. 아래 최근 대화에서 일어난 '실제 사건, 이동한 장소, 획득/사용한 아이템, 명시적 약속/계약'만을 1~2문장의 객관적 팩트 불릿(최대 3개)으로 추출하십시오.\n\n" +
        "[엄격한 제외 기준]:\n" +
        "- 문학적 미사여구, 날씨/분위기 묘사 제외\n" +
        "- 인물의 내면 심리 독백 및 단순 잡담 제외\n\n" +
        "[출력 형식]:\n" +
        "반드시 JSON 형식으로만 응답하십시오:\n" +
        "{\n" +
        "  \"bullets\": [\n" +
        "    \"팩트 1\",\n" +
        "    \"팩트 2\"\n" +
        "  ]\n" +
        "}\n\n" +
        "[최근 대화 이력]:\n";

    public async Task<List<EventLedgerItem>> ExtractLedgerFactsAsync(
        IEnumerable<ChatMessage> recentTurns,
        int currentTurn,
        CancellationToken cancellationToken = default)
    {
        if (recentTurns == null) return [];

        var turnList = recentTurns.ToList();
        if (turnList.Count == 0) return [];

        var sb = new StringBuilder();
        foreach (var msg in turnList)
        {
            string speaker = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase) ? "플레이어" : "AI";
            sb.AppendLine($"{speaker}: {msg.Text}");
        }

        string fullUserPrompt = ExtractionPromptTemplate + sb;

        var request = new GeminiRequest(
            SystemInstruction: null,
            Contents: [new Content("user", [new Part(fullUserPrompt)])],
            SafetySettings: null,
            GenerationConfig: new GenerationConfig(
                Temperature: 0.1f,
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

            // 3. 각 불릿별 Google text-embedding-004 768차원 임베딩 비동기 부착
            var ledgerItems = new List<EventLedgerItem>();
            foreach (var bullet in bullets)
            {
                float[]? embedding = null;
                try
                {
                    var emb = await apiService.GetEmbeddingAsync(bullet, cancellationToken);
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
                    Turn: currentTurn,
                    FactBullet: bullet,
                    Embedding: embedding
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

    private static List<string> ParseBulletsFromJson(string rawText)
    {
        var result = new List<string>();

        try
        {
            // 마크다운 코드 블록(```json ... ```) 정제
            string cleaned = rawText.Trim();
            if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[7..];
            }
            else if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned[3..];
            }

            if (cleaned.EndsWith("```"))
            {
                cleaned = cleaned[..^3];
            }

            cleaned = cleaned.Trim();

            var node = JsonNode.Parse(cleaned);
            if (node is JsonObject obj && obj.TryGetPropertyValue("bullets", out var bulletsNode) && bulletsNode is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    string? bulletText = item?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(bulletText))
                    {
                        result.Add(bulletText.Trim());
                    }
                }
            }
        }
        catch
        {
            // JSON 파싱 실패 시 <plot> 태그 추출 시도 (Fail-Safe Fallback)
            var plotMatch = System.Text.RegularExpressions.Regex.Match(rawText, @"<plot\s*>(.*?)</plot\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (plotMatch.Success && !string.IsNullOrWhiteSpace(plotMatch.Groups[1].Value))
            {
                result.Add(plotMatch.Groups[1].Value.Trim());
            }
        }

        return result;
    }
}
