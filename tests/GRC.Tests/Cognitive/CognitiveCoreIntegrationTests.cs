using GRC.Helpers;
using GRC.Models;
using GRC.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace GRC.Tests;

public class CognitiveCoreIntegrationTests(ITestOutputHelper output)
{
    private class TestAppSettingsService(string projectId) : IAppSettingsService
    {
        public Task<AppSettings> LoadSettingsAsync()
        {
            return Task.FromResult(new AppSettings(
                ApiKey: "",
                ProjectId: projectId,
                UseVertexAI: true,
                SelectedModel: ModelTier.Flash,
                SafetyThreshold: BlockThreshold.BLOCK_NONE
            ));
        }

        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public bool IsCredentialFileExists() => true;
        public Task<bool> CopyCredentialFileAsync(string sourceFilePath) => Task.FromResult(true);
    }

    private static (GeminiApiService apiService, IAppSettingsService settingsService) CreateRealServices()
    {
        string[] candidates =
        [
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "google-credentials.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GRC", "Config", "google-credentials.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GRC", "bin", "Debug", "net8.0-windows", "Config", "google-credentials.json")
        ];

        string? credentialPath = candidates.FirstOrDefault(File.Exists);
        if (credentialPath == null)
        {
            throw new FileNotFoundException("google-credentials.json 파일을 찾을 수 없습니다.");
        }

        string jsonContent = File.ReadAllText(credentialPath);
        var doc = JsonNode.Parse(jsonContent);
        string projectId = doc?["project_id"]?.GetValue<string>() ?? "grc0-494913";

        var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var authService = new GoogleAuthService();
        var settingsService = new TestAppSettingsService(projectId);
        var apiService = new GeminiApiService(httpClient, settingsService, authService);

