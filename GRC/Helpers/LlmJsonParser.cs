using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GRC.Helpers;

/// <summary>
/// LLM 응답 텍스트에서 마크다운 코드블록, 대화형 사설, 잉여 괄호를 안전하게 무시하고
/// 유효한 JSON을 추출 및 역직렬화하는 고성능 내결함성 파서입니다.
/// </summary>
public static class LlmJsonParser
{
    public static readonly JsonSerializerOptions LenientOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonDocumentOptions LenientDocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// LLM의 응답 텍스트에서 마크다운 코드블록이나 불필요한 사설을 무시하고 유효한 단일 JSON 객체({ ... }) 문자열을 추출합니다.
    /// </summary>
    public static string? ExtractJson(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        // 1. 마크다운 코드블록(```json ... ```) 우선 탐색
        foreach (string fenced in ExtractCodeFenceBlocks(rawText))
        {
            string? candidate = ExtractFirstValidJsonBlock(fenced, '{', '}', isArray: false);
            if (candidate != null) return candidate;
        }

        // 2. 전체 텍스트에서 균형 잡힌 { ... } 블록 추출
        string? directCandidate = ExtractFirstValidJsonBlock(rawText, '{', '}', isArray: false);
        if (directCandidate != null) return directCandidate;

        // 3. 토큰 제한 등으로 잘린 JSON(Truncated JSON) 복구 시도
        return RepairTruncatedJson(rawText, '{', '}');
    }

    /// <summary>
    /// LLM의 응답 텍스트에서 마크다운 코드블록이나 사설을 무시하고 유효한 단일 JSON 배열([ ... ]) 문자열을 추출합니다.
    /// </summary>
    public static string? ExtractJsonArray(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        // 1. 마크다운 코드블록 우선 탐색
        foreach (string fenced in ExtractCodeFenceBlocks(rawText))
        {
            string? candidate = ExtractFirstValidJsonBlock(fenced, '[', ']', isArray: true);
            if (candidate != null) return candidate;
        }

        // 2. 전체 텍스트에서 균형 잡힌 [ ... ] 블록 추출
        string? directCandidate = ExtractFirstValidJsonBlock(rawText, '[', ']', isArray: true);
        if (directCandidate != null) return directCandidate;

        // 3. 잘린 JSON 배열 복구 시도
        return RepairTruncatedJson(rawText, '[', ']');
    }

    /// <summary>
    /// LLM 응답에서 JSON 객체를 안전하게 추출하고 역직렬화합니다.
    /// 다중 후보군 탐색, 문자열형 숫자 변환, Enum 파싱, 잘린 응답 복구를 자동으로 지원합니다.
    /// </summary>
    public static T? DeserializeSafe<T>(string rawText, JsonSerializerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return default;

        var targetOptions = options ?? LenientOptions;

        // 1. 객체 후보군 순차 역직렬화 시도
        var candidates = CollectCandidates(rawText, '{', '}', isArray: false);
        foreach (var json in candidates)
        {
            try
            {
                var result = JsonSerializer.Deserialize<T>(json, targetOptions);
                if (result != null) return result;
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[LlmJsonParser] Candidate object deserialization failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LlmJsonParser] Candidate deserialization unexpected error: {ex.Message}");
            }
        }

        // 2. T가 배열/컬렉션이거나 객체 매칭이 실패한 경우 배열 후보군 탐색
        var arrayCandidates = CollectCandidates(rawText, '[', ']', isArray: true);
        foreach (var json in arrayCandidates)
        {
            try
            {
                var result = JsonSerializer.Deserialize<T>(json, targetOptions);
                if (result != null) return result;
            }
            catch
            {
                // T와 배열 스키마 불일치 무시
            }
        }

