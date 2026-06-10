using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GRC.Models;

// ==========================================
// [Request (구글로 보낼 데이터 구조)]
// ==========================================
public record GeminiRequest(
    [property: JsonPropertyName("systemInstruction")] Content? SystemInstruction,
    [property: JsonPropertyName("contents")] List<Content> Contents,
    [property: JsonPropertyName("safetySettings")] List<SafetySetting>? SafetySettings,
    [property: JsonPropertyName("generationConfig")] GenerationConfig? GenerationConfig
);

public record Content(
    [property: JsonPropertyName("role")] string Role, // "user" 또는 "model"
    [property: JsonPropertyName("parts")] List<Part> Parts
);

public record Part(
    [property: JsonPropertyName("text")] string? Text, // nullable로 변경 (빈 텍스트 크래시 방지)
    [property: JsonPropertyName("thought")] bool? Thought = null // 사고 과정 식별자 추가
);


[JsonConverter(typeof(JsonStringEnumConverter))] 
public enum BlockThreshold
{
    BLOCK_NONE,
    BLOCK_ONLY_HIGH,
    BLOCK_MEDIUM_AND_ABOVE,
    BLOCK_LOW_AND_ABOVE
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelTier
{
    Pro,        // 최고 성능 (복잡한 추론)
    Flash,      // 가성비 메인 (일반 대화)
    FlashLite   // 초가성비 (백그라운드 요약)
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThinkingLevel
{
    minimal,
    low,
    medium,
    high
}

public record ThinkingConfig(
    [property: JsonPropertyName("thinkingLevel")] ThinkingLevel Level
);

public record SafetySetting(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("threshold")] BlockThreshold Threshold
);

public record GenerationConfig(
    [property: JsonPropertyName("temperature")] float Temperature,
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
    [property: JsonPropertyName("responseMimeType")] string? ResponseMimeType = "text/plain",
    [property: JsonPropertyName("responseSchema")] object? ResponseSchema = null,
    [property: JsonPropertyName("thinkingConfig")] ThinkingConfig? ThinkingConfig = null
);

// ==========================================
// [Response (구글에서 받을 데이터 구조)]
// ==========================================
public record GeminiResponse(
    [property: JsonPropertyName("candidates")] List<Candidate> Candidates,
    [property: JsonPropertyName("promptFeedback")] PromptFeedback? PromptFeedback, 
    [property: JsonPropertyName("usageMetadata")] UsageMetadata? UsageMetadata
);

public record Candidate(
    [property: JsonPropertyName("content")] Content Content,
    [property: JsonPropertyName("finishReason")] string FinishReason
);
public record UsageMetadata(
    [property: JsonPropertyName("promptTokenCount")] int PromptTokenCount,
    [property: JsonPropertyName("candidatesTokenCount")] int CandidatesTokenCount,
    [property: JsonPropertyName("totalTokenCount")] int TotalTokenCount,
    [property: JsonPropertyName("thoughtsTokenCount")] int? ThoughtsTokenCount = 0
);
public record PromptFeedback(
    [property: JsonPropertyName("blockReason")] string BlockReason
);