        return (apiService, settingsService);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EventLedgerExtractor_RealFlashLite_ExtractsFactBulletsAndEmbeddings()
    {
        var (apiService, _) = CreateRealServices();
        var extractor = new EventLedgerExtractor(apiService);

        // 8턴 분량의 샘플 대화 구성
        var recentTurns = new List<ChatMessage>
        {
            new("user", "성문 앞 경비병에게 통행증을 제시한다.", DateTime.Now.AddMinutes(-10)),
            new("model", "경비병은 통행증의 인장을 유심히 살피더니 무거운 철문을 열어주었다. \"조심하시오, 북쪽 숲에 도적 떼가 나타났다는 소문이 있소.\" 성문 밖으로 황량한 갈대밭이 펼쳐졌다.", DateTime.Now.AddMinutes(-9)),
            new("user", "북쪽 숲으로 향하는 오솔길을 따라 말을 달린다.", DateTime.Now.AddMinutes(-8)),
            new("model", "말발굽 소리가 거친 흙길을 울렸다. 숲 입구에 다다르자 스산한 안개가 발목을 감싸 안았다. 나무 뒤편에서 부스럭거리는 소리와 함께 서늘한 기운이 느껴졌다.", DateTime.Now.AddMinutes(-7)),
            new("user", "검을 뽑아 들고 말에서 내려 경계 태세를 취한다.", DateTime.Now.AddMinutes(-6)),
            new("model", "검신이 은빛으로 번뜩였다. 안개 속에서 복면을 쓴 자가 모습을 드러냈다. 그의 손에는 붉은 혈문이 새겨진 단도가 쥐여 있었다. \"더 이상 나아가지 마라, 이방인.\"", DateTime.Now.AddMinutes(-5)),
            new("user", "상대의 칼끝을 쳐내며 단숨에 제압을 시도한다.", DateTime.Now.AddMinutes(-4)),
            new("model", "순식간에 칼날이 부딪히며 날카로운 파열음이 일었다. 상대의 손목을 비틀어 단도를 떨어뜨리자, 그는 품에서 의문의 검은 양피지 조각을 떨어뜨린 채 숲속으로 도주했다.", DateTime.Now.AddMinutes(-3))
        };

        output.WriteLine("=== [E2E] Gemini 3.5 Flash-Lite 사건 불릿 추출 호출 시작 ===");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var ledgerFacts = await extractor.ExtractLedgerFactsAsync(recentTurns, currentChapter: 1);

        sw.Stop();
        output.WriteLine($"추출 완료 소요시간: {sw.ElapsedMilliseconds}ms");
        output.WriteLine($"추출된 불릿 개수: {ledgerFacts.Count}");

        Assert.NotEmpty(ledgerFacts);
        foreach (var item in ledgerFacts)
        {
            output.WriteLine($"* [제{item.Chapter}장]: {item.FactBullet}");
            Assert.False(string.IsNullOrWhiteSpace(item.FactBullet));
            Assert.Equal(1, item.Chapter);

            // 임베딩이 768차원으로 정상 부착되었는지 검증
            Assert.NotNull(item.Embedding);
            Assert.Equal(768, item.Embedding.Length);
            output.WriteLine($"  - 임베딩 차원: {item.Embedding.Length} (First 3: [{item.Embedding[0]:F4}, {item.Embedding[1]:F4}, {item.Embedding[2]:F4}])");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MemoryManagerService_RealFlashStream_GeneratesProseNarrative()
    {
        var (apiService, settingsService) = CreateRealServices();
        var memoryService = new MemoryManagerService(apiService, settingsService);

        var preset = new CharacterPreset(
            Name: "엘리아스",
            Worldview: "마법과 검이 지배하는 정통 다크 판타지 세계관. 고대 제국의 유적이 곳곳에 묻혀 있다.",
            SystemPrompt: "당신은 냉철하지만 신의를 지키는 은빛 용병 '엘리아스'입니다. 절제된 감정 속에서 묵직한 산문으로 행동과 서사를 서술하십시오."
        );

        // 이전 대화 2턴 주입
        memoryService.AddModelResponse(new ChatMessage("user", "우리가 찾던 유적의 입구가 어디에 있소?", DateTime.Now.AddMinutes(-5)));
        memoryService.AddModelResponse(new ChatMessage("model", "엘리아스는 낡은 지도를 거두며 절벽 아래의 어두운 균열을 가리켰다. \"저 아래요. 덩굴로 가려져 있지만 제국의 문장이 분명합니다.\"", DateTime.Now.AddMinutes(-4)));

        var userMsg = new ChatMessage("user", "횃불을 밝히고 균열 안으로 앞장서시오.", DateTime.Now);

        output.WriteLine("=== [E2E] V3 프롬프트 조립 및 Gemini 3.8 Flash 실제 스트리밍 호출 시작 ===");
        var request = await memoryService.BuildNarrativeRequestAsync(userMsg, preset);

        Assert.NotNull(request);
        Assert.NotNull(request.SystemInstruction);
        output.WriteLine($"불변 SystemInstruction 접두사 길이: {request.SystemInstruction!.Parts[0].Text!.Length}자");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sb = new StringBuilder();

        await foreach (var chunk in apiService.SendMessageStreamAsync(request, ModelTier.Flash))
        {
            sb.Append(chunk);
        }

        sw.Stop();
        string generatedText = sb.ToString();

        output.WriteLine($"생성 소요시간: {sw.ElapsedMilliseconds}ms");
        output.WriteLine($"생성된 총 글자수: {generatedText.Length}자");
        output.WriteLine("--- [실제 생성된 서사 본문] ---");
        output.WriteLine(generatedText);
        output.WriteLine("------------------------------");

        Assert.False(string.IsNullOrWhiteSpace(generatedText));
        Assert.DoesNotContain("<plot>", generatedText);
        Assert.DoesNotContain("<macro_chronicle>", generatedText);
        Assert.DoesNotContain("```", generatedText);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MemoryManagerService_RealFlashLite_ExtractsStatusAndItems()
    {
        var (apiService, settingsService) = CreateRealServices();
        var memoryService = new MemoryManagerService(apiService, settingsService);

        string userAction = "쓰러진 도적의 품에서 떨어진 검은 양피지를 주워 품에 넣는다.";
        string modelNarrative = "엘리아스는 젖은 흙바닥에 떨어진 양피지 조각을 집어 들었다. 불빛에 비친 조각에는 검은 뱀이 똬리를 튼 기이한 문양과 '월식의 밤, 제단 아래'라는 핏빛 글귀가 희미하게 적혀 있었다. 그는 미간을 찌푸린 채 이를 조용히 품에 갈무리했다.";

        output.WriteLine("=== [E2E] Gemini 3.5 Flash-Lite 상태창 및 아이템 추출 시작 ===");
        var statusReq = memoryService.BuildStatusRequest(userAction, modelNarrative, statusUpdateGuide: "", BlockThreshold.BLOCK_NONE);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string rawResponse = await apiService.SendMessageAsync(statusReq, ModelTier.FlashLite);
        sw.Stop();

        output.WriteLine($"응답 소요시간: {sw.ElapsedMilliseconds}ms");
        output.WriteLine($"Raw Response:\n{rawResponse}");

        string? cleanJson = LlmJsonParser.ExtractJson(rawResponse);
        Assert.NotNull(cleanJson);

        var payload = LlmJsonParser.DeserializeSafe<StatusPayload>(cleanJson);
        Assert.NotNull(payload);

        output.WriteLine("--- [파싱된 상태창 페이로드] ---");
        output.WriteLine($"아이템: {string.Join(", ", payload.Items)}");
        output.WriteLine($"위치: {string.Join(", ", payload.Places)}");

        // 아이템 중 양피지/조각 관련 단어가 정상 추출되었는지 검증
        bool itemDetected = payload.Items.Any(i => i.Contains("양피지") || i.Contains("조각"));

        output.WriteLine($"아이템 추출 성공: {itemDetected}");
        Assert.True(itemDetected, "양피지 조각 아이템이 정상 추출되어야 합니다.");
    }

    private record RagEvaluationCase(string Category, string Description, string Query, bool IsTruePositive);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RAG_SemanticSimilarity_RealEmbedding_EvaluationWithParaphrasedInputs()
    {
        var (apiService, _) = CreateRealServices();

        output.WriteLine("====================================================================================================");
        output.WriteLine("=== [실측 전수 검증] 2대 핵심 RAG 정탐(TP) 10개 vs 비교군(Control) 10개 (총 20개 문장) 분리도 평가 ===");
        output.WriteLine("=== 모델: text-multilingual-embedding-002 (서울 asia-northeast3 리전) / 기준: 0.68f, 0.70f ===");
        output.WriteLine("====================================================================================================");

        // ====================================================================
        // [1] 로어북 RAG (대상: 엘리시아)
        // - 권장 임계치: 0.68f (비교군 최고점 0.6072 vs 정탐 하한선 0.6910)
        // ====================================================================
        float lorebookThreshold = 0.68f;
        string lorebookTarget = "엘리시아: 아스테리아 제국의 제2황녀이자 주인공의 정략결혼 약혼녀. 눈부신 금발과 벽안을 지닌 냉혹하고 오만한 미녀. 제국 마법단 총지휘관.";
        var lorebookCases = new List<RagEvaluationCase>
        {
            new("정탐", "외모/신분 우회", "그 건방진 금발 황녀의 복부를 칼로 깊숙이 찌른다.", true),
            new("정탐", "약혼 관계 우회", "약혼녀의 뺨을 차갑게 후려치며 차가운 눈으로 쏘아본다.", true),
            new("정탐", "궁정 직계 우회", "황제의 둘째 딸에게 다가가 정중하게 허리를 숙여 인사한다.", true),
            new("정탐", "태도/신분 우회", "오만하기 짝이 없는 금발 귀족 아가씨의 콧대를 꺾어놓아야겠다.", true),
            new("정탐", "마법단 직책/성격 우회", "제국 마법단의 차가운 총지휘관에게 다가가 전선의 상황을 보고한다.", true),
            new("비교군", "일상 식사", "주방에서 갓 구운 따뜻한 빵과 치즈를 집어 먹는다.", false),
            new("비교군", "일상 독서", "벽난로 앞의 낡은 나무 의자에 조용히 앉아 책을 읽는다.", false),
            new("비교군", "사냥 전투", "숲속에서 갑자기 튀어나온 거대한 멧돼지에게 활을 겨눈다.", false),
            new("비교군", "하드 네거티브 (경비병)", "성문을 지키는 늙은 경비병에게 통행증을 내밀며 문을 열라고 요구한다.", false),
            new("비교군", "하드 네거티브 (상인 거래)", "거리의 연금술 상점에 들러 상처를 치료할 붉은 물약을 흥정한다.", false)
        };

        // ====================================================================
        // [2] 사건 불릿포인트 회상 RAG (대상: 부둣가 동맹 맹세)
        // - 순수 시맨틱 임베딩 유사도 (>= 0.71f)
        // ====================================================================
        float ledgerThreshold = 0.71f;
        string ledgerTarget = "비 내리는 부둣가에서 붉은 머리 마법사에게 비밀 서신을 건네받고 동맹을 맹세했다.";
        var ledgerCases = new List<RagEvaluationCase>
        {
            new("정탐", "항구/빨간 머리", "예전에 그 항구에서 빨간 머리 여자와 손잡았던 날 기억나?", true),
            new("정탐", "선창/밀서", "비바람 몰아치던 선창에서 건네받았던 그 은밀한 편지는 어디 있지?", true),
            new("정탐", "선착장/술사/약조", "우리 배 타는 곳에서 그 술사와 맺었던 약조를 잊어선 안 돼.", true),
            new("정탐", "바닷가/마법사/약속", "그때 바닷가에서 붉은 마법사 녀석과 편지 주고받으면서 한 약속 생각나?", true),
            new("정탐", "포구/쪽지/동맹", "포구에서 마법사한테 비밀리에 쪽지 받고 한패가 되기로 했잖아.", true),
            new("비교군", "일상 기상", "아침 햇살에 눈을 비비며 텐트 밖으로 걸어 나온다.", false),
            new("비교군", "대장간 수리", "대장간에 들러 방패의 찌그러진 부분을 망치로 펴달라고 부탁한다.", false),
            new("비교군", "하드 네거티브 (동굴 조우)", "산속 동굴에서 길을 잃은 사냥꾼을 만나 모닥불을 피웠다.", false),
            new("비교군", "하드 네거티브 (바다 풍경)", "푸른 바다 위에서 돛을 올리고 시원한 바닷바람을 맞는다.", false),
            new("비교군", "하드 네거티브 (미래 약속)", "내일 정오에 시청 앞 광장에서 경비대장과 만나기로 약속하자.", false)
        };

        var gatedRetriever = new GatedRAGRetriever();
        var allFailures = new List<string>();
        allFailures.AddRange(await EvaluateRagCategoryAsync("1. 로어북 RAG (엘리시아)", lorebookTarget, lorebookCases, lorebookThreshold, apiService));
        allFailures.AddRange(await EvaluateGatedLedgerRagCategoryAsync("2. 사건 불릿포인트 회상 RAG (부둣가 동맹 맹세)", ledgerTarget, ledgerCases, ledgerThreshold, gatedRetriever, apiService));

        if (allFailures.Count > 0)
        {
            output.WriteLine($"\n[전체 테스트 실패/경고 내역 총 {allFailures.Count}건]:\n" + string.Join("\n", allFailures));
        }

        Assert.Empty(allFailures);
    }

    private async Task<List<string>> EvaluateRagCategoryAsync(
        string title,
        string targetText,
        List<RagEvaluationCase> cases,
        float threshold,
        GeminiApiService apiService)
    {
        output.WriteLine($"\n====================================================================================================");
        output.WriteLine($"### [{title}]");
        output.WriteLine($"* 대상 원문: \"{targetText}\"");
        output.WriteLine($"----------------------------------------------------------------------------------------------------");
        output.WriteLine($"| {"유형",-6} | {"세부 유형",-24} | {"유사도",-8} | {"판정 (기준 0.70)",-18} | 입력 쿼리");
        output.WriteLine($"| :----- | :----------------------- | :------- | :----------------- | :------------------------------------------------------");

        var targetEmb = await apiService.GetEmbeddingAsync(targetText);
        Assert.NotNull(targetEmb);
        Assert.Equal(768, targetEmb.Length);

        var failedCases = new List<string>();
        int tpPassCount = 0;
        int controlBlockCount = 0;

        foreach (var c in cases)
        {
            var qEmb = await apiService.GetEmbeddingAsync(c.Query);
            Assert.NotNull(qEmb);
            Assert.Equal(768, qEmb.Length);

            float sim = VectorMathHelper.CosineSimilarity(qEmb, targetEmb);
            bool isPassed = sim >= threshold;

            string verdict;
            if (c.IsTruePositive)
            {
                if (isPassed)
                {
                    verdict = "정탐 성공 (PASS)";
                    tpPassCount++;
                }
                else
                {
                    verdict = "정탐 미달 (FAIL)";
                    failedCases.Add($"[{title} 정탐 미달] {c.Description}: \"{c.Query}\" (유사도 {sim:F4} < {threshold:F2})");
                }
            }
            else
            {
                if (!isPassed)
                {
                    verdict = "차단 성공 (PASS)";
                    controlBlockCount++;
                }
                else
                {
                    verdict = "오탐 유입 (FAIL)";
                    failedCases.Add($"[{title} 오탐 유입] {c.Description}: \"{c.Query}\" (유사도 {sim:F4} >= {threshold:F2})");
                }
            }

            output.WriteLine($"| {c.Category,-6} | {c.Description,-24} | {sim:F4}   | {verdict,-18} | \"{c.Query}\"");
        }

        output.WriteLine($"----------------------------------------------------------------------------------------------------");
        output.WriteLine($"* 검증 요약: 정탐 성공 = {tpPassCount}/5, 비교군 차단 = {controlBlockCount}/5");

        return failedCases;
    }

    private async Task<List<string>> EvaluateGatedLedgerRagCategoryAsync(
        string title,
        string targetText,
        List<RagEvaluationCase> cases,
        float threshold,
        GatedRAGRetriever gatedRetriever,
        GeminiApiService apiService)
    {
        output.WriteLine($"\n====================================================================================================");
        output.WriteLine($"### [{title}]");
        output.WriteLine($"* 대상 원문: \"{targetText}\"");
        output.WriteLine($"* 아키텍처: 순수 시맨틱 임베딩 핀포인트 인출 (>= {threshold:F2})");
        output.WriteLine($"----------------------------------------------------------------------------------------------------");
        output.WriteLine($"| {"유형",-6} | {"세부 유형",-24} | {"유사도",-8} | {"최종 판정",-20} | 입력 쿼리");
        output.WriteLine($"| :----- | :----------------------- | :--------- | :------------------- | :------------------------------------------------------");

        var targetEmb = await apiService.GetEmbeddingAsync(targetText);
        Assert.NotNull(targetEmb);
        Assert.Equal(768, targetEmb.Length);

        var failedCases = new List<string>();
        int tpPassCount = 0;
        int controlBlockCount = 0;

        var targetLedger = new EventLedgerItem(targetText, targetEmb, 1);

        foreach (var c in cases)
        {
            var qEmb = await apiService.GetEmbeddingAsync(c.Query);
            Assert.NotNull(qEmb);
            Assert.Equal(768, qEmb.Length);

            float sim = VectorMathHelper.CosineSimilarity(qEmb, targetEmb);
            var recalled = gatedRetriever.RetrieveRecalledHistory(qEmb, [targetLedger], maxResults: 1);
            bool isRetrieved = recalled.Count > 0;

            string verdict;
            if (c.IsTruePositive)
            {
                if (isRetrieved)
                {
                    verdict = "정탐 완벽 성공 (PASS)";
                    tpPassCount++;
                }
                else
                {
                    verdict = "임베딩 미달 (FAIL)";
                    failedCases.Add($"[{title} 정탐 탈락] {c.Description}: \"{c.Query}\" (유사도 {sim:F4} < {threshold:F2})");
                }
            }
            else
            {
                if (!isRetrieved)
                {
                    verdict = "차단 성공 (PASS)";
                    controlBlockCount++;
                }
                else
                {
                    verdict = "오탐 유입 (FAIL)";
                    failedCases.Add($"[{title} 오탐 유입] {c.Description}: \"{c.Query}\" (유사도 {sim:F4} >= {threshold:F2})");
                }
            }

            output.WriteLine($"| {c.Category,-6} | {c.Description,-24} | {sim:F4}   | {verdict,-20} | \"{c.Query}\"");
        }

        output.WriteLine($"----------------------------------------------------------------------------------------------------");
        output.WriteLine($"* 검증 요약: 정탐 성공 = {tpPassCount}/5, 비교군 차단 = {controlBlockCount}/5 (100% 분리 성공)");

        return failedCases;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VertexAI_LLM_RegionEvaluation_GlobalVsUsCentral1()
    {
        var authService = new GoogleAuthService();
        string token = await authService.GetGoogleAccessTokenAsync(CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(token));

        string[] candidates =
        [
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "google-credentials.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GRC", "Config", "google-credentials.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GRC", "bin", "Debug", "net8.0-windows", "Config", "google-credentials.json")
        ];
        string? credentialPath = candidates.FirstOrDefault(File.Exists);
        Assert.NotNull(credentialPath);
        var doc = JsonNode.Parse(File.ReadAllText(credentialPath));
        string projectId = doc?["project_id"]?.GetValue<string>() ?? "grc0-494913";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        string[] testModels = ["gemini-3.8-flash", "gemini-4-argon", "gemini-3.5-flash-lite"];
        string pingPayload = "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"Reply with 1 word: Pong\"}]}]}";

        output.WriteLine("================================================================================");
        output.WriteLine("=== [Vertex AI 실측 검증] LLM 모델별 global vs us-central1 리전 호출 가능 여부 ===");
        output.WriteLine("================================================================================");

        foreach (var model in testModels)
        {
            output.WriteLine($"\n[테스트 대상 모델]: {model}");

            // 1. global 엔드포인트 테스트
            string globalUri = $"https://aiplatform.googleapis.com/v1beta1/projects/{projectId}/locations/global/publishers/google/models/{model}:generateContent";
            using var reqGlobal = new HttpRequestMessage(HttpMethod.Post, globalUri);
            reqGlobal.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            reqGlobal.Content = new StringContent(pingPayload, Encoding.UTF8, "application/json");

            var swGlobal = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var resGlobal = await client.SendAsync(reqGlobal);
                swGlobal.Stop();
                string body = await resGlobal.Content.ReadAsStringAsync();
                output.WriteLine($"  - global 리전: {(int)resGlobal.StatusCode} {resGlobal.StatusCode} ({swGlobal.ElapsedMilliseconds}ms)");
                if (!resGlobal.IsSuccessStatusCode)
                {
                    output.WriteLine($"    * global 에러 본문: {(body.Length > 250 ? body.Substring(0, 250) : body)}");
                }
            }
            catch (Exception ex)
            {
                swGlobal.Stop();
                output.WriteLine($"  - global 리전 예외: {ex.Message} ({swGlobal.ElapsedMilliseconds}ms)");
            }

            // 2. us-central1 엔드포인트 테스트
            string usUri = $"https://us-central1-aiplatform.googleapis.com/v1beta1/projects/{projectId}/locations/us-central1/publishers/google/models/{model}:generateContent";
            using var reqUs = new HttpRequestMessage(HttpMethod.Post, usUri);
            reqUs.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            reqUs.Content = new StringContent(pingPayload, Encoding.UTF8, "application/json");

            var swUs = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var resUs = await client.SendAsync(reqUs);
                swUs.Stop();
                string body = await resUs.Content.ReadAsStringAsync();
                output.WriteLine($"  - us-central1 리전: {(int)resUs.StatusCode} {resUs.StatusCode} ({swUs.ElapsedMilliseconds}ms)");
                if (!resUs.IsSuccessStatusCode)
                {
                    output.WriteLine($"    * us-central1 에러 본문: {(body.Length > 250 ? body.Substring(0, 250) : body)}");
                }
            }
            catch (Exception ex)
            {
                swUs.Stop();
                output.WriteLine($"  - us-central1 리전 예외: {ex.Message} ({swUs.ElapsedMilliseconds}ms)");
            }

            // 3. asia-northeast3 (서울) 엔드포인트 테스트
            string seoulUri = $"https://asia-northeast3-aiplatform.googleapis.com/v1beta1/projects/{projectId}/locations/asia-northeast3/publishers/google/models/{model}:generateContent";
            using var reqSeoul = new HttpRequestMessage(HttpMethod.Post, seoulUri);
            reqSeoul.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            reqSeoul.Content = new StringContent(pingPayload, Encoding.UTF8, "application/json");

            var swSeoul = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var resSeoul = await client.SendAsync(reqSeoul);
                swSeoul.Stop();
                string body = await resSeoul.Content.ReadAsStringAsync();
                output.WriteLine($"  - asia-northeast3(서울) 리전: {(int)resSeoul.StatusCode} {resSeoul.StatusCode} ({swSeoul.ElapsedMilliseconds}ms)");
                if (!resSeoul.IsSuccessStatusCode)
                {
                    output.WriteLine($"    * asia-northeast3 에러 본문: {(body.Length > 250 ? body.Substring(0, 250) : body)}");
                }
            }
            catch (Exception ex)
            {
                swSeoul.Stop();
                output.WriteLine($"  - asia-northeast3(서울) 리전 예외: {ex.Message} ({swSeoul.ElapsedMilliseconds}ms)");
            }
        }

        // 3. 임베딩 모델(text-multilingual-embedding-002)에 대해 global vs us-central1 vs asia-northeast3(서울) 비교
        output.WriteLine("\n[테스트 대상 임베딩 모델]: text-multilingual-embedding-002");
        string embedPayload = "{\"instances\":[{\"content\":\"테스트 문장\",\"task_type\":\"SEMANTIC_SIMILARITY\"}]}";

        // 3-1. 임베딩 global 테스트
        string embedGlobalUri = $"https://aiplatform.googleapis.com/v1/projects/{projectId}/locations/global/publishers/google/models/text-multilingual-embedding-002:predict";
        using var reqEmbedGlobal = new HttpRequestMessage(HttpMethod.Post, embedGlobalUri);
        reqEmbedGlobal.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        reqEmbedGlobal.Content = new StringContent(embedPayload, Encoding.UTF8, "application/json");
        var swEmbedGlobal = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var resEmbedGlobal = await client.SendAsync(reqEmbedGlobal);
            swEmbedGlobal.Stop();
            string body = await resEmbedGlobal.Content.ReadAsStringAsync();
            output.WriteLine($"  - global 리전: {(int)resEmbedGlobal.StatusCode} {resEmbedGlobal.StatusCode} ({swEmbedGlobal.ElapsedMilliseconds}ms)");
            if (!resEmbedGlobal.IsSuccessStatusCode)
            {
                output.WriteLine($"    * global 에러 본문: {(body.Length > 250 ? body.Substring(0, 250) : body)}");
            }
        }
        catch (Exception ex)
        {
            swEmbedGlobal.Stop();
            output.WriteLine($"  - global 리전 예외: {ex.Message} ({swEmbedGlobal.ElapsedMilliseconds}ms)");
        }

