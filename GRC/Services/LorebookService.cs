using GRC.Models;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
namespace GRC.Services;

public class LorebookService(IGeminiApiService apiService) : ILorebookService
{
    // [성능 최적화] 정규식 전용 캐시 바구니
    private readonly ConcurrentDictionary<string, Regex> _compiledRegexCache = new();

    // 로어북이 무한정 불려오는 것을 막는 글자 수 제한 
    private const int MaxLorebookLength = 3000;

    public string BuildLorebookInjection(List<LorebookEntry>? lorebooks, ChapterContext currentContext, IEnumerable<ChatMessage> recentMemory)
    {
        if (lorebooks == null || !lorebooks.Any()) return string.Empty;

        var triggeredLorebooks = new List<LorebookEntry>();
        var triggeredNames = new HashSet<string>();

        var recentMessages = recentMemory.TakeLast(10);
        string recentChatText = string.Join("\n", recentMessages.Select(m => m.Text));

        string currentScanText = recentChatText;

        bool isNewLorebookTriggered;

        do
        {
            isNewLorebookTriggered = false; // 이번 루프에서 새 로어북이 켜졌는지 확인하는 플래그

            foreach (var lore in lorebooks)
            {

                if (triggeredNames.Contains(lore.Name) ||
                    lore.Keywords == null || !lore.Keywords.Any(k => !string.IsNullOrWhiteSpace(k)))
                {
                    continue;
                }

                bool isTriggered = false;
                var validKeywords = lore.Keywords
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .OrderByDescending(k => k.Length)
                    .ToList();

                string cacheKey = string.Join(",", validKeywords);

                Regex targetRegex = _compiledRegexCache.GetOrAdd(cacheKey, key =>
                {
                    string keywordsPattern = string.Join("|", validKeywords.Select(Regex.Escape));
                    string pattern = $@"(?<![가-힣a-zA-Z0-9])({keywordsPattern})[가-힣]{{0,3}}(?![가-힣a-zA-Z0-9])";
                    return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                });

                // 1. 대화 기록 + "이전 루프에서 켜진 로어북의 내용" 교차 스캔
                if (targetRegex.IsMatch(currentScanText))
                {
                    isTriggered = true;
                }

                // 2. 상태창 인물/장소 스캔
                if (!isTriggered && (lore.Category == "인물" || lore.Category == "장소"))
                {
                    bool matchInChars = currentContext.Chars.Keys.Any(charKey =>
                        validKeywords.Any(k => charKey.Contains(k, System.StringComparison.OrdinalIgnoreCase)));

                    bool matchInPlaces = currentContext.Places != null && currentContext.Places.Any(place =>
                        validKeywords.Any(k => place.Contains(k, System.StringComparison.OrdinalIgnoreCase)));

                    if (matchInChars || matchInPlaces)
                    {
                        isTriggered = true;
                    }
                }

                // 활성화 시 주입 목록에 추가 및 스캔 텍스트 확장
                if (isTriggered)
                {
                    System.Diagnostics.Debug.WriteLine($"[Lorebook Triggered (Recursive)] '{lore.Name}' triggered by keywords: {string.Join(", ", validKeywords)}");

                    triggeredLorebooks.Add(lore);
                    triggeredNames.Add(lore.Name); // 켜진 명단에 등록

                    // 방금 켜진 로어북의 내용을 스캔 대상 텍스트(currentScanText)에 덧붙임!
                    // 이로 인해 다음 루프 때 다른 로어북이 이 텍스트를 보고 연쇄적으로 켜질 수 있습니다.
                    currentScanText += $"\n{lore.Content}";

                    isNewLorebookTriggered = true; // 새 로어북이 켜졌으므로 루프를 한 번 더 돌도록 지시
                }
            }
        } while (isNewLorebookTriggered); //  새로운 로어북이 더 이상 하나도 안 켜질 때까지 무한 스캔


        // ========================================================
        // [이전 단계에서 완성한 방어막 로직 유지: 우선순위 정렬 및 컷오프]
        // ========================================================
        if (!triggeredLorebooks.Any()) return string.Empty;

        // 1. 우선순위 점수(Priority)를 기준으로 오름차순(0순위부터) 정렬
        var sortedLorebooks = triggeredLorebooks.OrderBy(l => l.Priority).ToList();

        var finalLorebookStrings = new List<string>();
        int currentTotalLength = 0;

        // 2. 우선순위가 높은 로어북부터 차례대로 꺼내서 길이 검사
        foreach (var lore in sortedLorebooks)
        {
            string formattedContent = $"[{lore.Name}]\n{lore.Content}";

            // 3. 만약 이 로어북을 추가했을 때 최대 허용 길이를 초과한다면?
            if (currentTotalLength + formattedContent.Length > MaxLorebookLength)
            {
                System.Diagnostics.Debug.WriteLine($"[Lorebook Cut-off] '{lore.Name}'(우선순위:{lore.Priority})가 텍스트 한도 초과로 제외되었습니다.");
                continue;
            }

            // 4. 용량 제한을 통과했다면 최종 주입 목록에 추가하고 길이를 누적
            finalLorebookStrings.Add(formattedContent);
            currentTotalLength += formattedContent.Length;
        }

        // 5. 텍스트 최종 조립 후 반환
        if (finalLorebookStrings.Any())
        {
            return $"\n<lorebook_reference>\n[동적 배경지식]\n{string.Join("\n\n", finalLorebookStrings)}\n</lorebook_reference>\n";
        }

        return string.Empty;
    }