        // 3. 잘린 JSON 객체 복구 후 역직렬화 시도
        string? repaired = RepairTruncatedJson(rawText, '{', '}');
        if (repaired != null)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(repaired, targetOptions);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LlmJsonParser] Repaired object deserialization failed: {ex.Message}");
            }
        }

        return default;
    }

    /// <summary>
    /// LLM 응답에서 JSON 배열을 안전하게 추출하고 역직렬화합니다.
    /// </summary>
    public static T? DeserializeArraySafe<T>(string rawText, JsonSerializerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return default;

        var targetOptions = options ?? LenientOptions;

        // 1. 배열 후보군 순차 역직렬화 시도
        var candidates = CollectCandidates(rawText, '[', ']', isArray: true);
        foreach (var json in candidates)
        {
            try
            {
                var result = JsonSerializer.Deserialize<T>(json, targetOptions);
                if (result != null) return result;
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[LlmJsonParser] Candidate array deserialization failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LlmJsonParser] Candidate array unexpected error: {ex.Message}");
            }
        }

        // 2. 잘린 JSON 배열 복구 후 역직렬화 시도
        string? repaired = RepairTruncatedJson(rawText, '[', ']');
        if (repaired != null)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(repaired, targetOptions);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LlmJsonParser] Repaired array deserialization failed: {ex.Message}");
            }
        }

        return default;
    }

    /// <summary>
    /// 스트리밍 중단이나 토큰 제한으로 인해 닫히지 않은 JSON의 문자열 및 괄호 구조를 자동으로 닫아 복구합니다.
    /// </summary>
    public static string? RepairTruncatedJson(string rawText, char openChar = '{', char closeChar = '}')
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        int startIndex = rawText.IndexOf(openChar);
        if (startIndex < 0) return null;

        var bracketStack = new List<char>();
        bool inString = false;
        bool isEscaped = false;

        for (int i = startIndex; i < rawText.Length; i++)
        {
            char c = rawText[i];

            if (inString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else
            {
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    bracketStack.Add('}');
                }
                else if (c == '[')
                {
                    bracketStack.Add(']');
                }
                else if (c == '}' || c == ']')
                {
                    if (bracketStack.Count > 0 && bracketStack[^1] == c)
                    {
                        bracketStack.RemoveAt(bracketStack.Count - 1);
                    }
                }
            }
        }

        if (bracketStack.Count == 0 && !inString)
        {
            return null; // 잘린 구조가 아님
        }

        var sb = new StringBuilder(rawText[startIndex..]);

        if (inString)
        {
            if (sb.Length > 0 && sb[^1] == '\\' && isEscaped)
            {
                sb.Remove(sb.Length - 1, 1);
            }
            sb.Append('"');
        }

        CleanTrailingDanglingTokens(sb);

        for (int i = bracketStack.Count - 1; i >= 0; i--)
        {
            sb.Append(bracketStack[i]);
        }

        string candidate = sb.ToString();
        if (IsValidJson(candidate, isArray: openChar == '['))
        {
            return candidate;
        }

        return null;
    }

    private static void CleanTrailingDanglingTokens(StringBuilder sb)
    {
        while (sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
        {
            sb.Remove(sb.Length - 1, 1);
        }

        if (sb.Length == 0) return;

        // 콜론으로 끝나는 불완전한 key-value 제거
        if (sb[^1] == ':')
        {
            sb.Remove(sb.Length - 1, 1);
            while (sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
            {
                sb.Remove(sb.Length - 1, 1);
            }

            if (sb.Length > 0 && sb[^1] == '"')
            {
                sb.Remove(sb.Length - 1, 1);
                while (sb.Length > 0 && sb[^1] != '"')
                {
                    sb.Remove(sb.Length - 1, 1);
                }
                if (sb.Length > 0 && sb[^1] == '"')
                {
                    sb.Remove(sb.Length - 1, 1);
                }
            }

            while (sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
            {
                sb.Remove(sb.Length - 1, 1);
            }
        }

        if (sb.Length > 0 && sb[^1] == ',')
        {
            sb.Remove(sb.Length - 1, 1);
        }

        while (sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
        {
            sb.Remove(sb.Length - 1, 1);
        }
    }

    private static string? ExtractFirstValidJsonBlock(string text, char openChar, char closeChar, bool isArray)
    {
        var candidates = ExtractBalancedBlocks(text, openChar, closeChar);
        foreach (var c in candidates)
        {
            if (IsValidJson(c, isArray)) return c;
        }

        if (candidates.Count == 0)
        {
            int firstIdx = text.IndexOf(openChar);
            if (firstIdx >= 0)
            {
                var fallbackBlocks = ExtractBalancedBlocksFrom(text, firstIdx, openChar, closeChar);
                foreach (var fb in fallbackBlocks)
                {
                    if (IsValidJson(fb, isArray)) return fb;
                }
                if (fallbackBlocks.Count > 0) return fallbackBlocks[0];
            }
        }

        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static List<string> CollectCandidates(string rawText, char openChar, char closeChar, bool isArray)
    {
        var list = new List<string>();

        // 1. 마크다운 코드블록 우선
        foreach (var fenced in ExtractCodeFenceBlocks(rawText))
        {
            if (IsValidJson(fenced, isArray))
            {
                list.Add(fenced);
            }
            else
            {
                foreach (var b in ExtractBalancedBlocks(fenced, openChar, closeChar))
                {
                    if (IsValidJson(b, isArray) && !list.Contains(b)) list.Add(b);
                }
            }
        }

        // 2. 전체 텍스트에서 균형 잡힌 블록 수집
        foreach (var b in ExtractBalancedBlocks(rawText, openChar, closeChar))
        {
            if (!list.Contains(b)) list.Add(b);
        }

        // 3. 인덱스 0부터 탐색 시 사설 따옴표로 인한 블록 누락 방지용 Fallback
        if (list.Count == 0)
        {
            int idx = rawText.IndexOf(openChar);
            if (idx >= 0)
            {
                foreach (var b in ExtractBalancedBlocksFrom(rawText, idx, openChar, closeChar))
                {
                    if (!list.Contains(b)) list.Add(b);
                }
            }
        }

        return list;
    }

    private static List<string> ExtractBalancedBlocks(string rawText, char openChar, char closeChar)
    {
        var blocks = new List<string>();
        if (string.IsNullOrWhiteSpace(rawText)) return blocks;

        int openCount = 0;
        int startIndex = -1;
        bool inString = false;
        bool isEscaped = false;

        for (int i = 0; i < rawText.Length; i++)
        {
            char c = rawText[i];

            if (inString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else
            {
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == openChar)
                {
                    if (openCount == 0)
                    {
                        startIndex = i;
                    }
                    openCount++;
                }
                else if (c == closeChar && openCount > 0)
                {
                    openCount--;
                    if (openCount == 0 && startIndex >= 0)
                    {
                        blocks.Add(rawText[startIndex..(i + 1)]);
                        startIndex = -1;
                    }
                }
            }
        }

        return blocks;
    }

    private static List<string> ExtractBalancedBlocksFrom(string rawText, int startFrom, char openChar, char closeChar)
    {
        var blocks = new List<string>();
        if (string.IsNullOrWhiteSpace(rawText) || startFrom < 0 || startFrom >= rawText.Length) return blocks;

        int openCount = 0;
        int startIndex = -1;
        bool inString = false;
        bool isEscaped = false;

        for (int i = startFrom; i < rawText.Length; i++)
        {
            char c = rawText[i];

            if (inString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else
            {
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == openChar)
                {
                    if (openCount == 0)
                    {
                        startIndex = i;
                    }
                    openCount++;
                }
                else if (c == closeChar && openCount > 0)
                {
                    openCount--;
                    if (openCount == 0 && startIndex >= 0)
                    {
                        blocks.Add(rawText[startIndex..(i + 1)]);
                        startIndex = -1;
                    }
                }
            }
        }

        return blocks;
    }

    private static List<string> ExtractCodeFenceBlocks(string rawText)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(rawText)) return list;

        int searchStart = 0;
        while (searchStart < rawText.Length)
        {
            int fenceStart = rawText.IndexOf("```", searchStart, StringComparison.Ordinal);
            if (fenceStart < 0) break;

            int contentStart = rawText.IndexOf('\n', fenceStart);
            if (contentStart < 0)
            {
                contentStart = fenceStart + 3;
            }
            else
            {
                contentStart++;
            }

            int fenceEnd = rawText.IndexOf("```", contentStart, StringComparison.Ordinal);
            if (fenceEnd >= 0)
            {
                string fencedContent = rawText[contentStart..fenceEnd].Trim();
                if (!string.IsNullOrEmpty(fencedContent))
                {
                    list.Add(fencedContent);
                }
                searchStart = fenceEnd + 3;
            }
            else
            {
                string fencedContent = rawText[contentStart..].Trim();
                if (!string.IsNullOrEmpty(fencedContent))
                {
                    list.Add(fencedContent);
                }
                break;
            }
        }

        return list;
    }

    private static bool IsValidJson(string candidate, bool isArray)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            using var doc = JsonDocument.Parse(candidate, LenientDocOptions);
            return isArray
                ? doc.RootElement.ValueKind == JsonValueKind.Array
                : doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }
}