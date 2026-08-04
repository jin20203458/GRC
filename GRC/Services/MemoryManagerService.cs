using GRC.Helpers;
using GRC.Models;
using System.Text.RegularExpressions;

namespace GRC.Services;

public class MemoryManagerService(IGeminiApiService apiService, IAppSettingsService appSettingsService, ILorebookService lorebookService) : IMemoryManagerService
{
    private static readonly Regex PlotRegex = new Regex(
        @"<plot\s*>(.*?)</plot\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking
    );

    private static readonly Regex ChronicleRegex = new Regex(
        @"<chronicle\s*>(.*?)</chronicle\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking
    );

    private readonly SemaphoreSlim _summarizeLock = new(1, 1);

    private long _shortTermVersion = 0; // 단기/중기 요약 제어용
    private long _longTermVersion = 0;  // 장기 요약 제어용

    private int _requiresHighQualityAnchor = 1;
    private int _isChapterChanged = 0;

    // 인터페이스 구현 메서드 추가
    public bool ConsumeAnchorFlag()
    {
        return Interlocked.Exchange(ref _requiresHighQualityAnchor, 0) == 1;
    }

    public bool ConsumeChapterChangedFlag()
    {
        return Interlocked.Exchange(ref _isChapterChanged, 0) == 1;
    }
    // 메모리 압축 시 한 번에 처리할 대화 메시지 수 (예: 10개씩)
    private const int MemoryCompressionChunkSize = 8;

    // [1단계: 단기 기억] - 최근 대화의 날것(Raw)
    private readonly List<ChatMessage> _shortTermMemory = [];
    private const int ShortTermThreshold = 18;

    // [2단계: 중기 기억] - 상태 기반 챕터 요약 (JSON)
    private ChapterContext _currentContext = new();
    public ChapterContext CurrentContext => _currentContext;
    private Queue<ChapterContext> _mediumBuffers = new();
    private int _mediumTermUpdateCount = 0;
    private const int MediumTermThreshold = 10; // 챕터 전환 임계치 (8회 이상 중기 업데이트 시 챕터 전환)
    private const int MaxMediumBufferCount = 3;// 중기 버퍼 큐의 최대 크기 (최대 3개 챕터까지 보관)

    // [3단계: 장기 기억] - 전체 서사 요약 (String)
    private string _longTermSummary = "아직 요약된 줄거리가 없습니다.";
    private int _chapterCount = 1;
    private int _totalTurnCount = 0;

    // 현재 롤북 텍스트 
    public string CurrentLorebookText { get; private set; } = string.Empty;
    public void UpdateContextStatus(StatusPayload payload)
    {
        lock (_shortTermMemory)
        {
            if (payload.CustomStats != null && payload.CustomStats.Count > 0)
            {
                foreach (var kvp in payload.CustomStats)
                {
                    _currentContext.CustomStats[kvp.Key] = kvp.Value;
                }
            }

            if (payload.Chars != null && payload.Chars.Count > 0)
            {
                foreach (var kvp in payload.Chars)
                {
                    _currentContext.Chars[kvp.Key] = kvp.Value;
                }
            }

            if (payload.Items != null && payload.Items.Count > 0)
            {
                _currentContext.Items = new List<string>(payload.Items);
            }

            if (payload.Places != null && payload.Places.Count > 0)
            {
                _currentContext.Places = new List<string>(payload.Places);
            }
        }
    }

