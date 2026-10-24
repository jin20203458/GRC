using GRC.Helpers;
using GRC.Models;
using System.Text.RegularExpressions;

namespace GRC.Services;

public class MemoryManagerService : IMemoryManagerService
{

    private static readonly Regex ChronicleRegex = new Regex(
        @"<chronicle\s*>(.*?)</chronicle\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking
    );

    private readonly IGeminiApiService _apiService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly ICognitivePromptAssembler _cognitivePromptAssembler;
    private readonly IEventLedgerExtractor _eventLedgerExtractor;
    private readonly IHybridLorebookRetriever _hybridLorebookRetriever;
    private readonly IGatedRAGRetriever _gatedRagRetriever;

    public MemoryManagerService(
        IGeminiApiService apiService,
        IAppSettingsService appSettingsService,
        ICognitivePromptAssembler cognitivePromptAssembler,
        IEventLedgerExtractor eventLedgerExtractor,
        IHybridLorebookRetriever hybridLorebookRetriever,
        IGatedRAGRetriever gatedRagRetriever)
    {
        _apiService = apiService;
        _appSettingsService = appSettingsService;
        _cognitivePromptAssembler = cognitivePromptAssembler;
        _eventLedgerExtractor = eventLedgerExtractor;
        _hybridLorebookRetriever = hybridLorebookRetriever;
        _gatedRagRetriever = gatedRagRetriever;
    }

    public MemoryManagerService(
        IGeminiApiService apiService,
        IAppSettingsService appSettingsService)
        : this(apiService, appSettingsService,
               new CognitivePromptAssembler(),
               new EventLedgerExtractor(apiService),
               new HybridLorebookRetriever(),
               new GatedRAGRetriever())
    {
    }

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
    // 메모리 압축 시 한 번에 처리할 대화 메시지 수 (8개 메시지 = 4턴)
    private const int MemoryCompressionChunkSize = 8;

    // [1단계: 단기 기억] - 최근 대화의 날것(Raw)
    private readonly List<ChatMessage> _shortTermMemory = [];
    private const int ShortTermThreshold = 18;

    // [2단계: 중기 기억] - 단일 사건 불릿 원장 (Single Event Ledger) 및 상태 스냅샷
    private ChapterContext _currentContext = new();
    public ChapterContext CurrentContext => _currentContext;
    private int _mediumTermUpdateCount = 0;
    private const int MediumTermThreshold = 10; // 챕터 전환 임계치 (10회 중기 업데이트 = 40턴 = 80개 메시지 시 챕터 전환)

    // [3단계: 장기 기억] - 전체 서사 요약 (String)
    private string _longTermSummary = "아직 요약된 줄거리가 없습니다.";
    private int _chapterCount = 1;
    private int _totalTurnCount = 0;

