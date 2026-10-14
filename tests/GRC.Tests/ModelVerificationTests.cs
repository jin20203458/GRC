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

public class ModelVerificationTests(ITestOutputHelper output)
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

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("gemini-3.5-flash", true)]
    [InlineData("gemini-3.8-flash", true)]
    [InlineData("gemini-3.5-flash-lite", true)]
    [InlineData("gemini-4-argon", true)]
    public async Task VerifyModelResponsiveness(string modelName, bool isTargetStandard)
    {
        var (token, projectId) = await GetAuthInfoAsync();

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        string requestUri = $"https://aiplatform.googleapis.com/v1beta1/projects/{projectId}/locations/global/publishers/google/models/{modelName}:generateContent";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[]
                    {
                        new { text = "Respond with 'OK'." }
                    }
                }
            },
            generationConfig = new
            {
                maxOutputTokens = 32
            }
        };

        string jsonString = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(jsonString, Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            sw.Stop();
            output.WriteLine($"[{modelName}] (Standard={isTargetStandard}) EXCEPTION: {ex.Message} (Elapsed: {sw.ElapsedMilliseconds}ms)");
            throw;
        }

        sw.Stop();
        string body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            string reply = "";
            try
            {
                var json = JsonNode.Parse(body);
                reply = json?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>()?.Trim() ?? "";
            }
            catch
            {
                reply = body;
            }

            output.WriteLine($"[{modelName}] SUCCESS (Status: {(int)response.StatusCode}, Latency: {sw.ElapsedMilliseconds}ms) -> Response: {reply}");
        }
        else
        {
            output.WriteLine($"[{modelName}] FAILED (Status: {(int)response.StatusCode}, Latency: {sw.ElapsedMilliseconds}ms) -> Error: {body}");
        }

        if (isTargetStandard)
        {
            Assert.True(response.IsSuccessStatusCode, $"Target standard model '{modelName}' failed with {(int)response.StatusCode}: {body}");
        }
    }

    [Fact]
    public void VerifyAppSettingsBackwardsCompatibility_IgnoresLegacyLocationProperty()
    {
        string legacyJson = """
        {
            "ApiKey": "test-key",
            "ProjectId": "my-project",
            "Location": "asia-northeast3",
            "UseVertexAI": true,
            "SelectedModel": "Flash"
        }
        """;

        var settings = JsonSerializer.Deserialize<GRC.Models.AppSettings>(legacyJson);
        Assert.NotNull(settings);
        Assert.Equal("test-key", settings.ApiKey);
        Assert.Equal("my-project", settings.ProjectId);
        Assert.True(settings.UseVertexAI);
        Assert.Equal(GRC.Models.ModelTier.Flash, settings.SelectedModel);
    }
}
