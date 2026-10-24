using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GRC.Models;
using GRC.Services;
using Xunit;

namespace GRC.Tests;

public class ReplySuggestionServiceTests
{
    private class FakeGeminiApiService : IGeminiApiService
    {
        public GeminiRequest? LastRequest { get; private set; }
        public ModelTier? LastModelTier { get; private set; }
        public string ResponseToReturn { get; set; } = "";
        public Exception? ExceptionToThrow { get; set; }

        public Task<string> SendMessageAsync(GeminiRequest request, ModelTier? overrideTier = null, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            LastModelTier = overrideTier;

            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(ResponseToReturn);
        }

        public async IAsyncEnumerable<string> SendMessageStreamAsync(
            GeminiRequest request,
            ModelTier? overrideTier = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public Task<float[]> GetEmbeddingAsync(string text, string? taskType = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<float>());
    }

    [Fact]
    public async Task GenerateAsync_WhenMemoryIsEmpty_ReturnsEmptyListWithoutCallingApi()
    {
        var fakeApi = new FakeGeminiApiService();
        var service = new ReplySuggestionService(fakeApi);

        var result = await service.GenerateAsync("컨텍스트", new List<ChatMessage>());

        Assert.Empty(result);
        Assert.Null(fakeApi.LastRequest);
    }

    [Fact]
    public async Task GenerateAsync_WhenLastMessageIsUser_ReturnsEmptyListWithoutCallingApi()
    {
        var fakeApi = new FakeGeminiApiService();
        var service = new ReplySuggestionService(fakeApi);
        var memory = new List<ChatMessage>
        {
            new("model", "어떤 일을 하시겠습니까?", DateTime.Now),
            new("user", "문을 열어본다.", DateTime.Now)
        };

        var result = await service.GenerateAsync("컨텍스트", memory);

        Assert.Empty(result);
        Assert.Null(fakeApi.LastRequest);
    }

    [Fact]
    public async Task GenerateAsync_WhenLastMessageIsModel_SendsRequestAndParsesSuggestions()
    {
        var fakeApi = new FakeGeminiApiService
        {
            ResponseToReturn = """
            [
              "문 뒤의 낌새를 귀 기울여 확인한다.",
              "천천히 손잡이를 돌려본다.",
              "동료에게 먼저 들어가지 말라고 신호한다."
            ]
            """
        };
        var service = new ReplySuggestionService(fakeApi);
        var memory = new List<ChatMessage>
        {
            new("user", "조심스럽게 다가간다.", DateTime.Now),
            new("model", "낡은 나무문 너머로 희미한 발소리가 들립니다.", DateTime.Now)
        };

        var result = await service.GenerateAsync("장소: 버려진 오두막", memory);

        Assert.Equal(3, result.Count);
        Assert.Equal("문 뒤의 낌새를 귀 기울여 확인한다.", result[0]);
        Assert.Equal("천천히 손잡이를 돌려본다.", result[1]);
        Assert.Equal("동료에게 먼저 들어가지 말라고 신호한다.", result[2]);

        Assert.NotNull(fakeApi.LastRequest);
        Assert.Equal(ModelTier.FlashLite, fakeApi.LastModelTier);
        Assert.Equal(1024, fakeApi.LastRequest.GenerationConfig?.MaxOutputTokens);
        Assert.Equal(ThinkingLevel.low, fakeApi.LastRequest.GenerationConfig?.ThinkingConfig?.Level);

        string promptText = fakeApi.LastRequest.Contents[0].Parts[0].Text!;
        Assert.Contains("<session_context>", promptText);
        Assert.Contains("장소: 버려진 오두막", promptText);
        Assert.Contains("<recent_conversation>", promptText);
        Assert.Contains("낡은 나무문 너머로 희미한 발소리가 들립니다.", promptText);
    }

    [Fact]
    public async Task GenerateAsync_WhenApiReturnsSystemError_ReturnsEmptyListGracefully()
    {
        var fakeApi = new FakeGeminiApiService
        {
            ResponseToReturn = "[System Error: Quota exceeded]"
        };
        var service = new ReplySuggestionService(fakeApi);
        var memory = new List<ChatMessage>
        {
            new("model", "안내 문구", DateTime.Now)
        };

        var result = await service.GenerateAsync("컨텍스트", memory);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GenerateAsync_WhenApiThrowsException_ReturnsEmptyListGracefully()
    {
        var fakeApi = new FakeGeminiApiService
        {
            ExceptionToThrow = new InvalidOperationException("Network failure")
        };
        var service = new ReplySuggestionService(fakeApi);
        var memory = new List<ChatMessage>
        {
            new("model", "안내 문구", DateTime.Now)
        };

        var result = await service.GenerateAsync("컨텍스트", memory);

        Assert.Empty(result);
    }
}