    // 현재 활성화된 로어북 객체 컬렉션 (TTS 및 UI 연동용)
    public IReadOnlyList<LorebookEntry> CurrentActiveLorebooks { get; private set; } = [];
    public string CurrentSessionFileName { get; set; } = string.Empty;
    private static string ClampGaugeValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('/'))
            return value;

        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double current) &&
            double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double max) &&
            max > 0 && double.IsFinite(current) && double.IsFinite(max))
        {
            double clamped = Math.Clamp(current, 0, max);
            if (Math.Abs(clamped % 1) < 1e-9 && Math.Abs(max % 1) < 1e-9)
            {
                return $"{(long)clamped}/{(long)max}";
            }
            return $"{clamped.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{max.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }
        return value;
    }

    public void UpdateContextStatus(StatusPayload payload, bool replaceCustomStats = false)
    {
        lock (_shortTermMemory)
        {
            if (payload.CustomStats != null)
            {
                if (replaceCustomStats)
                {
                    _currentContext.CustomStats = payload.CustomStats.ToDictionary(kv => kv.Key, kv => ClampGaugeValue(kv.Value));
                }
                else if (payload.CustomStats.Count > 0)
                {
                    foreach (var kvp in payload.CustomStats)
                    {
                        _currentContext.CustomStats[kvp.Key] = ClampGaugeValue(kvp.Value);
                    }
                }
            }

            if (payload.Chars != null && payload.Chars.Count > 0)
            {
                foreach (var kvp in payload.Chars)
                {
                    _currentContext.Chars[kvp.Key] = kvp.Value;
                }
            }

            if (payload.Items != null)
            {
                _currentContext.Items = new List<string>(payload.Items);
            }

            if (payload.Places != null)
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
                LogMemoryEvent("[단기 기억] 안전 밸브 작동: 요약 장기 실패로 인해 오래된 단기 기억을 강제로 밀어냅니다.");
            }
        }

        int currentCount;
        lock (_shortTermMemory) { currentCount = _shortTermMemory.Count; }

        if (currentCount >= ShortTermThreshold)
        {
            _ = Task.Run(async () => await CompressToMediumTermAsync());
        }

        // 1. 순수 유저 입력 및 인지 컨텍스트(CognitiveContext) 검색 준비
        // (메타 지시문은 CognitiveContext.MetaDirective로 전달되어 독립 태그로 조립되며, 임베딩 쿼리 벡터 희석 방지)
        string currentInputText = userMessage.Text ?? string.Empty;

        // 2. 인지 컨텍스트(CognitiveContext) 검색 및 조립
        // (1) 유저 입력 텍스트 기반 임베딩 비동기 생성 시도 (RAG & 로어북용, 메타 지침에 의한 벡터 희석 방지)
        float[]? queryEmbedding = null;
        if (!string.IsNullOrWhiteSpace(currentInputText))
        {
            try
            {
                queryEmbedding = await _apiService.GetEmbeddingAsync(currentInputText);
            }
            catch
            {
                queryEmbedding = null; // 실패 시 null 유지
            }
        }

        // (2) 하이브리드 로어북 검색 (Sparse 정규식 OR Dense 시맨틱 임베딩, 최대 3,000토큰 동적 패킹, 3턴 잔존 및 상태창 인물 보충)
        List<LorebookEntry> matchedLorebooks = [];
        if (preset.Lorebooks != null && preset.Lorebooks.Count > 0)
        {
            IEnumerable<string>? activeChars = null;
            string? lastModelText = null;
            lock (_shortTermMemory)
            {
                lastModelText = _shortTermMemory
                    .LastOrDefault(m => string.Equals(m.Role, "model", StringComparison.OrdinalIgnoreCase))?.Text;

                if (_currentContext.Chars != null && _currentContext.Chars.Count > 0)
                {
                    // 초장기 세션 퇴장 조연 가비지 컬렉션 및 대명사 구간 서사 보존을 위해
                    // 단기 기억 버퍼 전체(18~25개 메시지) + 최근 사건 팩트 불릿 3개 + 현재 유저 입력을 광역 스캔
                    var recentLedgers = _currentContext.EventLedgers?.TakeLast(3).ToList();

                    activeChars = _currentContext.Chars.Keys
                        .Where(name => !string.IsNullOrWhiteSpace(name) &&
                            (currentInputText.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                             _shortTermMemory.Any(m => m.Text != null && m.Text.Contains(name, StringComparison.OrdinalIgnoreCase)) ||
                             (recentLedgers != null && recentLedgers.Any(l => l.FactBullet.Contains(name, StringComparison.OrdinalIgnoreCase)))))
                        .ToList();
                }
            }

            // queryEmbedding은 현재 유저 입력으로만 유지하여 밀집 벡터 희석을 방지하고,
            // 텍스트 스캔 쿼리에만 직전 model 응답을 결합하여 AI가 먼저 제시한 고유명사 키워드를 즉시 매칭
            string loreScanQuery = string.IsNullOrWhiteSpace(lastModelText)
                ? currentInputText
                : $"{lastModelText}\n{currentInputText}";

            matchedLorebooks = _hybridLorebookRetriever.Retrieve(
                loreScanQuery,
                queryEmbedding,
                preset.Lorebooks,
                activeCharNames: activeChars,
                maxTokenBudget: 3000
            );
        }

        // TTS 및 UI 연동을 위한 최신 활성 로어북 객체 컬렉션 갱신
        CurrentActiveLorebooks = matchedLorebooks;

        // (3) RAG 과거 회상 검색 (상시 노출 15개 밖으로 밀려난 과거 챕터 및 오래된 렛저)
        List<EventLedgerItem> allHistoricalLedgers = [];
        lock (_shortTermMemory)
        {
            // 단일 불릿 원장(_currentContext.EventLedgers)에서 상시 노출 15개 밖으로 밀려난 과거 렛저 수집
            if (_currentContext.EventLedgers != null && _currentContext.EventLedgers.Count > 15)
            {
                allHistoricalLedgers = _currentContext.EventLedgers
                    .Take(_currentContext.EventLedgers.Count - 15)
                    .ToList();
            }
        }

        List<EventLedgerItem> recalledLedgers = [];
        if (queryEmbedding != null && queryEmbedding.Length == 768)
        {
            recalledLedgers = _gatedRagRetriever.RetrieveRecalledHistory(
                queryEmbedding,
                allHistoricalLedgers,
                maxResults: 2
            );
        }

        // (4) 인물 상태 및 수치 대비 (BiTemporalEntityProjector) 및 상태창 스냅샷 원자적 획득
        List<EntitySlotItem> entitySlots = [];
        Dictionary<string, string>? remainingCustomStats = null;
        List<string>? itemsSnapshot = null;
        List<string>? placesSnapshot = null;
        List<EventLedgerItem> currentChapterLedgers;

        lock (_shortTermMemory)
        {
            var matchedStatKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_currentContext.Chars != null && _currentContext.Chars.Count > 0)
            {
                foreach (var kvp in _currentContext.Chars)
                {
                    string statValue = "";
                    if (_currentContext.CustomStats != null)
                    {
                        if (_currentContext.CustomStats.TryGetValue(kvp.Key, out var sv))
                        {
                            statValue = sv;
                            matchedStatKeys.Add(kvp.Key);
                        }
                        else
                        {
                            string trimmedKey = kvp.Key.Trim();
                            var candidatePrefixes = new List<string> { trimmedKey + " " };

                            // 풀네임(공백 포함) 인물인 경우 첫 어절(예: "아리아 폰 블랑셰" -> "아리아 ")도 고유할 시 후보에 포함
                            if (trimmedKey.Contains(' '))
                            {
                                string firstToken = trimmedKey.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                                if (firstToken.Length >= 2)
                                {
                                    bool isUnique = !_currentContext.Chars.Keys.Any(otherKey =>
                                        !string.Equals(otherKey, kvp.Key, StringComparison.OrdinalIgnoreCase) &&
                                        otherKey.Trim().StartsWith(firstToken, StringComparison.OrdinalIgnoreCase));

                                    if (isUnique)
                                    {
                                        candidatePrefixes.Add(firstToken + " ");
                                    }
                                }
                            }

                            var prefixMatches = new List<string>();
                            foreach (var statKvp in _currentContext.CustomStats)
                            {
                                if (matchedStatKeys.Contains(statKvp.Key)) continue;

                                foreach (var pfx in candidatePrefixes)
                                {
                                    if (statKvp.Key.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                                    {
                                        string subLabel = statKvp.Key[pfx.Length..].Trim();
                                        prefixMatches.Add(string.IsNullOrEmpty(subLabel) ? statKvp.Value : $"{subLabel}: {statKvp.Value}");
                                        matchedStatKeys.Add(statKvp.Key);
                                        break;
                                    }
                                }
                            }
                            if (prefixMatches.Count > 0)
                            {
                                statValue = string.Join(", ", prefixMatches);
                            }
                        }
                    }

                    entitySlots.Add(new EntitySlotItem(
                        Name: kvp.Key,
                        ConditionDesc: kvp.Value,
                        StatValue: statValue
                    ));
                }
            }

            // 인물 엔티티 슬롯에 이미 바인딩된 스탯은 제외하여 <current_state> 내 중복 출력 방지
            if (_currentContext.CustomStats != null)
            {
                remainingCustomStats = _currentContext.CustomStats
                    .Where(kv => !matchedStatKeys.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            itemsSnapshot = _currentContext.Items != null ? new List<string>(_currentContext.Items) : null;
            placesSnapshot = _currentContext.Places != null ? new List<string>(_currentContext.Places) : null;
            currentChapterLedgers = _currentContext.EventLedgers != null
                ? _currentContext.EventLedgers.ToList()
                : [];
        }

        var cognitiveContext = new CognitiveContext(
            MacroChronicle: _longTermSummary,
            LedgerFacts: currentChapterLedgers,
            Entities: entitySlots,
            RetrievedLores: matchedLorebooks,
            RecalledHistory: recalledLedgers,
            CurrentChapter: _chapterCount,
            MetaDirective: metaDirective,
            CustomStats: remainingCustomStats,
            Items: itemsSnapshot,
            Places: placesSnapshot
        );

        // 3. CognitivePromptAssembler 동기 호출 (불변 SystemInstruction + 최신 유저 직전 Dynamic Context)
        List<ChatMessage> shortTermSnapshot;
        lock (_shortTermMemory)
        {
            shortTermSnapshot = _shortTermMemory.ToList();
        }

        var assembledRequest = _cognitivePromptAssembler.AssemblePrompt(
            preset,
            shortTermSnapshot,
            userMessage,
            cognitiveContext
        );

        // 4. GenerationConfig & SafetySettings 재부착 (Thinking Anchor 및 모델 파라미터 보존)
        var settings = await _appSettingsService.LoadSettingsAsync();
        var request = assembledRequest with
        {
            SafetySettings = GetSafetySettings(settings.SafetyThreshold),
            GenerationConfig = new GenerationConfig(
                preset.Temperature,
                preset.MaxOutputTokens,
                "text/plain",
                null,
                new ThinkingConfig(ThinkingLevel.medium)
            )
        };

        return request;
    }

    /// <summary>
    /// 18턴 도달 후 중기 기억 압축 전 대화 안정화 대기 시간 (ms, 기본값: 3000ms, 단위 테스트 시 단축 가능)
    /// </summary>
    public int CompressionDelayMs { get; set; } = 3000;

    public void AddModelResponse(ChatMessage message)
    {
        lock (_shortTermMemory)
        {
            _shortTermMemory.Add(message);
        }
    }

    private async Task CompressToMediumTermAsync()
    {
        if (CompressionDelayMs > 0)
        {
            await Task.Delay(CompressionDelayMs);
        }
        if (!_summarizeLock.Wait(0)) return;
        System.Diagnostics.Debug.WriteLine("[MemoryManager] V3 중기 사건 렛저 추출 시작");
        try
        {
            List<ChatMessage> targetToSummarize;
            long expectedVersion;

            lock (_shortTermMemory)
            {
                if (_shortTermMemory.Count < MemoryCompressionChunkSize) return;

                targetToSummarize = _shortTermMemory.Take(MemoryCompressionChunkSize).ToList();
                expectedVersion = _shortTermVersion;
            }

            // V3: EventLedgerExtractor를 통한 4턴(8개 메시지) 불변 팩트 불릿 추출 (Gemini 3.5 Flash-Lite)
            var extractedBullets = await _eventLedgerExtractor.ExtractLedgerFactsAsync(targetToSummarize, _chapterCount);

            ChapterContext? targetBufferForLongTerm = null;
            lock (_shortTermMemory)
            {
                if (_shortTermVersion != expectedVersion) return;

                if (extractedBullets != null && extractedBullets.Count > 0)
                {
                    _currentContext.EventLedgers.AddRange(extractedBullets);
                }

                // 현재 챕터 불릿들을 타임라인 문자열로 조합하여 Plot에 동기화 (서사 열람 UI 표시 및 80턴 장기 연대기 압축 입력용)
                var currentChapterBullets = _currentContext.EventLedgers
                    .Where(l => l.Chapter == _chapterCount)
                    .Select(l => $"* {l.FactBullet}");
                string formattedPlot = $"[제 {_chapterCount}장]\n" + string.Join("\n", currentChapterBullets);
                _currentContext.Plot = formattedPlot;

                if (_shortTermMemory.Count >= MemoryCompressionChunkSize)
                {
                    _shortTermMemory.RemoveRange(0, MemoryCompressionChunkSize);
                    Volatile.Write(ref _requiresHighQualityAnchor, 1);
                }
                _mediumTermUpdateCount++;

                System.Diagnostics.Debug.WriteLine($"[MemoryManager] 중기 렛저 누적 성공 (총 {_currentContext.EventLedgers.Count}개 불릿)");
                LogMemoryEvent($"[중기 기억] 렛저 누적 성공 (총 {_currentContext.EventLedgers.Count}개 불릿): {_currentContext.Plot.Substring(0, Math.Min(100, _currentContext.Plot.Length))}...");

                // 중기 챕터가 임계치에 도달하면 즉시 챕터 전환
                if (_mediumTermUpdateCount >= MediumTermThreshold)
                {
                    System.Diagnostics.Debug.WriteLine("[MemoryManager] 중기 임계치 도달: 현재 챕터를 마감하고 새 챕터로 전환합니다.");
                    LogMemoryEvent("[중기 기억] 임계치 도달: 챕터 마감 및 새 챕터 전환");

                    // 1. 현재 챕터 스냅샷 보존 (장기 연대기 압축용 Plot 전달)
                    var previousContext = _currentContext;

                    // 2. 새 챕터로 전환 (최신 상태 데이터 상속 및 단일 원장 불릿 승계)
                    _chapterCount++;
                    Volatile.Write(ref _isChapterChanged, 1);
                    _currentContext = new ChapterContext
                    {
                        Plot = $"[제 {_chapterCount}장]\n(새로운 챕터의 사건이 기록되는 중입니다.)",
                        CustomStats = new Dictionary<string, string>(previousContext.CustomStats),
                        Items = new List<string>(previousContext.Items),
                        Chars = new Dictionary<string, string>(previousContext.Chars),
                        Places = new List<string>(previousContext.Places),
                        EventLedgers = new List<EventLedgerItem>(previousContext.EventLedgers) // 단일 불릿 원장 무손실 승계!
                    };

                    _mediumTermUpdateCount = 0; // 카운트 초기화
                    Volatile.Write(ref _requiresHighQualityAnchor, 1);

                    // 3. 챕터 전환 즉시 장기 연대기(Pro 모델) 요약 병합 트리거
                    targetBufferForLongTerm = previousContext;
                }
            }

            if (targetBufferForLongTerm != null)
            {
                long expectedLongVersion = _longTermVersion;
                _ = Task.Run(async () => await CompressToLongTermAsync(expectedLongVersion, targetBufferForLongTerm));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemoryManager] 중기 서사 압축 실패: {ex.Message}");
            LogMemoryEvent($"[중기 기억] 압축 실패 오류: {ex.Message}");
        }
        finally
        {
            System.Diagnostics.Debug.WriteLine("[MemoryManager] 중기 서사 요약 로직 종료");
            _summarizeLock.Release();
        }
    }

    private async Task CompressToLongTermAsync(long expectedLongVersion, ChapterContext oldestBuffer)
    {
        string? newLongSummary = null;
        await Task.Delay(2000);
        System.Diagnostics.Debug.WriteLine("[MemoryManager] 장기 기억 로직 시작.");
        string oldestPlot = oldestBuffer.Plot;
        if (!string.IsNullOrWhiteSpace(oldestPlot))
        {
            string systemPrompt = """
<system_directive>
Role: 롤플레잉 소설의 전문 연대기 필자
Task: <long_term_memory>와 <recent_chapter_state>를 융합해 서사의 뼈대(Spine) 압축 및 갱신
</system_directive>

<critical_constraints>
1. 분량 한계: 공백 포함 최대 1,500자. (초과 시 시점 불문, '서사적 파급력'이 최하위인 사건부터 삭제)
2. 중요도 기반 망각 (Salience Evaporation):
   - 병합/소거: 메인 플롯 영향력이 낮아진 부차적 갈등, 조연 행적은 1문장의 은유로 압축하거나 완전 소거.
   - 영구 보존: 세계관 변화, 중대 분기점, 치명적 관계/감정 변화(Canon Events)는 발생 시점과 무관하게 보존.
3. 문학적 승화: 물리적 충돌 및 육체적 교감은 직설적 묘사를 피하고, 담백하고 은유적인 문학적 산문으로 묘사.
</critical_constraints>

<output_format>
1. 개조식 기호(숫자, 글머리 기호) 및 메타 설명을 일절 배제하고, 소설 본문 형태의 유려한 산문(Prose) 단락으로만 작성하십시오.
2. 최종 출력 전문을 반드시 <chronicle>과 </chronicle> 태그로 감싸서 반환하십시오.
</output_format>
""";

            string prompt = $$"""
<long_term_memory>
{{_longTermSummary}}
</long_term_memory>

<recent_chapter_state>
{{oldestPlot}}
</recent_chapter_state>

<final_instruction>
위 데이터를 바탕으로 서사의 뼈대를 갱신하여 <chronicle> 태그 내에 즉시 출력하십시오.
- [사고 및 검증 기준]: 기존 캐논 이벤트와 새 챕터 사건의 인과율을 대조 평가하고, 공백 포함 1,500자 이내의 단일 유려한 서사로 온전히 수렴되었는지 내부적으로 자체 검증하십시오.
</final_instruction>
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
                    newLongSummary = await _apiService.SendMessageAsync(longReq, ModelTier.Pro);

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
                    LogMemoryEvent($"[장기 기억] API 통신 오류 (시도 {retryCount}/{maxRetries}): {ex.Message}");

                    if (retryCount >= maxRetries)
                    {
                        System.Diagnostics.Debug.WriteLine("[MemoryManager] 장기 기억 서사 병합 최종 실패. 챕터 전환을 중단합니다.");
                        LogMemoryEvent($"[장기 기억] 서사 병합 3회 시도 최종 실패. 에러: {ex.Message}");
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



                System.Diagnostics.Debug.WriteLine($"[MemoryManager] 장기 병합 성공 (역사 100자): {_longTermSummary.Substring(0, Math.Min(100, _longTermSummary.Length))}...");
                LogMemoryEvent($"[장기 기억] 서사 병합 성공 (역사 100자): {_longTermSummary.Substring(0, Math.Min(100, _longTermSummary.Length))}...");
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
        if (string.IsNullOrWhiteSpace(scenarioText)) return;
        lock (_shortTermMemory)
        {
            _shortTermMemory.Insert(0, new ChatMessage("user", $"<initial_scenario>\n{scenarioText.Trim()}\n</initial_scenario>", DateTime.Now));
            _shortTermMemory.Insert(1, new ChatMessage("model", "주어진 세계관과 초기 상황의 흐름을 받아들여, 첫 발걸음을 뗄 준비를 마쳤습니다.", DateTime.Now));
        }
    }

    public void Clear()
    {
        lock (_shortTermMemory)
        {
            _shortTermVersion++; // 단/중기 요약 무효화
            _longTermVersion++;  // 장기 요약 무효화

            _shortTermMemory.Clear();
            _longTermSummary = "아직 요약된 줄거리가 없습니다.";
            _mediumTermUpdateCount = 0;
            _chapterCount = 1;
            _totalTurnCount = 0;
            _currentContext = new ChapterContext
            {
                Plot = $"[제 {_chapterCount}장] 새로운 이야기가 시작되었습니다."
            };
            _hybridLorebookRetriever.ClearSticky();
            CurrentActiveLorebooks = [];
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
            _currentContext = session.CurrentContext ?? new ChapterContext();
            _longTermSummary = session.LongTermSummary;
            _mediumTermUpdateCount = session.MediumTermUpdateCount;
            _chapterCount = session.ChapterCount;
            _totalTurnCount = session.TotalTurnCount;
            _hybridLorebookRetriever.ClearSticky();
            CurrentActiveLorebooks = [];

            // 대화 내역이 없는 신규 세션일 때만 앵커를 활성화하고, 기존 대화가 이어지는 세션 로드 시에는 불필요한 과잉 추론 방지
            int anchorRequired = (session.History == null || session.History.Count == 0) ? 1 : 0;
            Volatile.Write(ref _requiresHighQualityAnchor, anchorRequired);
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
                _longTermSummary,
                _chapterCount,
                _totalTurnCount,
                _mediumTermUpdateCount
            );
        }
    }

    public IReadOnlyList<ChatMessage> GetCurrentShortTermMemory()
    {
        lock (_shortTermMemory)
        {
            return _shortTermMemory.ToList();
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
<system_directive>
Domain: 텍스트 롤플레잉 게임의 상태 및 인벤토리 추적 엔진
Task: 방금 완료된 1턴의 대화만을 분석하여 이전 상태 데이터를 정확히 갱신하십시오.
</system_directive>

<rules>
1. 오직 <latest_turn>에 묘사된 사건만을 근거로 삼으십시오.
2. 수치형("100/100" 형태)은 0부터 최대치 범위(0~최대치)를 유지하십시오. 단일 정수(예: 1000)는 산술 연산으로 증감하십시오.
3. 아이템(items): 명시적인 획득 또는 소비 묘사가 존재할 때만 목록을 증감하십시오.
4. 인물 상태(characterConditionDesc): 부상, 감정 변화 등이 묘사된 경우에만 갱신하십시오.
5. 변화가 없는 항목은 이전 값을 그대로 유지하십시오.
6. uiBadges의 기존 Key는 <previous_state>의 명칭을 정확히 유지(Exact Match)하고, 새로운 동료나 스탯 추적이 필요할 때만 새 Key를 추가하십시오.
</rules>[CUSTOM_RULES]

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

  // 현재 소지품
  items: string[];

  // 현재 위치
  places: string[];
}
</output_format>
""";

        string prompt = $"""
<previous_state>
{stateSnapshot}
</previous_state>

<latest_turn>
<user_action>{userAction}</user_action>
<narrative_description>{modelNarrative}</narrative_description>
</latest_turn>

<final_instruction>
위 <latest_turn>의 내용만을 바탕으로 <previous_state>를 갱신한 JSON을 즉시 출력하십시오.
</final_instruction>
""";


        string customRulesBlock = !string.IsNullOrWhiteSpace(statusUpdateGuide)
            ? $"\n\n<custom_rules>\n{statusUpdateGuide.Trim()}\n</custom_rules>"
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
                ThinkingConfig: new ThinkingConfig(ThinkingLevel.low)
            )
        );
    }

    private List<SafetySetting> GetSafetySettings(BlockThreshold threshold) => [
        new("HARM_CATEGORY_HARASSMENT", threshold),
        new("HARM_CATEGORY_HATE_SPEECH", threshold),
        new("HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold),
        new("HARM_CATEGORY_DANGEROUS_CONTENT", threshold)
    ];

    private void LogMemoryEvent(string message) =>
        _ = Task.Run(() => MemoryEventLogger.LogMemoryEventAsync(message, CurrentSessionFileName));
}
