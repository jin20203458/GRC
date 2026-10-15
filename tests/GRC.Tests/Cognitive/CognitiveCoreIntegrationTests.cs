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

        var ledgerFacts = await extractor.ExtractLedgerFactsAsync(recentTurns, currentTurn: 8);

        sw.Stop();
        output.WriteLine($"추출 완료 소요시간: {sw.ElapsedMilliseconds}ms");
        output.WriteLine($"추출된 불릿 개수: {ledgerFacts.Count}");

        Assert.NotEmpty(ledgerFacts);
        foreach (var item in ledgerFacts)
        {
            output.WriteLine($"* [Turn {item.Turn}]: {item.FactBullet}");
            Assert.False(string.IsNullOrWhiteSpace(item.FactBullet));
            Assert.Equal(8, item.Turn);

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
    public async Task MemoryManagerService_RealFlashLite_ExtractsStatusAndClues()
    {
        var (apiService, settingsService) = CreateRealServices();
        var memoryService = new MemoryManagerService(apiService, settingsService);

        string userAction = "쓰러진 도적의 품에서 떨어진 검은 양피지를 주워 품에 넣는다.";
        string modelNarrative = "엘리아스는 젖은 흙바닥에 떨어진 양피지 조각을 집어 들었다. 불빛에 비친 조각에는 검은 뱀이 똬리를 튼 기이한 문양과 '월식의 밤, 제단 아래'라는 핏빛 글귀가 희미하게 적혀 있었다. 그는 미간을 찌푸린 채 이를 조용히 품에 갈무리했다.";

        output.WriteLine("=== [E2E] Gemini 3.5 Flash-Lite 상태창 및 미회수 단서(unresolvedClues) 추출 시작 ===");
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
        if (payload.UnresolvedClues != null && payload.UnresolvedClues.Count > 0)
        {
            output.WriteLine($"미회수 떡밥/단서: {string.Join(" | ", payload.UnresolvedClues)}");
        }

        // 아이템이나 떡밥 중 양피지/조각/문양 관련 단어가 정상 추출되었는지 검증
        bool itemDetected = payload.Items.Any(i => i.Contains("양피지") || i.Contains("조각"));
        bool clueDetected = payload.UnresolvedClues != null && payload.UnresolvedClues.Any(c => c.Contains("양피지") || c.Contains("월식") || c.Contains("문양") || c.Contains("뱀"));

        output.WriteLine($"아이템 추출 성공: {itemDetected}, 단서 추출 성공: {clueDetected}");
        Assert.True(itemDetected || clueDetected, "양피지 아이템 또는 미해결 단서 중 최소 1개 이상 추출되어야 합니다.");
    }
}
