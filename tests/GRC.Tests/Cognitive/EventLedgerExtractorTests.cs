using GRC.Models;
using GRC.Services;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GRC.Tests;

public class EventLedgerExtractorTests
{
    private class FakeGeminiApiService(string responseJson, float[]? dummyEmbedding = null) : IGeminiApiService
    {
        public Task<string> SendMessageAsync(GeminiRequest request, ModelTier? overrideTier = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(responseJson);
        }

        public async IAsyncEnumerable<string> SendMessageStreamAsync(
            GeminiRequest request,
            ModelTier? overrideTier = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return responseJson;
        }

        public Task<float[]> GetEmbeddingAsync(string text, string? taskType = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(dummyEmbedding ?? new float[768]);
        }
    }

    [Fact]
    public async Task ExtractLedgerFactsAsync_WhenValidJsonReturned_ParsesBulletsAndAttachesEmbedding()
    {
        // Arrange
        string json = "{\n  \"bullets\": [\n    \"안개 숲에서 부러진 백은 반지를 습득함\",\n    \"동굴 제단에서 발렌티 가문 인장 반지를 발견함\"\n  ]\n}";
        var dummyVec = new float[768];
        dummyVec[0] = 0.5f;

        var apiService = new FakeGeminiApiService(json, dummyVec);
        var extractor = new EventLedgerExtractor(apiService);

        var turns = new List<ChatMessage>
        {
            new("user", "동굴로 가자.", DateTime.Now),
            new("model", "동굴 제단에서 반지를 주웠습니다.", DateTime.Now)
        };

        // Act
        var result = await extractor.ExtractLedgerFactsAsync(turns, currentChapter: 1);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("안개 숲에서 부러진 백은 반지를 습득함", result[0].FactBullet);
        Assert.NotNull(result[0].Embedding);
        Assert.Equal(768, result[0].Embedding!.Length);
        Assert.Equal(0.5f, result[0].Embedding![0]);

        Assert.Equal("동굴 제단에서 발렌티 가문 인장 반지를 발견함", result[1].FactBullet);
    }

    [Fact]
    public async Task ExtractLedgerFactsAsync_WhenMarkdownJsonReturned_CleanAndParses()
    {
        // Arrange - 마크다운 코드블록 포함
        string json = "```json\n{\n  \"bullets\": [\"성내 여관에 은신처를 마련함\"]\n}\n```";
        var apiService = new FakeGeminiApiService(json);
        var extractor = new EventLedgerExtractor(apiService);

        var turns = new List<ChatMessage> { new("user", "여관에 방을 잡자.", DateTime.Now) };

        // Act
        var result = await extractor.ExtractLedgerFactsAsync(turns, currentChapter: 1);

        // Assert
        Assert.Single(result);
        Assert.Equal("성내 여관에 은신처를 마련함", result[0].FactBullet);
    }

    [Fact]
    public async Task ExtractLedgerFactsAsync_WhenEmptyOrInvalidResponse_ReturnsEmptyListWithoutCrash()
    {
        // Arrange - 깨진 응답
        var apiService = new FakeGeminiApiService("여기는 JSON이 아닙니다.");
        var extractor = new EventLedgerExtractor(apiService);

        var turns = new List<ChatMessage> { new("user", "테스트", DateTime.Now) };

        // Act
        var result = await extractor.ExtractLedgerFactsAsync(turns);

        // Assert - 크래시 없이 빈 리스트 반환
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExtractLedgerFactsAsync_WhenEmptyTurns_ReturnsEmptyImmediately()
    {
        // Arrange
        var apiService = new FakeGeminiApiService("{}");
        var extractor = new EventLedgerExtractor(apiService);

        // Act
        var result = await extractor.ExtractLedgerFactsAsync([]);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExtractLedgerFactsAsync_AssignsSpecifiedChapterToItems()
    {
        // Arrange
        string json = "{\n  \"bullets\": [\"2장의 중요 사건 발생\"]\n}";
        var apiService = new FakeGeminiApiService(json);
        var extractor = new EventLedgerExtractor(apiService);

        var turns = new List<ChatMessage> { new("user", "행동", DateTime.Now) };

        // Act - Chapter: 3 전달
        var result = await extractor.ExtractLedgerFactsAsync(turns, currentChapter: 3);

        // Assert
        Assert.Single(result);
        Assert.Equal(3, result[0].Chapter);
        Assert.Equal("2장의 중요 사건 발생", result[0].FactBullet);
    }

    [Fact]
    public async Task ExtractLedgerFactsAsync_WhenConversationalPreambleAndPostambleReturned_SuccessfullyParses()
    {
        // Arrange - 대화형 사설 및 마크다운 펜스 혼합
        string rawLlmResponse = "알겠습니다. 최근 대화의 핵심 사건을 요약합니다:\n```json\n{\n  \"bullets\": [\n    \"루시엘이 서고에서 고대 룬 문자 마법서를 해독함\"\n  ]\n}\n```\n향후 서사에 참고하십시오.";
        var apiService = new FakeGeminiApiService(rawLlmResponse);
        var extractor = new EventLedgerExtractor(apiService);
        var turns = new List<ChatMessage> { new("user", "해독해줘.", DateTime.Now) };

        // Act
        var result = await extractor.ExtractLedgerFactsAsync(turns);

        // Assert
        Assert.Single(result);
        Assert.Equal("루시엘이 서고에서 고대 룬 문자 마법서를 해독함", result[0].FactBullet);
    }

    [Fact]
    public async Task ExtractLedgerFactsAsync_WhenJsonTruncated_RecoversValidBullets()
    {
        // Arrange - 출력 토큰 제한으로 닫는 괄호가 누락된 잘린 JSON
        string truncatedJson = "{\n  \"bullets\": [\n    \"세레나가 북쪽 탑에서 반란군의 봉화를 목격함\"\n";
        var apiService = new FakeGeminiApiService(truncatedJson);
        var extractor = new EventLedgerExtractor(apiService);
        var turns = new List<ChatMessage> { new("user", "탑을 올려다본다.", DateTime.Now) };

        // Act
        var result = await extractor.ExtractLedgerFactsAsync(turns);

        // Assert - LlmJsonParser의 RepairTruncatedJson을 통해 무사히 복구되어야 함
        Assert.Single(result);
        Assert.Equal("세레나가 북쪽 탑에서 반란군의 봉화를 목격함", result[0].FactBullet);
    }
}