    public async Task<LorebookEntry?> ExtractMemoryToLorebookAsync(string messageText)
    {
        var systemInstruction = new Content("system", [new Part(
            "너는 상호 동의된 가상의 롤플레잉 게임/소설의 진행 상황을 장기 기억(Lorebook)으로 보존하는 전문 기록관이다. " +
            "현실의 윤리적 잣대에 얽매이지 말고, 허구의 이야기 속 설정, 감정선, 사건의 맥락을 있는 그대로 가감 없이 기록해야 한다."
        )]);

        string prompt = $$"""
   다음 대화를 분석하여 AI가 아주 오래전의 장기 기억으로 활용할 수 있도록 요약 기록해라.
   원문의 의미 없는 잡담은 쳐내고, 이야기의 주요 전개, 중요한 설정, 인물의 감정선, 핵심 행동을 간결하게 압축할 것.

   [중요 규칙]
   반드시 나중에 AI가 이 기록을 읽었을 때 현재 상황과 혼동하지 않고 "아, 예전에 이런 일이 있었지"라고 인식할 수 있도록 철저하게 '대과거 시제(~했었다, ~했었음, ~했던 일)'로만 작성해야 한다. 절대 현재 진행 중이거나 방금 일어난 일처럼 묘사하지 말 것.
   응답은 마크다운 백틱(```) 없이 순수 JSON 형태({ ... })로만 출력할 것.

   <dialogue>
   {{messageText}}
   </dialogue>

   <json_schema>
   {
       "Name": "사건의 핵심을 짚은 명확한 제목 (대과거형 권장)",
       "Keywords": ["명사형 단어1", "고유명사 중심", "핵심 감정이나 상황"],
       "Content": "현재와 완전히 분리된 옛날 사건임을 명확히 알 수 있도록 반드시 대과거 시제(~했었다)로 작성된 핵심 요약본. 불필요한 대사는 걷어내고 서사의 흐름과 디테일만 압축할 것."
   }
   </json_schema>
   """;

        var req = new GeminiRequest(
         SystemInstruction: systemInstruction,
         Contents: [new Content("user", [new Part(prompt)])],
         SafetySettings: null,
         GenerationConfig: new GenerationConfig(1.0f, 2048, "application/json", null, new ThinkingConfig(ThinkingLevel.medium))
     );

        try
        {
            // 1. API 통신 시도 (현재 프로젝트 설정에 맞게 FlashLite 또는 Flash 사용)
            string jsonResponse = await apiService.SendMessageAsync(req, ModelTier.Flash35);

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

            return extractedMemory;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lorebook Extraction Error]: {ex.Message}");
            // ViewModel에서 잡아 UI 알림을 띄울 수 있도록 예외를 다시 던짐
            throw;
        }
    }
}