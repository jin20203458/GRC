using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Apis.Auth.OAuth2;
using Xunit;
using Xunit.Abstractions;

namespace GRC.Tests;

public class ContextCachingVerificationTests(ITestOutputHelper output)
{
    private static async Task<(string token, string projectId)> GetAuthInfoAsync()
    {
        string[] candidates =
        [
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "google-credentials.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GRC", "Config", "google-credentials.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GRC", "bin", "Debug", "net8.0-windows", "Config", "google-credentials.json")
        ];

        string? foundPath = candidates.FirstOrDefault(File.Exists);
        if (foundPath == null)
        {
            throw new FileNotFoundException("google-credentials.json 파일을 찾을 수 없습니다.");
        }

        string jsonContent = await File.ReadAllTextAsync(foundPath);
        var doc = JsonNode.Parse(jsonContent);
        string projectId = doc?["project_id"]?.GetValue<string>() ?? "grc0-494913";

        var specificCredential = CredentialFactory.FromJson<ServiceAccountCredential>(jsonContent);
        var credential = specificCredential.ToGoogleCredential()
            .CreateScoped("https://www.googleapis.com/auth/cloud-platform");

        string token = await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(null, CancellationToken.None);
        return (token, projectId);
    }

    private static string GenerateRepeatedText(string baseText, int targetWords)
    {
        var sb = new StringBuilder();
        int words = 0;
        int index = 1;
        while (words < targetWords)
        {
            string line = $"{index}. {baseText} ";
            sb.AppendLine(line);
            words += line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            index++;
        }
        return sb.ToString();
    }

    private static async Task<(int PromptTokens, int CachedTokens, int OutputTokens, string ResponseText)> SendRequestAsync(
        HttpClient client, string requestUri, string token, string systemInstructionText, string userMessageText)
    {
        var payload = new
        {
            systemInstruction = new
            {
                role = "system",
                parts = new[] { new { text = systemInstructionText } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userMessageText } }
                }
            },
            generationConfig = new
            {
                maxOutputTokens = 64,
                temperature = 1.0
            }
        };

        string jsonString = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(jsonString, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"API Failure ({(int)response.StatusCode}): {body}");
        }

        var doc = JsonNode.Parse(body);
        int promptTokens = doc?["usageMetadata"]?["promptTokenCount"]?.GetValue<int>() ?? 0;
        int cachedTokens = doc?["usageMetadata"]?["cachedContentTokenCount"]?.GetValue<int>() ?? 0;
        int candidatesTokens = doc?["usageMetadata"]?["candidatesTokenCount"]?.GetValue<int>() ?? 0;
        string text = doc?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>()?.Trim() ?? "";

        return (promptTokens, cachedTokens, candidatesTokens, text);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TestContextCaching_Comparison_3000_vs_4200_Tokens()
    {
        var (token, projectId) = await GetAuthInfoAsync();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        string modelName = "gemini-3.8-flash";
        string requestUri = $"https://aiplatform.googleapis.com/v1beta1/projects/{projectId}/locations/global/publishers/google/models/{modelName}:generateContent";

        output.WriteLine($"=== Testing Context Caching on {modelName} ===");

        string uniqueSeedA = "Alpha" + Guid.NewGuid().ToString("N")[..6];
        string prefix3000 = GenerateRepeatedText($"[Session:{uniqueSeedA}] 아카이아 제국의 역사와 제1황녀 일리아의 전기.", 1100);

        output.WriteLine($"--- [Test 1: Unique ~3000 Tokens ({uniqueSeedA})] ---");
        var (p1_1, c1_1, _, _) = await SendRequestAsync(client, requestUri, token, prefix3000, "질문 1");
        output.WriteLine($"Call 1: Prompt={p1_1}, Cached={c1_1}");
        await Task.Delay(2000);
        var (p1_2, c1_2, _, _) = await SendRequestAsync(client, requestUri, token, prefix3000, "질문 2");
        output.WriteLine($"Call 2: Prompt={p1_2}, Cached={c1_2}");

        string uniqueSeedB = "Beta" + Guid.NewGuid().ToString("N")[..6];
        string prefix5000 = GenerateRepeatedText($"[Session:{uniqueSeedB}] 아카이아 제국의 역사와 제1황녀 일리아의 전기.", 1850);

        output.WriteLine($"--- [Test 2: Unique ~5000 Tokens ({uniqueSeedB})] ---");
        var (p2_1, c2_1, _, _) = await SendRequestAsync(client, requestUri, token, prefix5000, "질문 1");
        output.WriteLine($"Call 1: Prompt={p2_1}, Cached={c2_1}");
        await Task.Delay(3000);
        var (p2_2, c2_2, _, _) = await SendRequestAsync(client, requestUri, token, prefix5000, "질문 2");
        output.WriteLine($"Call 2: Prompt={p2_2}, Cached={c2_2}");

        output.WriteLine("");
        output.WriteLine("=== FINAL REAL-WORLD DATA ===");
        output.WriteLine($"3,000 토큰 구간: Call 1={p1_1} (Cached={c1_1}) -> Call 2={p1_2} (Cached={c1_2})");
        output.WriteLine($"5,000 토큰 구간: Call 1={p2_1} (Cached={c2_1}) -> Call 2={p2_2} (Cached={c2_2})");
    }
}