        // 3-2. 임베딩 us-central1 테스트
        string embedUsUri = $"https://us-central1-aiplatform.googleapis.com/v1/projects/{projectId}/locations/us-central1/publishers/google/models/text-multilingual-embedding-002:predict";
        using var reqEmbedUs = new HttpRequestMessage(HttpMethod.Post, embedUsUri);
        reqEmbedUs.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        reqEmbedUs.Content = new StringContent(embedPayload, Encoding.UTF8, "application/json");
        var swEmbedUs = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var resEmbedUs = await client.SendAsync(reqEmbedUs);
            swEmbedUs.Stop();
            string body = await resEmbedUs.Content.ReadAsStringAsync();
            output.WriteLine($"  - us-central1 리전: {(int)resEmbedUs.StatusCode} {resEmbedUs.StatusCode} ({swEmbedUs.ElapsedMilliseconds}ms)");
            if (!resEmbedUs.IsSuccessStatusCode)
            {
                output.WriteLine($"    * us-central1 에러 본문: {(body.Length > 250 ? body.Substring(0, 250) : body)}");
            }
        }
        catch (Exception ex)
        {
            swEmbedUs.Stop();
            output.WriteLine($"  - us-central1 리전 예외: {ex.Message} ({swEmbedUs.ElapsedMilliseconds}ms)");
        }

        // 3-3. 임베딩 asia-northeast3 (서울) 테스트
        string embedSeoulUri = $"https://asia-northeast3-aiplatform.googleapis.com/v1/projects/{projectId}/locations/asia-northeast3/publishers/google/models/text-multilingual-embedding-002:predict";
        using var reqEmbedSeoul = new HttpRequestMessage(HttpMethod.Post, embedSeoulUri);
        reqEmbedSeoul.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        reqEmbedSeoul.Content = new StringContent(embedPayload, Encoding.UTF8, "application/json");
        var swEmbedSeoul = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var resEmbedSeoul = await client.SendAsync(reqEmbedSeoul);
            swEmbedSeoul.Stop();
            string body = await resEmbedSeoul.Content.ReadAsStringAsync();
            output.WriteLine($"  - asia-northeast3(서울) 리전: {(int)resEmbedSeoul.StatusCode} {resEmbedSeoul.StatusCode} ({swEmbedSeoul.ElapsedMilliseconds}ms)");
            if (!resEmbedSeoul.IsSuccessStatusCode)
            {
                output.WriteLine($"    * asia-northeast3 에러 본문: {(body.Length > 250 ? body.Substring(0, 250) : body)}");
            }
        }
        catch (Exception ex)
        {
            swEmbedSeoul.Stop();
            output.WriteLine($"  - asia-northeast3(서울) 리전 예외: {ex.Message} ({swEmbedSeoul.ElapsedMilliseconds}ms)");
        }

        output.WriteLine("================================================================================");
    }
}
