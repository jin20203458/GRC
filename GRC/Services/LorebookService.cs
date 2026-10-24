using GRC.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GRC.Services;

public class LorebookService(IGeminiApiService apiService) : ILorebookService
{

    public async Task<LorebookEntry?> ExtractMemoryToLorebookAsync(string messageText)
    {
        var systemInstruction = new Content("system", [new Part("""
<system_directive>
Role: 롤플레잉 소설의 전문 연대기 기록관 (3rd-person Omniscient Chronicler)
Task: <dialogue>를 분석하여 AI의 장기 기억(Lorebook)으로 보존할 핵심 사건을 대과거 시제로 추출하고 JSON 객체로 반환
</system_directive>

<rules>
1. 이야기의 주요 사건 전개, 핵심 설정, 인물의 감정선 변화 및 중대 결정을 간결하게 압축하십시오.
2. 모든 서술은 반드시 대과거 시제(~했었다, ~했었음, ~했던 일)로 작성하여 현재 진행 중인 이야기와 혼동을 방지하십시오.
</rules>

<output_format>
반드시 아래 TypeScript 인터페이스 규격을 준수하는 단일 JSON 객체만을 출력하십시오.

interface LorebookEntry {
  Name: string; // 사건의 핵심을 짚은 명확한 제목 (대과거형 권장)
  Keywords: string[]; // 명사형 단어, 고유명사 중심, 핵심 감정이나 상황
  Content: string; // 반드시 대과거 시제(~했었다)로 작성된 핵심 요약본
}
</output_format>

<example>
<input>
user: 숲의 폐허에서 오래된 검을 발견한다.
model: 이끼 낀 제단 위에 녹슨 검이 놓여 있었다. 칼날에 희미한 룬 문자가 새겨진 검을 감싸는 순간 차가운 전류가 팔을 타고 올라왔다.
user: 검을 집어든다.
model: 검을 쥐는 순간, 머릿속에 낯선 기억의 파편들이 쏟아졌다. 한때 이 검의 주인이었던 기사의 최후가 환영처럼 스쳐 지나갔다.
</input>
<output>
{
  "Name": "폐허의 제단에서 룬 검을 발견했던 일",
  "Keywords": ["룬 검", "폐허", "기사의 환영"],
  "Content": "숲 속 폐허의 이끼 낀 제단 위에서 룬 문자가 새겨진 녹슨 검을 발견했었다. 검을 쥐자 차가운 전류와 함께 이전 주인이었던 기사의 최후가 환영으로 스쳐 지나갔었다."
}
</output>
</example>
""")]);

        string prompt = $$"""
<dialogue>
{{messageText}}
</dialogue>

<final_instruction>
위 대화에서 장기 기억으로 보존할 핵심 사건을 추출하여 JSON을 즉시 출력하십시오.
</final_instruction>
""";

        var lorebookSchema = new
        {
            type = "OBJECT",
            properties = new
            {
                Name = new { type = "STRING", description = "사건의 핵심을 짚은 명확한 제목 (대과거형)" },
                Keywords = new { type = "ARRAY", items = new { type = "STRING" }, description = "핵심 키워드 목록 (명사형)" },
                Content = new { type = "STRING", description = "대과거 시제로 작성된 핵심 요약 서사" }
            },
            required = new[] { "Name", "Keywords", "Content" }
        };

        var req = new GeminiRequest(
            SystemInstruction: systemInstruction,
            Contents: [new Content("user", [new Part(prompt)])],
            SafetySettings: [
                new("HARM_CATEGORY_HARASSMENT", BlockThreshold.BLOCK_NONE),
                new("HARM_CATEGORY_HATE_SPEECH", BlockThreshold.BLOCK_NONE),
                new("HARM_CATEGORY_SEXUALLY_EXPLICIT", BlockThreshold.BLOCK_NONE),
                new("HARM_CATEGORY_DANGEROUS_CONTENT", BlockThreshold.BLOCK_NONE)
            ],
            GenerationConfig: new GenerationConfig(null, 2048, "application/json", lorebookSchema, new ThinkingConfig(ThinkingLevel.low))
        );

        try
        {
            // 1. API 통신 시도 (현재 프로젝트 설정에 맞게 FlashLite 또는 Flash 사용)
            string jsonResponse = await apiService.SendMessageAsync(req, ModelTier.Flash);

            // 2. API 또는 시스템 에러 감지 (GeminiApiService의 에러 반환 규격 확인)
            if (jsonResponse.StartsWith("[System", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"API 통신 또는 필터 오류가 발생했습니다.\n상세: {jsonResponse}");
            }

            // 3. JSON 파싱 시도
            var extractedMemory = GRC.Helpers.LlmJsonParser.DeserializeSafe<LorebookEntry>(jsonResponse);

            // 4. 파싱 실패 감지 (AI가 JSON 규격을 어겼거나 엉뚱한 대답을 함)
            if (extractedMemory == null)
            {
                throw new Exception("AI가 올바른 형태의 데이터(JSON)를 생성하지 못했습니다. 다시 시도해 주세요.");
            }

            // 5. 추출된 기억 로어북의 768차원 시맨틱 임베딩 자동 부착 (런타임 쿼리와 동일한 대칭 공간 SEMANTIC_SIMILARITY)
            float[]? embedding = null;
            try
            {
                string embText = BuildEmbeddingSourceText(extractedMemory);
                var emb = await apiService.GetEmbeddingAsync(embText, taskType: "SEMANTIC_SIMILARITY");
                if (emb != null && emb.Length == 768)
                {
                    embedding = emb;
                }
            }
            catch
            {
                embedding = null;
            }

            return extractedMemory with { Embedding = embedding };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lorebook Extraction Error]: {ex.Message}");
            // ViewModel에서 잡아 UI 알림을 띄울 수 있도록 예외를 다시 던짐
            throw;
        }
    }

    public async Task<List<LorebookEntry>> EnsureEmbeddingsAsync(
        IEnumerable<LorebookEntry>? lorebooks,
        CancellationToken cancellationToken = default)
    {
        if (lorebooks == null) return [];

        var list = lorebooks.ToList();
        if (list.Count == 0) return [];

        using var semaphore = new SemaphoreSlim(4, 4);

        var tasks = list.Select(async entry =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return entry;
            }

            // 이미 768차원 유효 임베딩이 존재하면 API 호출 없이 그대로 보존
            if (entry.Embedding != null && entry.Embedding.Length == 768)
            {
                return entry;
            }

            bool acquired = false;
            try
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;

                string sourceText = BuildEmbeddingSourceText(entry);
                if (!string.IsNullOrWhiteSpace(sourceText))
                {
                    var emb = await apiService.GetEmbeddingAsync(
                        sourceText,
                        taskType: "SEMANTIC_SIMILARITY",
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (emb != null && emb.Length == 768)
                    {
                        return entry with { Embedding = emb };
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LorebookService] '{entry.Name}' 임베딩 생성 실패 (Graceful Degradation): {ex.Message}");
            }
            finally
            {
                if (acquired)
                {
                    semaphore.Release();
                }
            }

            return entry with { Embedding = null };
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }

    public static string BuildEmbeddingSourceText(LorebookEntry entry)
    {
        string keywords = entry.Keywords != null && entry.Keywords.Count > 0
            ? string.Join(", ", entry.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)))
            : string.Empty;

        return string.IsNullOrWhiteSpace(keywords)
            ? $"{entry.Name}: {entry.Content}".Trim()
            : $"{entry.Name} ({keywords}): {entry.Content}".Trim();
    }
}
