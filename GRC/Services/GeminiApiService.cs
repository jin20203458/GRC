using GRC.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;

namespace GRC.Services;

public class GeminiApiService(HttpClient httpClient, IAppSettingsService appSettingsService, IGoogleAuthService googleAuthService) : IGeminiApiService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private string GetModelName(ModelTier tier) => tier switch
    {
        ModelTier.Pro => "models/gemini-3.1-pro-preview",
        ModelTier.Flash35 => "models/gemini-3.5-flash", 
        ModelTier.FlashLite => "models/gemini-3.1-flash-lite-preview",
        ModelTier.Flash3 => "models/gemini-3-flash-preview",
        _ => "models/gemini-3.5-flash" 
    };

    private string GetVertexModelName(ModelTier tier) => tier switch
    {
        ModelTier.Pro => "gemini-3.1-pro-preview",
        ModelTier.Flash35 => "gemini-3.5-flash", 
        ModelTier.FlashLite => "gemini-3.1-flash-lite-preview",
        ModelTier.Flash3 => "gemini-3-flash-preview",
        _ => "gemini-3.5-flash"
    };

    public async Task<string> SendMessageAsync(GeminiRequest request, ModelTier? overrideTier = null, CancellationToken cancellationToken = default)
    {
        var settings = await appSettingsService.LoadSettingsAsync();
        string requestUri;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "");
        string jsonPayload = JsonSerializer.Serialize(request, _jsonOptions);
        httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        if (settings.UseVertexAI)
        {
            if (string.IsNullOrWhiteSpace(settings.ProjectId))
                return "[System Error]: 구글 클라우드 Project ID가 설정되지 않았습니다.";

            string location = string.IsNullOrWhiteSpace(settings.Location) ? "global" : settings.Location.ToLower();

            // global 리전일 경우 호스트명 앞에 지역을 붙이지 않습니다.
            string hostName = location == "global"
                ? "aiplatform.googleapis.com"
                : $"{location}-aiplatform.googleapis.com";

            string modelName = GetVertexModelName(overrideTier ?? settings.SelectedModel);
            requestUri = $"https://{hostName}/v1beta1/projects/{settings.ProjectId}/locations/{location}/publishers/google/models/{modelName}:generateContent";

            try
            {
                string token = await googleAuthService.GetGoogleAccessTokenAsync(cancellationToken);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            catch (Exception ex)
            {
                return $"[System Error]: 인증 토큰 획득 실패. 인증 파일을 확인하세요. {ex.Message}";
            }

            System.Diagnostics.Debug.WriteLine($"[Vertex AI 단발성 호출]: {modelName}, 리전: {location}");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
                return "[System Error]: API 키가 설정되지 않았습니다. 우측 상단의 ⚙ 설정 화면에서 API 키를 입력해주세요.";

            string modelName = GetModelName(overrideTier ?? settings.SelectedModel);
            System.Diagnostics.Debug.WriteLine($"[AI Studio 단발성 사용 모델]: {modelName}");

            requestUri = $"https://generativelanguage.googleapis.com/v1beta/{modelName}:generateContent?key={settings.ApiKey}";
        }

        httpRequest.RequestUri = new Uri(requestUri);

        try
        {
            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"API 통신 실패 ({(int)response.StatusCode}): {errorContent}");
            }

            string responseBody = await response.Content.ReadAsStringAsync();
            var responseData = JsonSerializer.Deserialize<GeminiResponse>(responseBody, _jsonOptions);

            if (responseData?.UsageMetadata != null)
            {
                _ = Task.Run(() => GRC.Helpers.TokenLogger.LogUsageAsync(
                    "Background_Summary",
                    responseData.UsageMetadata.PromptTokenCount,
                    responseData.UsageMetadata.CandidatesTokenCount,
                    responseData.UsageMetadata.ThoughtsTokenCount ?? 0,
                    responseData.UsageMetadata.TotalTokenCount));
            }

            if (responseData?.PromptFeedback != null && !string.IsNullOrEmpty(responseData.PromptFeedback.BlockReason))
            {
                System.Diagnostics.Debug.WriteLine($"[Google Safety Filter Blocked]: 사유 - {responseData.PromptFeedback.BlockReason}");
                return $"[System Error]: 프롬프트가 구글 안전 필터에 의해 원천 차단되었습니다. 사유: {responseData.PromptFeedback.BlockReason}";
            }

            if (responseData?.Candidates != null && responseData.Candidates.Count > 0)
            {
                var candidate = responseData.Candidates[0];

                if (candidate.FinishReason != "STOP" && candidate.FinishReason != "MAX_TOKENS")
                {
                    System.Diagnostics.Debug.WriteLine($"[System Error]: 생성 비정상 중단. FinishReason: {candidate.FinishReason}");
                    return $"[System Error]: 생성 비정상 중단. 사유: {candidate.FinishReason}";
                }

                if (candidate.Content?.Parts != null)
                {
                    var finalAnswer = new StringBuilder();

                    foreach (var part in candidate.Content.Parts)
                    {
                        if (string.IsNullOrEmpty(part.Text)) continue;
                        if (part.Thought == true) continue;

                        if (part.Text.Trim().Equals("thought", StringComparison.OrdinalIgnoreCase)) continue;

                        finalAnswer.Append(part.Text);
                    }

                    if (finalAnswer.Length > 0)
                    {
                        return finalAnswer.ToString();
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[GeminiApiService] Candidates[0]에 Parts는 존재하나 유효한 Text 내용이 없습니다. Raw Response:\n{responseBody}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[GeminiApiService] Candidates[0]의 Content 또는 Parts가 null입니다. Raw Response:\n{responseBody}");
                }
            }
            else
            {
                string feedbackStr = responseData?.PromptFeedback != null 
                    ? JsonSerializer.Serialize(responseData.PromptFeedback, _jsonOptions) 
                    : "없음";
                System.Diagnostics.Debug.WriteLine($"[GeminiApiService] Candidates가 비어있거나 null입니다. PromptFeedback: {feedbackStr}. Raw Response:\n{responseBody}");
            }

            return $"[System Error]: 구글 API로부터 유효한 텍스트를 받지 못했습니다. (Candidates 개수: {responseData?.Candidates?.Count ?? 0}, PromptFeedback 존재 여부: {responseData?.PromptFeedback != null})";
        }
        catch (HttpRequestException ex)
        {
            return $"[System Error]: 인터넷 연결을 확인해주세요. {ex.Message}";
        }
        catch (JsonException ex)
        {
            return $"[System Error]: 구글의 응답 규격이 변경되었을 수 있습니다. {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"[System Error]: {ex.Message}";
        }
    }

    public async IAsyncEnumerable<string> SendMessageStreamAsync(GeminiRequest request, ModelTier? overrideTier = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = await appSettingsService.LoadSettingsAsync();
        string requestUri;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "");
        string jsonPayload = JsonSerializer.Serialize(request, _jsonOptions);
        httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        if (settings.UseVertexAI)
        {
            if (string.IsNullOrWhiteSpace(settings.ProjectId))
            {
                yield return "[System Error]: 구글 클라우드 Project ID가 설정되지 않았습니다.";
                yield break;
            }

            string location = string.IsNullOrWhiteSpace(settings.Location) ? "global" : settings.Location.ToLower();

            // global 리전일 경우 호스트명 앞에 지역을 붙이지 않습니다.
            string hostName = location == "global"
                ? "aiplatform.googleapis.com"
                : $"{location}-aiplatform.googleapis.com";

            string modelName = GetVertexModelName(overrideTier ?? settings.SelectedModel);
            System.Diagnostics.Debug.WriteLine($"[Vertex AI 스트리밍 사용 모델]: {modelName}, 리전: {location}");

            requestUri = $"https://{hostName}/v1beta1/projects/{settings.ProjectId}/locations/{location}/publishers/google/models/{modelName}:streamGenerateContent?alt=sse";

            // 💡 C# 문법 제한 해결: catch 문 밖에서 yield를 반환하기 위해 변수 사용
            string? authErrorMessage = null;
            try
            {
                string token = await googleAuthService.GetGoogleAccessTokenAsync(cancellationToken);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            catch (Exception ex)
            {
                authErrorMessage = $"[System Error]: 인증 토큰 획득 실패. 인증 파일을 확인하세요. {ex.Message}";
            }

            // try-catch 블록을 빠져나온 후 yield return 실행
            if (authErrorMessage != null)
            {
                yield return authErrorMessage;
                yield break;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                yield return "[System Error]: API 키가 설정되지 않았습니다. 설정 화면에서 API 키를 입력해주세요.";
                yield break;
            }

            string modelName = GetModelName(overrideTier ?? settings.SelectedModel);
            System.Diagnostics.Debug.WriteLine($"[AI Studio 스트리밍 사용 모델]: {modelName}");

            requestUri = $"https://generativelanguage.googleapis.com/v1beta/{modelName}:streamGenerateContent?key={settings.ApiKey}&alt=sse";
        }

        httpRequest.RequestUri = new Uri(requestUri);

        using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string errorContent = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[HTTP Error]: {errorContent}");
            yield return $"[System Error]: API 통신 실패 ({(int)response.StatusCode}) - {errorContent}";
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        int finalPromptTokens = 0;
        int finalCandidateTokens = 0;
        int finalThoughtsTokens = 0;
        int finalTotalTokens = 0;

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            const string dataPrefix = "data: ";
            if (line.StartsWith(dataPrefix))
            {
                string jsonString = line.Substring(dataPrefix.Length);
                string? extractedChunk = null;

                try
                {
                    using var doc = JsonDocument.Parse(jsonString);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("usageMetadata", out var usageMeta))
                    {
                        if (usageMeta.TryGetProperty("promptTokenCount", out var promptTokens) &&
                            usageMeta.TryGetProperty("candidatesTokenCount", out var candidateTokens))
                        {
                            finalPromptTokens = promptTokens.GetInt32();
                            finalCandidateTokens = candidateTokens.GetInt32();

                            if (usageMeta.TryGetProperty("thoughtsTokenCount", out var thoughtsProp))
                            {
                                finalThoughtsTokens = thoughtsProp.GetInt32();
                            }
                            if (usageMeta.TryGetProperty("totalTokenCount", out var totalProp))
                            {
                                finalTotalTokens = totalProp.GetInt32();
                            }
                        }
                    }

                    if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                    {
                        var candidate = candidates[0];
                        var chunkBuilder = new StringBuilder();

                        // 1. Content가 있다면 먼저 파싱
                        if (candidate.TryGetProperty("content", out var contentElement) &&
                            contentElement.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                        {
                            foreach (var part in parts.EnumerateArray())
                            {
                                bool isThought = part.TryGetProperty("thought", out var thoughtElement) && thoughtElement.GetBoolean();
                                if (isThought) continue;

                                if (part.TryGetProperty("text", out var textElement))
                                {
                                    string? text = textElement.GetString();

                                    if (!string.IsNullOrEmpty(text) && !text.Trim().Equals("thought", StringComparison.OrdinalIgnoreCase))
                                    {
                                        chunkBuilder.Append(text);
                                    }
                                }
                            }
                        }

                        // 2. FinishReason 체크 (STOP이나 MAX_TOKENS가 아니면 에러로 처리)
                        if (candidate.TryGetProperty("finishReason", out var finishReasonElement))
                        {
                            string? finishReason = finishReasonElement.GetString();

                            if (!string.IsNullOrEmpty(finishReason) && finishReason != "STOP" && finishReason != "MAX_TOKENS")
                            {
                                chunkBuilder.Append($"\n\n[System Info]: 생성이 중단되었습니다. (사유: {finishReason})");
                            }
                        }

                        if (chunkBuilder.Length > 0)
                        {
                            extractedChunk = chunkBuilder.ToString();
                        }
                    }
                }
                catch (JsonException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[JSON Parsing Error]: {ex.Message} / 대상: {jsonString}");
                    continue; 
                }

                if (extractedChunk != null)
                {
                    yield return extractedChunk;
                }
            }
        }

        if (finalPromptTokens > 0)
        {
            _ = Task.Run(() => GRC.Helpers.TokenLogger.LogUsageAsync(
                "Chat_Stream",
                finalPromptTokens,
                finalCandidateTokens,
                finalThoughtsTokens,
                finalTotalTokens));
        }
    }
}