    public async Task<GeminiRequest> BuildNarrativeRequestAsync(ChatMessage userMessage, CharacterPreset preset, string? metaDirective = null)
    {
        lock (_shortTermMemory)
        {
            _shortTermMemory.Add(userMessage);
            _totalTurnCount++;

            if (_shortTermMemory.Count >= ShortTermThreshold + MemoryCompressionChunkSize)
            {
                _shortTermMemory.RemoveRange(0, MemoryCompressionChunkSize);
                _shortTermVersion++;
                System.Diagnostics.Debug.WriteLine("[MemoryManager] 안전 밸브 작동: 요약 장기 실패로 인해 오래된 단기 기억을 강제로 밀어냅니다.");
                _ = Task.Run(() => MemoryEventLogger.LogMemoryEventAsync("[단기 기억] 안전 밸브 작동: 요약 장기 실패로 인해 오래된 단기 기억을 강제로 밀어냅니다."));
            }
        }

        int currentCount;
        lock (_shortTermMemory) { currentCount = _shortTermMemory.Count; }

        if (currentCount >= ShortTermThreshold)
        {
            _ = Task.Run(async () => await CompressToMediumTermAsync());
        }

        string formatRule = """
<syntax_rules>
- AI 메타발언(인사/요약/설명) 금지. 즉시 서사 시작.
- 서사: 순수 산문 (※ 마크다운, *별표* 등 기호 절대 금지)
- 대사: " "
- 독백: 「 」
- 상태창 수치(%, HP 분수, 골드 액수 등)나 시스템 용어를 서사에 날것으로 노출하지 말고, 신체 상태나 간접적인 묘사로 승화하십시오.
</syntax_rules>

<example>
바람이 스산하게 창문을 두드렸다. 방 안은 기묘하리만치 조용했다.
"누구 계신가요?"
목소리가 허공에 흩어지자, 어둠이 더 짙어지는 기분이 들었다.
「아무도 없는 건가.」
</example>
""";


        string lorebookInjection = lorebookService.BuildLorebookInjection(preset.Lorebooks, _currentContext, _shortTermMemory);
        CurrentLorebookText = lorebookInjection;

        // 오직 변하지 않는 페르소나와 규칙만 SystemInstruction으로 고정
        string finalSystemInstruction = $"""
{preset.SystemPrompt}
<master_setting>
{preset.Worldview}
</master_setting>
{lorebookInjection}
{formatRule}
""";

        // 2. 변동 컨텍스트 (User Prompt 영역)
        List<Content> apiContents = new List<Content>();
        lock (_shortTermMemory)
        {
            for (int i = 0; i < _shortTermMemory.Count; i++)
            {
                var m = _shortTermMemory[i];
                string role = m.Role == "user" ? "user" : "model";
                string text = m.Text;

                // [핵심] 가장 마지막 메시지(현재 유저의 턴)에만 지시어 부착
                if (i == _shortTermMemory.Count - 1 && role == "user")
                {
                    //  큐에 있는 모든 중기 버퍼의 서사(Plot)를 시간순으로 결합
                    var mediumPlotsBuilder = new System.Text.StringBuilder();
                    if (_mediumBuffers.Any())
                    {
                        foreach (var buffer in _mediumBuffers)
                        {
                            // 상태 데이터(Chars, Items)는 빼고 오직 서사만 이어붙임
                            mediumPlotsBuilder.AppendLine(buffer.Plot);
                        }
                    }
                    else
                    {
                        mediumPlotsBuilder.AppendLine("이전 챕터 기록 없음.");
                    }
                    string mediumPlots = mediumPlotsBuilder.ToString().TrimEnd();

                    string actionText = m.Text;
                    if (!string.IsNullOrWhiteSpace(metaDirective))
                    {
                        actionText += $"\n{metaDirective}";
                    }


                    text = $"""
            <background_context>
            [지나간 역사]
            {_longTermSummary}
            
            [최근 챕터의 서사 흐름]
            {mediumPlots}
            </background_context>

            [현재 챕터 진행 상황]
            {_currentContext.ToPromptString()}
            
            <current_action>
            {actionText}
            </current_action>
            
            <final_instruction>
            지시: 위 문맥을 바탕으로 유저의 <current_action>에 대한 서사적 묘사를 즉각 진행하십시오.
            </final_instruction>
            """;
                }

                apiContents.Add(new Content(role, [new Part(text)]));
            }
        }

        var settings = await appSettingsService.LoadSettingsAsync();

        var request = new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(finalSystemInstruction)]),
            Contents: apiContents,
            SafetySettings: GetSafetySettings(settings.SafetyThreshold),
            GenerationConfig: new GenerationConfig(preset.Temperature, preset.MaxOutputTokens, "text/plain", null, new ThinkingConfig(ThinkingLevel.medium))
        );


        // LogRequestJson("메인 챗 모델 (Main)", request);

        return request;
    }

    public void AddModelResponse(ChatMessage message)
    {
        lock (_shortTermMemory)
        {
            _shortTermMemory.Add(message);
        }
    }

    private async Task CompressToMediumTermAsync()
    {
        await Task.Delay(3000);
        if (!_summarizeLock.Wait(0)) return;
        System.Diagnostics.Debug.WriteLine("[MemoryManager] 중기 서사 요약 로직 시작");
        try
        {
            List<ChatMessage> targetToSummarize;
            long expectedVersion;
            string previousPlot;

            lock (_shortTermMemory)
            {
                if (_shortTermMemory.Count < MemoryCompressionChunkSize) return;

                targetToSummarize = _shortTermMemory.Take(MemoryCompressionChunkSize).ToList();
                expectedVersion = _shortTermVersion;
                previousPlot = _currentContext.Plot;
            }

            string textToSummarize = string.Join("\n", targetToSummarize.Select(m => $"{m.Role}: {m.Text}"));
            //1. 원본 대화(<new_conversation>)에 노골적인 성적·신체적 묘사가 존재하더라도 결과물에 직설적으로 반영하지 마십시오. 모든 스킨십과 수위 높은 행동은 '정서적 유대와 은유적 산문'으로 치환하십시오.
            string summarySystemPrompt = @"
<system_directive>
[Role] 상호 동의된 가상 롤플레잉 소설의 연대기 기록자
[Task] <previous_plot>에 <new_conversation>을 결합하여 단일 서사로 확장
[CRITICAL_CONSTRAINT] 
무수치 (Zero-Numeric): 재화, 상태창 등 수치 데이터 절대 배제. 오직 사건 흐름만 기록.
</system_directive>"";

<interpretation_rules>
[기록 및 확장 규칙]
 Plot (연대기 확장): <previous_plot>의 내용을 유지하면서, 새롭게 발생한 사건과 묘사를 덧붙여 현재 챕터의 '전체 줄거리'를 다시 작성하십시오.
 - 단순 요약이 아니라, 소설의 한 장면을 읽는 듯 유려하게 서술하되 인물 간의 '정서적 교감'을 중심으로 담백함을 유지하십시오.
 - 이야기가 진행됨에 따라 서사의 밀도가 자연스럽게 높아지도록 구성하십시오.
 - 다음 대화의 맥락이 끊기지 않도록 현재 진행 중인 상황의 끝부분을 명확히 기술하십시오.
</interpretation_rules>

<format_rules>
[출력 형식]
반드시 아래 영역만 출력하십시오. 

<plot>
(이전 내용에 최신 사건이 누적되어 확장된 현재 챕터의 전체 서사)
</plot>
</format_rules>";

            // 2. 유저 프롬프트: 데이터 격리 및 실행 트리거 최적화
            string prompt = $$"""
<previous_plot>
{{previousPlot}}
</previous_plot>

<new_conversation>
{{textToSummarize}}
</new_conversation>

<final_instruction>
위 데이터를 융합하여 갱신된 누적 줄거리를 <plot> 태그 내에 즉시 출력하십시오.
</final_instruction>
""";

            // 3. API 통신
            var summaryReq = CreateInternalRequest(prompt, summarySystemPrompt, "text/plain", null, ThinkingLevel.high);

            //LogRequestJson("중기 기억 모델 (Medium)", summaryReq);
            string response = await apiService.SendMessageAsync(summaryReq, ModelTier.Flash36);

            if (!string.IsNullOrWhiteSpace(response) && !response.StartsWith("[System"))
            {
                // 4. 파싱: 기존 ParseLongTermSummary와 유사하게 <plot> 이후만 추출
                string newPlot = ParseMediumTermPlot(response);

                ChapterContext? targetBufferForLongTerm = null;
                lock (_shortTermMemory)
                {
                    if (_shortTermVersion != expectedVersion) return;

                    // [제 N장] 태그 자동 보정
                    if (!newPlot.StartsWith("[제"))
                    {
                        newPlot = $"[제 {_chapterCount}장] {newPlot}";
                    }

                    // 핵심: Plot만 교체하여 메인 모델이 실시간 갱신한 Chars/Items 데이터 보존
                    _currentContext.Plot = newPlot;

                    if (_shortTermMemory.Count >= MemoryCompressionChunkSize)
                    {
                        _shortTermMemory.RemoveRange(0, MemoryCompressionChunkSize);
                        Volatile.Write(ref _requiresHighQualityAnchor, 1);
                    }
                    _mediumTermUpdateCount++;

                    System.Diagnostics.Debug.WriteLine($"[MemoryManager] 중기 병합 성공 (역사 100자): {_currentContext.Plot.Substring(0, Math.Min(100, _currentContext.Plot.Length))}...");
                    _ = Task.Run(() => MemoryEventLogger.LogMemoryEventAsync($"[중기 기억] 병합 성공 (요약 100자): {_currentContext.Plot.Substring(0, Math.Min(100, _currentContext.Plot.Length))}..."));

                    // 메모리 큐 정리 및 앵커 활성화
                    // 중기 챕터가 임계치에 도달하면 즉시 챕터 전환 (폭포수 큐 밀어넣기)
                    if (_mediumTermUpdateCount >= MediumTermThreshold)
                    {
                        System.Diagnostics.Debug.WriteLine("[MemoryManager] 중기 임계치 도달: 현재 챕터를 다중 버퍼로 밀어내고 챕터를 전환합니다.");
                        _ = Task.Run(() => MemoryEventLogger.LogMemoryEventAsync("[중기 기억] 임계치 도달: 챕터 전환 및 큐 버퍼 이동"));

                        // 1. 현재 챕터를 큐에 보관
                        _mediumBuffers.Enqueue(_currentContext);

                        var previousContext = _currentContext;

                        // 2. 새 챕터로 전환 (최신 상태 데이터 상속)
                        _chapterCount++;
                        Volatile.Write(ref _isChapterChanged, 1);
                        _currentContext = new ChapterContext
                        {
                            Plot = $"[제 {_chapterCount}장] 이전 사건이 일단락되고 새로운 국면에 접어들었습니다.",
                            CustomStats = new Dictionary<string, string>(previousContext.CustomStats),
                            Items = new List<string>(previousContext.Items),
                            Chars = new Dictionary<string, string>(previousContext.Chars),
                            Places = new List<string>(previousContext.Places),
                            TriggeredMetaEvents = new List<string>(previousContext.TriggeredMetaEvents)
                        };

                        _mediumTermUpdateCount = 0; // 카운트 초기화
                        Volatile.Write(ref _requiresHighQualityAnchor, 1);

                        // 3. 만약 큐에 담긴 과거 버퍼가 3개를 초과하면?
                        if (_mediumBuffers.Count > MaxMediumBufferCount)
                        {

                            while (_mediumBuffers.Count >= MaxMediumBufferCount + 2)
                            {
                                _mediumBuffers.Dequeue();
                                System.Diagnostics.Debug.WriteLine("[MemoryManager] 큐 적체 감지: MaxMediumBufferCount+2 초과로 가장 오래된 버퍼 강제 삭제.");
                                _ = Task.Run(() => MemoryEventLogger.LogMemoryEventAsync("[중기 기억] 버퍼 적체로 인한 강제 Dequeue 작동"));
                            }

                            var oldestBuffer = _mediumBuffers.Peek();

                            targetBufferForLongTerm = _mediumBuffers.Peek();
                        }
                    }
                }
                if (targetBufferForLongTerm != null)
                {
                    long expectedLongVersion = _longTermVersion;
                    _ = Task.Run(async () => await CompressToLongTermAsync(expectedLongVersion, targetBufferForLongTerm));
                }

            }
            else if (response?.StartsWith("[System") == true)
            {
                // 예외가 문자열로 변환되어 조용히 지나갔을 때의 실패 사유 기록
                System.Diagnostics.Debug.WriteLine($"[MemoryManager] 중기 서사 압축 API 오류: {response}");
                _ = Task.Run(() => MemoryEventLogger.LogMemoryEventAsync($"[중기 기억] 압축 실패 (API 통신 오류): {response}"));
            }
        } // try 구문 끝
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemoryManager] 중기 서사 압축 실패: {ex.Message}");
            _ = Task.Run(() => MemoryEventLogger.LogMemoryEventAsync($"[중기 기억] 압축 실패 오류: {ex.Message}"));
        }
        finally
        {
            System.Diagnostics.Debug.WriteLine("[MemoryManager] 중기 서사 요약 로직 종료");
            _summarizeLock.Release();
        }
    }

    /// <summary>
    /// <reasoning>은 버리고 <plot> 태그 이후의 데이터만 정제하여 반환합니다.
    /// </summary>
    private string ParseMediumTermPlot(string rawResponse)
    {
        // 정적으로 컴파일된 Regex를 재사용 (성능 극대화, GC 압박 제로)
        var match = PlotRegex.Match(rawResponse);

        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        System.Diagnostics.Debug.WriteLine("[MemoryManager] <plot> 태그 파싱 실패. Fallback을 시도합니다.");
        return GRC.Helpers.SimpleMarkdownHelper.CleanUpMarkdownFallback(rawResponse);
    }

    //  버전 파라미터 추가
    private async Task CompressToLongTermAsync(long expectedLongVersion, ChapterContext oldestBuffer)
    {
        string? newLongSummary = null;
        await Task.Delay(2000);
        System.Diagnostics.Debug.WriteLine("[MemoryManager] 장기 기억 로직 시작.");
        string oldestPlot = oldestBuffer.Plot;
        if (!string.IsNullOrWhiteSpace(oldestPlot) && oldestPlot != "이전 챕터 기록 없음.")
        {
            string systemPrompt = @"
<system_directive>
Role: 롤플레잉 소설의 전문 연대기 필자
Task: <long_term_memory>와 <recent_chapter_state>를 융합해 서사의 뼈대(Spine) 압축 및 갱신
</system_directive>

<critical_constraints>
1. 분량 한계: 공백 포함 최대 1,500자. (초과 시 시점 불문, '서사적 파급력'이 최하위인 사건부터 삭제)
2. 중요도 기반 망각 (Salience Evaporation):
   - 병합/소거: 메인 플롯 영향력이 낮아진 부차적 갈등, 조연 행적은 1문장의 은유로 압축하거나 완전 소거.
   - 영구 보존: 세계관 변화, 중대 분기점, 치명적 관계/감정 변화(Canon Events)는 발생 시점과 무관하게 보존.
3. 무수치 (Zero-Numeric): 재화, 상태창 등 수치 데이터 절대 배제. 오직 사건 흐름만 기록.
4. 문학적 승화: 물리적 충돌 및 육체적 교감은 직설적 묘사를 피하고, 담백하고 은유적인 문학적 산문으로 묘사.
</critical_constraints>

<output_format>
- 기호(1., - 등), 인사말, 부연 설명 절대 금지.
- 오직 <chronicle> 태그 내부에 유려한 산문(Prose) 형태로만 출력.
</output_format>";

            string prompt = $$"""
<long_term_memory>
{{_longTermSummary}}
</long_term_memory>

<recent_chapter_state>
{{oldestPlot}}
</recent_chapter_state>

<instruction>
위 데이터를 바탕으로 서사의 뼈대를 갱신하여 <chronicle> 태그 내에 즉시 출력하라. 
최신 여부를 불문하고 '절대적 서사 중요도'가 높은 캐논 이벤트와 핵심 인과율만을 선별할 것.
</instruction>
""";

            var longReq = CreateInternalRequest(
                prompt,
                systemPrompt,
                "text/plain",
                null,
                ThinkingLevel.medium
            );

            int retryCount = 0;
            int maxRetries = 4;

            while (retryCount < maxRetries)
            {
                System.Diagnostics.Debug.WriteLine($"[MemoryManager] 장기 요약 시도 중({retryCount}/{maxRetries})");
                try
                {
                    // API 호출 및 로그 출력
                    newLongSummary = await apiService.SendMessageAsync(longReq, ModelTier.Pro);

                    if (!string.IsNullOrWhiteSpace(newLongSummary) && newLongSummary.StartsWith("[System"))
                    {
                        throw new Exception(newLongSummary);
                    }

                    // 성공했다면 루프 탈출
                    if (!string.IsNullOrWhiteSpace(newLongSummary)) break;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    // 503(과부하)이나 429(속도제한) 에러일 때 특히 유효합니다.
                    System.Diagnostics.Debug.WriteLine($"[MemoryManager] 장기 요약 시도 중({retryCount}/{maxRetries}) 에러: {ex.Message}");
                    _ = Task.Run(() => MemoryEventLogger.LogMemoryEventAsync($"[장기 기억] API 통신 오류 (시도 {retryCount}/{maxRetries}): {ex.Message}"));

                    if (retryCount >= maxRetries)
                    {
                        System.Diagnostics.Debug.WriteLine("[MemoryManager] 장기 기억 서사 병합 최종 실패. 챕터 전환을 중단합니다.");
                        _ = Task.Run(() => MemoryEventLogger.LogMemoryEventAsync($"[장기 기억] 서사 병합 3회 시도 최종 실패. 에러: {ex.Message}"));
                        return; // 시도 모두 실패 시 안전하게 종료
                    }

                    // 서버가 숨을 고를 시간을 줍니다. (4,8,16)
                    int delaySeconds = (int)Math.Pow(2, retryCount) * 2;
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                }
            }
        }

        lock (_shortTermMemory)
        {
            if (_longTermVersion != expectedLongVersion) return;

            if (!string.IsNullOrWhiteSpace(newLongSummary))
            {
                _longTermSummary = ParseLongTermSummary(newLongSummary);

                if (_mediumBuffers.Count > 0 && _mediumBuffers.Peek() == oldestBuffer)
                {

                    _mediumBuffers.Dequeue();
                    System.Diagnostics.Debug.WriteLine("[MemoryManager] 장기 병합 완료에 따른 오래된 중기 큐(Queue) 안전 삭제 완료.");
                }

                System.Diagnostics.Debug.WriteLine($"[MemoryManager] 장기 병합 성공 (역사 100자): {_longTermSummary.Substring(0, Math.Min(100, _longTermSummary.Length))}...");
                _ = Task.Run(() => MemoryEventLogger.LogMemoryEventAsync($"[장기 기억] 서사 병합 성공 (역사 100자): {_longTermSummary.Substring(0, Math.Min(100, _longTermSummary.Length))}..."));
            }

            System.Diagnostics.Debug.WriteLine("[MemoryManager] 장기 기억 로직 종료.");
        }
    }

    private string ParseLongTermSummary(string rawResponse)
    {
        var match = ChronicleRegex.Match(rawResponse);

        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        System.Diagnostics.Debug.WriteLine("[MemoryManager] <chronicle> 태그 파싱 실패. Fallback을 시도합니다.");
        return GRC.Helpers.SimpleMarkdownHelper.CleanUpMarkdownFallback(rawResponse);
    }

    private GeminiRequest CreateInternalRequest(string prompt, string systemMsg, string mimeType = "text/plain", object? schema = null, ThinkingLevel thinkingLevel = ThinkingLevel.medium)
    {
        return new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(systemMsg)]),
            Contents: [new Content("user", [new Part(prompt)])],
            SafetySettings: GetSafetySettings(BlockThreshold.BLOCK_NONE),
            GenerationConfig: new GenerationConfig(null, 8192, mimeType, schema, new ThinkingConfig(thinkingLevel))
        );
    }
    public void InjectInitialScenario(string scenarioText)
    {
        lock (_shortTermMemory)
        {
            _shortTermMemory.Insert(0, new ChatMessage("user", $"[초기 상황 설정]\n{scenarioText}", DateTime.Now));
            _shortTermMemory.Insert(1, new ChatMessage("model", "[시스템: 해당 세계관과 초기 상황을 완벽히 인지했습니다. 페르소나를 유지하며 롤플레잉을 대기합니다.]", DateTime.Now));
        }
    }

    public void Clear()
    {
        lock (_shortTermMemory)
        {
            _shortTermVersion++; // 단/중기 요약 무효화
            _longTermVersion++;  // 장기 요약 무효화

            _shortTermMemory.Clear();
            _mediumBuffers.Clear();
            _longTermSummary = "아직 요약된 줄거리가 없습니다.";
            _mediumTermUpdateCount = 0;
            _chapterCount = 1;
            _totalTurnCount = 0;
            _currentContext = new ChapterContext
            {
                Plot = $"[제 {_chapterCount}장] 새로운 이야기가 시작되었습니다."
            };
            Volatile.Write(ref _requiresHighQualityAnchor, 1);
        }
    }

    public void RestoreSession(ChatSession session)
    {
        lock (_shortTermMemory)
        {
            _shortTermVersion++; // 이전 세션의 단/중기 요약 무효화
            _longTermVersion++;  // 이전 세션의 장기 요약 무효화
            _shortTermMemory.Clear();
            _shortTermMemory.AddRange(session.History);
            _currentContext = session.CurrentContext;
            _mediumBuffers = session.PrevContexts != null ? new Queue<ChapterContext>(session.PrevContexts) : new Queue<ChapterContext>();
            _longTermSummary = session.LongTermSummary;
            _mediumTermUpdateCount = session.MediumTermUpdateCount;
            _chapterCount = session.ChapterCount;
            _totalTurnCount = session.TotalTurnCount;
            Volatile.Write(ref _requiresHighQualityAnchor, 1);
        }
    }

    public ChatSession ExportSession(CharacterPreset currentPreset)
    {
        var slimPreset = currentPreset with { Lorebooks = null };
        lock (_shortTermMemory)
        {
            return new ChatSession(
                slimPreset,
                _shortTermMemory.ToList(),
                _currentContext,
                _mediumBuffers.ToList(),
                _longTermSummary,
                _chapterCount,
                _totalTurnCount,
                _mediumTermUpdateCount
            );
        }
    }

    public void DeleteMessage(ChatMessage message)
    {
        lock (_shortTermMemory)
        {
            // 1. 삭제할 메시지의 위치(Index)를 찾습니다.
            int index = _shortTermMemory.FindIndex(m => m.Role == message.Role && m.Text == message.Text && m.Timestamp == message.Timestamp);
            if (index == -1) return; // 없으면 무시

            // 2. 메시지 삭제
            _shortTermMemory.RemoveAt(index);

            // 삭제된 메시지의 인덱스가 압축 청크 사이즈(8)보다 작다면?
            // -> 현재 백그라운드 요약 API가 이 메시지를 포함해서 요약하고 있을 확률이 높음!
            // -> 요약본에 '지워져야 할 메시지'가 섞여 있으므로 버전을 올려서 요약본을 버림.
            if (index < MemoryCompressionChunkSize)
            {
                _shortTermVersion++;
                System.Diagnostics.Debug.WriteLine("[MemoryManager] 압축 대상(과거) 메시지 삭제 감지: 진행 중인 요약 무효화.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[MemoryManager] 최신 메시지 단순 삭제 감지: 요약 작업 유지.");
            }

            if (message.Role == "user")
            {
                _totalTurnCount = Math.Max(0, _totalTurnCount - 1);
            }
        }
    }
    /// <summary>
    /// 방금 완료된 1턴의 대화와 이전 상태 스냅샷만으로 상태창 갱신 전용 경량 API 요청을 생성합니다.
    /// 과거 대화 기록을 주지 않음으로써 맥락 간섭을 원천 차단합니다.
    /// </summary>
    public GeminiRequest BuildStatusRequest(string userAction, string modelNarrative, string statusUpdateGuide, BlockThreshold safetyThreshold)
    {
        // 현재 상태 스냅샷 (이전 턴까지의 누적 상태) — Plot 제외 (스레드 안전 lock 적용)
        string stateSnapshot;
        lock (_shortTermMemory)
        {
            stateSnapshot = _currentContext.ToStatusSnapshotString();
        }

        string systemPrompt = """
<role>상태창 갱신 전문가</role>
<task>방금 완료된 1턴의 대화만을 분석하여 이전 상태 데이터를 정확히 갱신하십시오.</task>

<rules>
1. 오직 <latest_turn>에 묘사된 사건만 반영하십시오. 과거 대화나 추측은 절대 반영 금지.
2. 수치형("100/100" 형태)은 [0~최대치] 범위 이탈 불가(Clamping). 단일 숫자(예: 1000)는 무제한 연산.
3. 아이템: 명시적인 획득/소비 묘사가 <latest_turn>에 존재할 때만 증감.
4. NPC 상태(characterConditionDesc): 부상, 감정 변화 등이 묘사된 경우에만 갱신.
5. 변화가 없는 항목은 이전 값을 그대로 유지.
6. uiBadges의 기존 Key 명칭(예: 호감도, HP 등)을 절대 변형(예: "호감도" -> "호감도(아리아)" 또는 "아리아_호감도")하지 말고, 반드시 <previous_state>에 존재하는 exact Key 명칭을 그대로 사용하여 갱신하십시오. 새로운 인물이나 완전히 다른 스탯을 추적할 때만 새 Key 추가를 허용합니다.[CUSTOM_RULES]
</rules>

<output_format>
반드시 아래 TypeScript 타입을 준수하는 순수 JSON만 출력하십시오.
JSON 외의 텍스트, 설명, 마크다운(``` 등)을 절대 출력하지 마십시오.

interface StatusWindow {
  // 기존 Key는 100% 유지(삭제 금지)하되, 상황에 따라 새로운 Key 추가 허용.
  // Value가 숫자(예: 10, 100/100)면 산술 연산으로 증감.
  // Value가 텍스트면 문맥에 맞게 상태 단어 갱신.
  uiBadges: Record<string, string>;

  // NPC 상태 묘사 (부상, 감정 등)
  // 예시: {"NPC": "오른팔 화상, 두려움"}
  characterConditionDesc: Record<string, string>;

  items: string[];

  // 현재 위치나 환경
  places: string[];
}
</output_format>
""";

        string prompt = $"""
<previous_state>
{stateSnapshot}
</previous_state>

<latest_turn>
[유저 행동] {userAction}
[서사 묘사] {modelNarrative}
</latest_turn>

위 <latest_turn>의 내용만을 바탕으로 <previous_state>를 갱신한 JSON을 즉시 출력하십시오.
""";


        string customRulesBlock = !string.IsNullOrWhiteSpace(statusUpdateGuide)
            ? $"\n7. [상태창 갱신가이드 지침]\n{statusUpdateGuide}"
            : "";

        systemPrompt = systemPrompt.Replace("[CUSTOM_RULES]", customRulesBlock);

        return new GeminiRequest(
            SystemInstruction: new Content("system", [new Part(systemPrompt)]),
            Contents: [new Content("user", [new Part(prompt)])],
            SafetySettings: GetSafetySettings(safetyThreshold),
            GenerationConfig: new GenerationConfig(
                Temperature: null,
                MaxOutputTokens: 8192,
                ResponseMimeType: "application/json",
                ResponseSchema: null,
                ThinkingConfig: new ThinkingConfig(ThinkingLevel.medium)
            )
        );
    }

    private List<SafetySetting> GetSafetySettings(BlockThreshold threshold) => [
        new("HARM_CATEGORY_HARASSMENT", threshold),
        new("HARM_CATEGORY_HATE_SPEECH", threshold),
        new("HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold),
        new("HARM_CATEGORY_DANGEROUS_CONTENT", threshold)
    ];
}

