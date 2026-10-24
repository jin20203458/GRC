

using GRC.Models;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using Google.Apis.Auth.OAuth2;

namespace GRC.Services;

public class GeminiTtsService(
    HttpClient httpClient,
    IAppSettingsService appSettingsService,
    IGoogleAuthService googleAuthService,
    IGeminiApiService apiService) : IGoogleTtsService
{
    private const string AiStudioModel = "gemini-3.1-flash-tts-preview";
    private const string VertexModel = "gemini-3.1-flash-tts-preview";
    private const int DefaultSampleRate = 24000;
    private const string DefaultVoiceName = "Zephyr"; 

    public async Task<string> PreviewSpeechAsync(
        string text,
        string voiceName,
        TtsLanguage language,
        CancellationToken cancellationToken = default)
    {
        return await GenerateSpeechAsync(
            text: text,
            characterName: "시스템",
            narration: "안내 음성",
            activeLorebook: "안내 음성 테스트입니다.",
            voiceOverride: voiceName,
            languageOverride: language,
            cancellationToken: cancellationToken);
    }

    public async Task<string> GenerateSpeechAsync(
        string text,
        string characterName,
        string narration,
        string activeLorebook,
        string? voiceOverride = null,
        TtsLanguage? languageOverride = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await appSettingsService.LoadSettingsAsync();
        var effectiveLanguage = languageOverride ?? settings.SelectedTtsLanguage;
        string targetLanguage = effectiveLanguage == TtsLanguage.Japanese ? "일본어(Japanese)" : "한국어(Korean)";

        string speechText = text;
        if (effectiveLanguage == TtsLanguage.Japanese)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                string translationSystemPrompt = """
<system_directive>
Domain: Natural Conversational Dialogue Translation
Task: Translate Korean conversational dialogue into natural, immersive spoken Japanese for voice acting.
Source Language: Korean
Target Language: Japanese (Conversational Spoken Tone)
</system_directive>

<rules>
1. 원문의 감정선, 뉘앙스, 캐릭터 어투(존댓말/반말, 성격)를 살려 자연스러운 일본어 구어체(口語体)로 번역하십시오.
2. 따옴표(" ", 「 」), 마크다운 서식, 주석, 부연 설명을 일체 배제하고 순수한 번역문 텍스트만 직접 출력하십시오.
</rules>

<output_format>
Plain text containing only the translated Japanese dialogue.
</output_format>
""";

                string translationUserPrompt = $$"""
<dialogue_to_translate>
{{text.Trim()}}
</dialogue_to_translate>

<final_instruction>
위 <dialogue_to_translate> 내부의 텍스트를 가장 자연스러운 일본어 구어체 대사로 번역하여 순수 평문만 출력하십시오.
</final_instruction>
""";

                var translationRequest = new GeminiRequest(
                    SystemInstruction: new Content("system", [new Part(translationSystemPrompt)]),
                    Contents: [new Content("user", [new Part(translationUserPrompt)])],
                    SafetySettings: [
                        new("HARM_CATEGORY_HARASSMENT", BlockThreshold.BLOCK_NONE),
                        new("HARM_CATEGORY_HATE_SPEECH", BlockThreshold.BLOCK_NONE),
                        new("HARM_CATEGORY_SEXUALLY_EXPLICIT", BlockThreshold.BLOCK_NONE),
                        new("HARM_CATEGORY_DANGEROUS_CONTENT", BlockThreshold.BLOCK_NONE)
                    ],
                    GenerationConfig: new GenerationConfig(
                        Temperature: null,
                        MaxOutputTokens: 1024,
                        ResponseMimeType: "text/plain",
                        ResponseSchema: null,
                        ThinkingConfig: new ThinkingConfig(ThinkingLevel.low)
                    )
                );

                string translatedText = await apiService.SendMessageAsync(translationRequest, ModelTier.FlashLite, cancellationToken);
                if (!string.IsNullOrWhiteSpace(translatedText) && !translatedText.StartsWith("[System"))
                {
                    speechText = translatedText.Trim();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeminiTTS] 일본어 번역 실패, 원본 사용: {ex.Message}");
            }
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, string.Empty);

        if (settings.UseVertexAI)
        {
            if (string.IsNullOrWhiteSpace(settings.ProjectId))
                throw new InvalidOperationException("[System Error]: 구글 클라우드 Project ID가 설정되지 않았습니다.");

            const string location = "us-central1";
            httpRequest.RequestUri = new Uri($"https://{location}-aiplatform.googleapis.com/v1beta1/projects/{settings.ProjectId}/locations/{location}/publishers/google/models/{VertexModel}:generateContent");

            try
            {
                string token = await googleAuthService.GetGoogleAccessTokenAsync(cancellationToken);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            catch (Exception ex)
            {
                throw new UnauthorizedAccessException($"[System Error]: 인증 토큰 획득 실패. 인증 파일을 확인하세요. {ex.Message}");
            }

            Debug.WriteLine($"[GeminiTTS] 📡 Vertex AI 사용 모델: {VertexModel}, 리전: {location}");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
                throw new InvalidOperationException("[System Error]: API 키가 설정되지 않았습니다.");

            httpRequest.RequestUri = new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{AiStudioModel}:generateContent?key={settings.ApiKey}");
        }

        string effectiveVoice = !string.IsNullOrWhiteSpace(voiceOverride)
            ? voiceOverride
            : (string.IsNullOrWhiteSpace(settings.SelectedTtsVoice) ? DefaultVoiceName : settings.SelectedTtsVoice);

        string dynamicPrompt = $$"""
<system_directive>
Role: 캐릭터 보이스 전문 성우 (Voice Actor)
Task: 주어진 캐릭터 설정과 상황 지문에 몰입하여 대사를 감정적으로 연기
Target Language: {{targetLanguage}}
</system_directive>

<character_context>
{{activeLorebook}}
</character_context>

<acting_directing>
{{narration}}
</acting_directing>

<dialogue_to_speak>
{{speechText}}
</dialogue_to_speak>

<final_instruction>
위 상황과 감정선을 바탕으로 <dialogue_to_speak> 대사를 {{targetLanguage}} 원어민의 자연스러운 억양과 깊은 감정선으로 연기하십시오.
</final_instruction>
""";

        var payload = new
        {
            contents = new[] {
                new {
                    role = "user",
                    parts = new[] { new { text = dynamicPrompt } }
                }
            },
            safetySettings = new[]
            {
                new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
            },
            generationConfig = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = effectiveVoice } } }
            }
        };

        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                try
                {
                    // 구글 API의 표준 에러 응답 파싱 시도
                    using var errorDoc = JsonDocument.Parse(errorContent);
                    if (errorDoc.RootElement.TryGetProperty("error", out var errorElement))
                    {
                        string errorMessage = errorElement.TryGetProperty("message", out var msg) ? msg.GetString() ?? "알 수 없는 에러" : "알 수 없는 에러";
                        string errorCode = errorElement.TryGetProperty("code", out var code) ? code.GetInt32().ToString() : "N/A";
                        string errorStatus = errorElement.TryGetProperty("status", out var status) ? status.GetString() ?? "UNKNOWN" : "UNKNOWN";

                        throw new HttpRequestException($"[API 거부] 상태: {errorStatus}({errorCode})\n상세 메시지: {errorMessage}");
                    }
                }
                catch (JsonException)
                {
                    // JSON 형식이 아닌 에러 응답일 경우 폴백
                }

                throw new HttpRequestException($"API 통신 실패 ({(int)response.StatusCode}): {errorContent}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];

                // 1. content와 parts 속성이 존재하는지 안전하게 확인
                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("inlineData", out var inlineData))
                        {
                            string mimeType = inlineData.TryGetProperty("mimeType", out var mimeElement) ? mimeElement.GetString() ?? "" : "";

                            // 2. data 키도 안전하게 추출
                            if (!inlineData.TryGetProperty("data", out var dataElement))
                            {
                                continue;
                            }

                            string base64Audio = dataElement.GetString()!;
                            byte[] audioBytes = Convert.FromBase64String(base64Audio);

                            string extension = mimeType switch
                            {
                                _ when mimeType.Contains("mp3") => ".mp3",
                                _ when mimeType.Contains("ogg") => ".ogg",
                                _ => ".wav"
                            };

                            string filePath = Path.Combine(Path.GetTempPath(), $"grc_voice_{Guid.NewGuid()}{extension}");

                            if (extension == ".wav")
                            {
                                bool isRiffWav = audioBytes.Length >= 4 &&
                                                 audioBytes[0] == 0x52 && // 'R'
                                                 audioBytes[1] == 0x49 && // 'I'
                                                 audioBytes[2] == 0x46 && // 'F'
                                                 audioBytes[3] == 0x46;   // 'F'

                                if (!isRiffWav)
                                {
                                    await WriteWavFileAsync(filePath, audioBytes, DefaultSampleRate, 1, 16);
                                }
                                else
                                {
                                    await File.WriteAllBytesAsync(filePath, audioBytes);
                                }
                            }
                            else
                            {
                                await File.WriteAllBytesAsync(filePath, audioBytes);
                            }

                            //Debug.WriteLine($"[GeminiTTS] ✅ 음성 파일 생성 완료: {filePath}");
                            return filePath;
                        }
                    }
                }
                else
                {
                    // 3. 필터링 등 기타 사유로 content가 누락된 경우의 상세 원인 추적
                    string finishReason = candidate.TryGetProperty("finishReason", out var reasonElement)
                        ? reasonElement.GetString() ?? "Unknown"
                        : "Unknown";

                    string reasonDescription = finishReason switch
                    {
                        "SAFETY" => "안전 필터링(수위 제한)에 차단되었습니다.",
                        "RECITATION" => "저작권 또는 훈련 데이터 암기 방지 정책에 의해 차단되었습니다.",
                        "MAX_TOKENS" => "생성 가능한 최대 길이를 초과했습니다.",
                        "OTHER" => "구글 API 내부 오류 또는 모델이 처리를 거부했습니다.",
                        _ => "알 수 없는 이유로 생성이 중단되었습니다."
                    };

                    string detailedMessage = $"API가 오디오 생성을 거부했습니다. (사유: {finishReason} - {reasonDescription})";

                    // SAFETY 사유로 차단된 경우, 정확히 어떤 필터에 걸렸는지 상세 추적
                    if (finishReason == "SAFETY" && candidate.TryGetProperty("safetyRatings", out var safetyRatings))
                    {
                        var blockedReasons = new List<string>();

                        foreach (var rating in safetyRatings.EnumerateArray())
                        {
                            string category = rating.TryGetProperty("category", out var catElement) ? catElement.GetString() ?? "Unknown_Category" : "Unknown_Category";
                            string probability = rating.TryGetProperty("probability", out var probElement) ? probElement.GetString() ?? "Unknown_Probability" : "Unknown_Probability";

                            // 차단 여부를 명시적으로 알려주는 'blocked' 필드가 있는 경우 확인
                            bool isBlocked = rating.TryGetProperty("blocked", out var blockedElement) && blockedElement.GetBoolean();

                            // 차단되었거나, 확률이 높음(HIGH)/중간(MEDIUM)인 항목 수집
                            if (isBlocked || probability == "HIGH" || probability == "MEDIUM")
                            {
                                blockedReasons.Add($"[{category} 위반 의심: {probability}]");
                            }
                        }

                        if (blockedReasons.Count > 0)
                        {
                            detailedMessage += "\n상세 원인: " + string.Join(", ", blockedReasons);
                        }
                    }

                    throw new InvalidDataException(detailedMessage);
                }
            }

            throw new InvalidDataException("구글 API로부터 유효한 오디오 데이터를 받지 못했습니다. (응답 배열이 비어있음)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GeminiTTS] ❌ 시스템 오류: {ex.Message}");
            throw;
        }
    }


    private static async Task WriteWavFileAsync(string filePath, byte[] pcmData, int sampleRate, short channels, short bitsPerSample)
    {
        // 비동기 FileStream을 열어 블로킹 방지
        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        using var writer = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8);
        writer.Write(36 + pcmData.Length);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16); // Subchunk1Size
        writer.Write((short)1); // AudioFormat (1 = PCM)
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8)); // ByteRate
        writer.Write((short)(channels * (bitsPerSample / 8))); // BlockAlign
        writer.Write(bitsPerSample);

        writer.Write("data"u8);
        writer.Write(pcmData.Length);
        writer.Flush(); // 헤더 기록 완료

        // 대용량 PCM 데이터는 별도의 메모리 복사 없이 FileStream 비동기 쓰기로 넘김
        await fs.WriteAsync(pcmData);
    }
}