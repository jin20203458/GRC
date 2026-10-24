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
    private const int MaxSlidingAttempts = 16;

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

    private enum CommentState
    {
        None,
        SingleLine,
        MultiLine
    }

    private enum ContainerKind
    {
        Object,
        Array
    }

    private enum ContainerState
    {
        ObjExpectingKeyOrEnd,
        ObjExpectingKey,
        ObjInKey,
        ObjExpectingColon,
        ObjExpectingValue,
        ObjInValueString,
        ObjInValueToken,
        ObjExpectingCommaOrEnd,

        ArrExpectingElemOrEnd,
        ArrExpectingElem,
        ArrInElemString,
        ArrInElemToken,
        ArrExpectingCommaOrEnd
    }

    private struct ContainerFrame
    {
        public ContainerKind Kind;
        public ContainerState State;
        public int SafeRollbackLength;
        public int TokenStartIndex;
    }

    /// <summary>
    /// LLM의 응답 텍스트에서 마크다운 코드블록이나 불필요한 사설을 무시하고 유효한 단일 JSON 객체({ ... }) 문자열을 추출합니다.
    /// </summary>
    public static string? ExtractJson(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        // 1. 마크다운 코드블록(```json ... ```) 우선 탐색
        var fencedBlocks = ExtractCodeFenceBlocks(rawText, out var fenceSpans);
        foreach (string fenced in fencedBlocks)
        {
            string? candidate = ExtractFirstValidJsonBlock(fenced, '{', '}', isArray: false);
            if (candidate != null) return candidate;
        }

        // 2. 전체 텍스트(코드블록 외부)에서 균형 잡힌 { ... } 블록 추출
        string? directCandidate = ExtractFirstValidJsonBlock(rawText, '{', '}', isArray: false, fenceSpans);
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
        var fencedBlocks = ExtractCodeFenceBlocks(rawText, out var fenceSpans);
        foreach (string fenced in fencedBlocks)
        {
            string? candidate = ExtractFirstValidJsonBlock(fenced, '[', ']', isArray: true);
            if (candidate != null) return candidate;
        }

        // 2. 전체 텍스트(코드블록 외부)에서 균형 잡힌 [ ... ] 블록 추출
        string? directCandidate = ExtractFirstValidJsonBlock(rawText, '[', ']', isArray: true, fenceSpans);
        if (directCandidate != null) return directCandidate;

        // 3. 잘린 JSON 배열 복구 시도
        return RepairTruncatedJson(rawText, '[', ']');
    }

    /// <summary>
    /// LLM 응답에서 JSON 객체/배열을 안전하게 추출하고 역직렬화합니다.
    /// 대상 타입(T)의 컬렉션 여부에 따라 탐색 순서를 최적화하고 절단 복구를 지원합니다.
    /// </summary>
    public static T? DeserializeSafe<T>(string rawText, JsonSerializerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return default;

        var targetOptions = options ?? LenientOptions;
        bool isCollectionTarget = IsArrayOrCollectionType(typeof(T));

        if (isCollectionTarget)
        {
            // 1. 컬렉션 타입인 경우 배열 후보 우선 탐색
            var arrayCandidates = CollectCandidates(rawText, '[', ']', isArray: true);
            foreach (var json in arrayCandidates)
            {
                try
                {
                    var result = JsonSerializer.Deserialize<T>(json, targetOptions);
                    if (result != null) return result;
                }
                catch (JsonException ex)
                {
                    Debug.WriteLine($"[LlmJsonParser] Array candidate deserialization failed: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LlmJsonParser] Array candidate unexpected error: {ex.Message}");
                }
            }

            // 2. 잘린 JSON 배열 복구 시도
            string? repairedArray = RepairTruncatedJson(rawText, '[', ']');
            if (repairedArray != null)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(repairedArray, targetOptions);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LlmJsonParser] Repaired array deserialization failed: {ex.Message}");
                }
            }

            // 3. 객체 후보 Fallback (단일 래퍼 객체인 경우 등)
            var objCandidates = CollectCandidates(rawText, '{', '}', isArray: false);
            foreach (var json in objCandidates)
            {
                try
                {
                    var result = JsonSerializer.Deserialize<T>(json, targetOptions);
                    if (result != null) return result;
                }
                catch
                {
                }
            }

            string? repairedObj = RepairTruncatedJson(rawText, '{', '}');
            if (repairedObj != null)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(repairedObj, targetOptions);
                }
                catch
                {
                }
            }
        }
        else
        {
            // 1. 객체 후보군 순차 역직렬화 시도
            T? fallbackResult = default;
            var candidates = CollectCandidates(rawText, '{', '}', isArray: false);
            foreach (var json in candidates)
            {
                try
                {
                    var result = JsonSerializer.Deserialize<T>(json, targetOptions);
                    if (result != null)
                    {
                        if (HasMatchingSchemaProperties(json, typeof(T)))
                        {
                            return result;
                        }
                        fallbackResult ??= result;
                    }
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

            // 2. 잘린 JSON 객체 복구 후 역직렬화 시도
            string? repaired = RepairTruncatedJson(rawText, '{', '}');
            if (repaired != null)
            {
                try
                {
                    var result = JsonSerializer.Deserialize<T>(repaired, targetOptions);
                    if (result != null)
                    {
                        if (HasMatchingSchemaProperties(repaired, typeof(T)))
                        {
                            return result;
                        }
                        fallbackResult ??= result;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LlmJsonParser] Repaired object deserialization failed: {ex.Message}");
                }
            }

            if (fallbackResult != null) return fallbackResult;

            // 3. T가 object/JsonElement 등이거나 배열 래핑인 경우 배열 후보군 탐색
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
                }
            }

            string? repairedArr = RepairTruncatedJson(rawText, '[', ']');
            if (repairedArr != null)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(repairedArr, targetOptions);
                }
                catch
                {
                }
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
        bool isArray = openChar == '[';

        // 1. 마크다운 코드블록(특히 마지막 미완성 코드블록) 우선 복구
        var fencedBlocks = ExtractCodeFenceBlocks(rawText);
        for (int i = fencedBlocks.Count - 1; i >= 0; i--)
        {
            string? repairedFenced = RepairTruncatedJsonCore(fencedBlocks[i], openChar, isArray);
            if (repairedFenced != null) return repairedFenced;
        }

        // 2. 전체 원본 텍스트 기준 복구
        return RepairTruncatedJsonCore(rawText, openChar, isArray);
    }

    private static string? RepairTruncatedJsonCore(string text, char openChar, bool isArray)
    {
        int searchPos = 0;
        int attempts = 0;

        while (searchPos < text.Length && attempts <= MaxSlidingAttempts)
        {
            int startIndex = text.IndexOf(openChar, searchPos);
            if (startIndex < 0) break;

            if (!isArray && !LooksLikeJsonObjectStart(text, startIndex))
            {
                searchPos = startIndex + 1;
                continue;
            }

            attempts++;
            string? repaired = TryRepairFromIndex(text, startIndex, openChar, isArray, out _);
            if (repaired != null) return repaired;

            searchPos = startIndex + 1;
        }

        return null;
    }

    private static string? TryRepairFromIndex(
        string text,
        int startIndex,
        char openChar,
        bool isArray,
        out bool hadMidStreamSyntaxError)
    {
        hadMidStreamSyntaxError = false;
        var stack = new List<ContainerFrame>();
        var sb = new StringBuilder(text.Length - startIndex + 16);

        bool inString = false;
        bool isEscaped = false;
        CommentState commentState = CommentState.None;

        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];

            // 1. 주석 처리 (sb에 추가하지 않음)
            if (commentState == CommentState.SingleLine)
            {
                if (c == '\n')
                {
                    commentState = CommentState.None;
                    sb.Append('\n');
                }
                continue;
            }
            if (commentState == CommentState.MultiLine)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    commentState = CommentState.None;
                    i++;
                }
                continue;
            }

            // 2. 문자열 내부
            if (inString)
            {
                sb.Append(c);
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
                    if (stack.Count > 0)
                    {
                        var top = stack[^1];
                        if (top.State == ContainerState.ObjInKey)
                            top.State = ContainerState.ObjExpectingColon;
                        else if (top.State == ContainerState.ObjInValueString)
                            top.State = ContainerState.ObjExpectingCommaOrEnd;
                        else if (top.State == ContainerState.ArrInElemString)
                            top.State = ContainerState.ArrExpectingCommaOrEnd;
                        stack[^1] = top;
                    }
                }
                continue;
            }

            // 3. 문자열 외부 주석 시작
            if (c == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
            {
                if (stack.Count > 0 && !CompletePrimitiveTokenIfActive(sb, stack))
                {
                    hadMidStreamSyntaxError = true;
                    return null;
                }
                commentState = text[i + 1] == '/' ? CommentState.SingleLine : CommentState.MultiLine;
                i++;
                continue;
            }

            // 3-1. 문자열/주석 외부에서 닫는 마크다운 코드블록(```) 도달 시 절단 지점으로 간주
            if (c == '`' && i + 2 < text.Length && text[i + 1] == '`' && text[i + 2] == '`')
            {
                break;
            }

            // 4. 공백 처리
            if (char.IsWhiteSpace(c))
            {
                if (stack.Count > 0 && !CompletePrimitiveTokenIfActive(sb, stack))
                {
                    hadMidStreamSyntaxError = true;
                    return null;
                }
                sb.Append(c);
                continue;
            }

            // 5. 최초 루트 괄호 시작
            if (stack.Count == 0)
            {
                if (c != openChar)
                {
                    hadMidStreamSyntaxError = true;
                    return null;
                }
                sb.Append(c);
                stack.Add(new ContainerFrame
                {
                    Kind = isArray ? ContainerKind.Array : ContainerKind.Object,
                    State = isArray ? ContainerState.ArrExpectingElemOrEnd : ContainerState.ObjExpectingKeyOrEnd,
                    SafeRollbackLength = sb.Length,
                    TokenStartIndex = -1
                });
                continue;
            }

            // 6. 구분자(',', ':', '}', ']') 도달 시 진행 중인 리터럴/숫자 토큰 검증
            if (c == ',' || c == ':' || c == '}' || c == ']')
            {
                if (!CompletePrimitiveTokenIfActive(sb, stack))
                {
                    hadMidStreamSyntaxError = true;
                    return null;
                }
            }

            var current = stack[^1];

            // 7. 상태 머신 전이
            switch (current.State)
            {
                case ContainerState.ObjExpectingKeyOrEnd:
                    if (c == '}')
                    {
                        sb.Append(c);
                        stack.RemoveAt(stack.Count - 1);
                        AdvanceParentAfterChildComplete(stack);
                    }
                    else if (c == '"')
                    {
                        current.State = ContainerState.ObjInKey;
                        stack[^1] = current;
                        sb.Append(c);
                        inString = true;
                    }
                    else
                    {
                        hadMidStreamSyntaxError = true;
                        return null;
                    }
                    break;

                case ContainerState.ObjExpectingKey:
                    if (c == '}')
                    {
                        sb.Append(c);
                        stack.RemoveAt(stack.Count - 1);
                        AdvanceParentAfterChildComplete(stack);
                    }
                    else if (c == '"')
                    {
                        current.State = ContainerState.ObjInKey;
                        stack[^1] = current;
                        sb.Append(c);
                        inString = true;
                    }
                    else
                    {
                        hadMidStreamSyntaxError = true;
                        return null;
                    }
                    break;

                case ContainerState.ObjExpectingColon:
                    if (c == ':')
                    {
                        current.State = ContainerState.ObjExpectingValue;
                        stack[^1] = current;
                        sb.Append(c);
                    }
                    else
                    {
                        hadMidStreamSyntaxError = true;
                        return null;
                    }
                    break;

                case ContainerState.ObjExpectingValue:
                    if (!TryStartValueOrElement(c, isObjectProperty: true, sb, stack, ref inString))
                    {
                        hadMidStreamSyntaxError = true;
                        return null;
                    }
                    break;

                case ContainerState.ObjExpectingCommaOrEnd:
                    if (c == ',')
                    {
                        current.SafeRollbackLength = sb.Length;
                        current.State = ContainerState.ObjExpectingKey;
                        stack[^1] = current;
                        sb.Append(c);
                    }
                    else if (c == '}')
                    {
                        sb.Append(c);
                        stack.RemoveAt(stack.Count - 1);
                        AdvanceParentAfterChildComplete(stack);
                    }
                    else
                    {
                        hadMidStreamSyntaxError = true;
                        return null;
                    }
                    break;

                case ContainerState.ArrExpectingElemOrEnd:
                case ContainerState.ArrExpectingElem:
                    if (c == ']')
                    {
                        sb.Append(c);
                        stack.RemoveAt(stack.Count - 1);
                        AdvanceParentAfterChildComplete(stack);
                    }
                    else if (!TryStartValueOrElement(c, isObjectProperty: false, sb, stack, ref inString))
                    {
                        hadMidStreamSyntaxError = true;
                        return null;
                    }
                    break;

                case ContainerState.ArrExpectingCommaOrEnd:
                    if (c == ',')
                    {
                        current.SafeRollbackLength = sb.Length;
                        current.State = ContainerState.ArrExpectingElem;
                        stack[^1] = current;
                        sb.Append(c);
                    }
                    else if (c == ']')
                    {
                        sb.Append(c);
                        stack.RemoveAt(stack.Count - 1);
                        AdvanceParentAfterChildComplete(stack);
                    }
                    else
                    {
                        hadMidStreamSyntaxError = true;
                        return null;
                    }
                    break;

                case ContainerState.ObjInValueToken:
                case ContainerState.ArrInElemToken:
                    if (IsAllowedPrimitiveChar(c))
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        hadMidStreamSyntaxError = true;
                        return null;
                    }
                    break;

                default:
                    hadMidStreamSyntaxError = true;
                    return null;
            }

            if (stack.Count == 0)
            {
                return null; // 정상적으로 완전히 닫힘 -> 잘린 구조가 아님
            }
        }

        if (stack.Count == 0) return null;

        // 8. EOF 도달 시 롤백 및 정제
        var topFrame = stack[^1];

        if (inString)
        {
            if (topFrame.State == ContainerState.ObjInKey)
            {
                sb.Length = topFrame.SafeRollbackLength;
            }
            else
            {
                CloseTruncatedString(sb, isEscaped);
            }
        }
        else
        {
            switch (topFrame.State)
            {
                case ContainerState.ObjExpectingKey:
                case ContainerState.ObjExpectingColon:
                case ContainerState.ObjExpectingValue:
                case ContainerState.ArrExpectingElem:
                    sb.Length = topFrame.SafeRollbackLength;
                    break;

                case ContainerState.ObjInValueToken:
                case ContainerState.ArrInElemToken:
                    ReadOnlySpan<char> tokenSpan = sb.ToString().AsSpan(topFrame.TokenStartIndex);
                    if (!IsCompleteJsonPrimitive(tokenSpan))
                    {
                        sb.Length = topFrame.SafeRollbackLength;
                    }
                    break;
            }

            while (sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
            {
                sb.Length--;
            }
        }

        // 9. LIFO 역순으로 열린 컨테이너 닫기
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            sb.Append(stack[i].Kind == ContainerKind.Object ? '}' : ']');
        }

        string candidate = sb.ToString();
        return IsValidJson(candidate, isArray) ? candidate : null;
    }

    private static void CloseTruncatedString(StringBuilder sb, bool isEscaped)
    {
        if (isEscaped && sb.Length > 0 && sb[^1] == '\\')
        {
            sb.Remove(sb.Length - 1, 1);
        }

        RemoveIncompleteUnicodeEscape(sb);
        RemoveTrailingUnpairedHighSurrogate(sb);

        sb.Append('"');
    }

    private static void RemoveIncompleteUnicodeEscape(StringBuilder sb)
    {
        int hexCount = 0;
        int idx = sb.Length - 1;
        while (idx >= 0 && hexCount < 4 && IsHexDigit(sb[idx]))
        {
            hexCount++;
            idx--;
        }

        if (hexCount < 4 && idx >= 1 && sb[idx] == 'u' && sb[idx - 1] == '\\')
        {
            int backslashCount = CountPrecedingBackslashes(sb, idx - 1);
            if (backslashCount % 2 == 1)
            {
                int removeStart = idx - 1;
                sb.Remove(removeStart, sb.Length - removeStart);
            }
        }
    }

    private static void RemoveTrailingUnpairedHighSurrogate(StringBuilder sb)
    {
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1]))
        {
            sb.Remove(sb.Length - 1, 1);
            return;
        }

        if (sb.Length >= 6 && sb[^6] == '\\' && sb[^5] == 'u')
        {
            int backslashCount = CountPrecedingBackslashes(sb, sb.Length - 6);
            if (backslashCount % 2 == 1 &&
                IsHexDigit(sb[^4]) && IsHexDigit(sb[^3]) && IsHexDigit(sb[^2]) && IsHexDigit(sb[^1]))
            {
                string hex = sb.ToString(sb.Length - 4, 4);
                if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ushort codeUnit) &&
                    codeUnit >= 0xD800 && codeUnit <= 0xDBFF)
                {
                    sb.Remove(sb.Length - 6, 6);
                }
            }
        }
    }

    private static int CountPrecedingBackslashes(StringBuilder sb, int backslashIndex)
    {
        int count = 0;
        for (int i = backslashIndex; i >= 0 && sb[i] == '\\'; i--)
        {
            count++;
        }
        return count;
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsAllowedPrimitiveChar(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
        c == '-' || c == '+' || c == '.';

    private static bool IsCompleteJsonPrimitive(ReadOnlySpan<char> token)
    {
        if (token.IsEmpty) return false;
        if (token.SequenceEqual("true") || token.SequenceEqual("false") || token.SequenceEqual("null"))
            return true;

        int i = 0;
        if (token[i] == '-')
        {
            i++;
            if (i >= token.Length) return false;
        }

        if (token[i] == '0')
        {
            i++;
            if (i < token.Length && char.IsAsciiDigit(token[i])) return false;
        }
        else if (token[i] >= '1' && token[i] <= '9')
        {
            while (i < token.Length && char.IsAsciiDigit(token[i])) i++;
        }
        else
        {
            return false;
        }

        if (i < token.Length && token[i] == '.')
        {
            i++;
            if (i >= token.Length || !char.IsAsciiDigit(token[i])) return false;
            while (i < token.Length && char.IsAsciiDigit(token[i])) i++;
        }

        if (i < token.Length && (token[i] == 'e' || token[i] == 'E'))
        {
            i++;
            if (i < token.Length && (token[i] == '+' || token[i] == '-')) i++;
            if (i >= token.Length || !char.IsAsciiDigit(token[i])) return false;
            while (i < token.Length && char.IsAsciiDigit(token[i])) i++;
        }

        return i == token.Length;
    }

    private static bool TryStartValueOrElement(
        char c,
        bool isObjectProperty,
        StringBuilder sb,
        List<ContainerFrame> stack,
        ref bool inString)
    {
        var current = stack[^1];
        if (c == '"')
        {
            current.State = isObjectProperty ? ContainerState.ObjInValueString : ContainerState.ArrInElemString;
            stack[^1] = current;
            sb.Append(c);
            inString = true;
            return true;
        }
        if (c == '{')
        {
            sb.Append(c);
            stack.Add(new ContainerFrame
            {
                Kind = ContainerKind.Object,
                State = ContainerState.ObjExpectingKeyOrEnd,
                SafeRollbackLength = sb.Length,
                TokenStartIndex = -1
            });
            return true;
        }
        if (c == '[')
        {
            sb.Append(c);
            stack.Add(new ContainerFrame
            {
                Kind = ContainerKind.Array,
                State = ContainerState.ArrExpectingElemOrEnd,
                SafeRollbackLength = sb.Length,
                TokenStartIndex = -1
            });
            return true;
        }
        if (c == '-' || char.IsAsciiDigit(c) || c == 't' || c == 'f' || c == 'n')
        {
            current.State = isObjectProperty ? ContainerState.ObjInValueToken : ContainerState.ArrInElemToken;
            current.TokenStartIndex = sb.Length;
            stack[^1] = current;
            sb.Append(c);
            return true;
        }
        return false;
    }

    private static bool CompletePrimitiveTokenIfActive(StringBuilder sb, List<ContainerFrame> stack)
    {
        var current = stack[^1];
        if (current.State != ContainerState.ObjInValueToken && current.State != ContainerState.ArrInElemToken)
            return true;

        ReadOnlySpan<char> tokenSpan = sb.ToString().AsSpan(current.TokenStartIndex);
        if (!IsCompleteJsonPrimitive(tokenSpan))
            return false;

        current.State = current.Kind == ContainerKind.Object
            ? ContainerState.ObjExpectingCommaOrEnd
            : ContainerState.ArrExpectingCommaOrEnd;
        current.TokenStartIndex = -1;
        stack[^1] = current;
        return true;
    }

    private static void AdvanceParentAfterChildComplete(List<ContainerFrame> stack)
    {
        if (stack.Count == 0) return;
        var parent = stack[^1];
        if (parent.State == ContainerState.ObjExpectingValue)
            parent.State = ContainerState.ObjExpectingCommaOrEnd;
        else if (parent.State == ContainerState.ArrExpectingElemOrEnd || parent.State == ContainerState.ArrExpectingElem)
            parent.State = ContainerState.ArrExpectingCommaOrEnd;
        stack[^1] = parent;
    }

    private static string? ExtractFirstValidJsonBlock(
        string text,
        char openChar,
        char closeChar,
        bool isArray,
        List<(int Start, int End)>? skipSpans = null)
    {
        var validBlocks = ScanValidBalancedBlocks(text, openChar, closeChar, isArray, skipSpans);
        return validBlocks.Count > 0 ? validBlocks[0] : null;
    }

    private static List<string> CollectCandidates(string rawText, char openChar, char closeChar, bool isArray)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var fencedBlocks = ExtractCodeFenceBlocks(rawText, out var fenceSpans);
        foreach (var fenced in fencedBlocks)
        {
            if (seen.Add(fenced) && IsValidJson(fenced, isArray))
            {
                list.Add(fenced);
            }
            else
            {
                foreach (var b in ScanValidBalancedBlocks(fenced, openChar, closeChar, isArray))
                {
                    if (seen.Add(b)) list.Add(b);
                }
            }
        }

        foreach (var b in ScanValidBalancedBlocks(rawText, openChar, closeChar, isArray, fenceSpans))
        {
            if (seen.Add(b)) list.Add(b);
        }

        return list;
    }

    private static List<string> ScanValidBalancedBlocks(
        string text,
        char openChar,
        char closeChar,
        bool isArray,
        List<(int Start, int End)>? skipSpans = null)
    {
        var validBlocks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return validBlocks;

        int searchPos = 0;
        int slidingAttempts = 0;

        while (searchPos < text.Length)
        {
            int startIdx = text.IndexOf(openChar, searchPos);
            if (startIdx < 0) break;

            if (skipSpans != null && TryGetContainingSpanEnd(skipSpans, startIdx, out int spanEnd))
            {
                searchPos = spanEnd;
                continue;
            }

            if (!isArray && !LooksLikeJsonObjectStart(text, startIdx))
            {
                searchPos = startIdx + 1;
                continue;
            }

            if (TryExtractSingleBalancedBlock(text, startIdx, openChar, closeChar, out int endIdx, out bool reachedEofUnclosed))
            {
                string candidate = text[startIdx..(endIdx + 1)];
                if (IsValidJson(candidate, isArray))
                {
                    validBlocks.Add(candidate);
                    searchPos = endIdx + 1;
                    continue;
                }

                searchPos = startIdx + 1;
            }
            else if (reachedEofUnclosed)
            {
                // Root Truncation Guard: startIdx부터 복구 가능한 루트 JSON인 경우 내부 블록 오추출 방지
                string? repairedRoot = TryRepairFromIndex(text, startIdx, openChar, isArray, out _);
                if (repairedRoot != null)
                {
                    break;
                }

                slidingAttempts++;
                if (slidingAttempts > MaxSlidingAttempts) break;
                searchPos = startIdx + 1;
            }
            else
            {
                searchPos = startIdx + 1;
            }
        }

        return validBlocks;
    }

    private static bool TryGetContainingSpanEnd(List<(int Start, int End)> spans, int index, out int spanEnd)
    {
        for (int i = 0; i < spans.Count; i++)
        {
            if (index >= spans[i].Start && index < spans[i].End)
            {
                spanEnd = spans[i].End;
                return true;
            }
        }
        spanEnd = -1;
        return false;
    }

    private static bool LooksLikeJsonObjectStart(string text, int openBraceIdx)
    {
        int i = SkipWhitespaceAndComments(text, openBraceIdx + 1);
        if (i >= text.Length) return true;
        return text[i] == '"' || text[i] == '}';
    }

    private static int SkipWhitespaceAndComments(string text, int index)
    {
        int i = index;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    i += 2;
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    i = (i + 1 < text.Length) ? i + 2 : text.Length;
                    continue;
                }
            }
            break;
        }
        return i;
    }

    private static bool TryExtractSingleBalancedBlock(
        string text,
        int startIdx,
        char openChar,
        char closeChar,
        out int endIdx,
        out bool reachedEofUnclosed)
    {
        endIdx = -1;
        reachedEofUnclosed = false;

        var stack = new List<char>();
        bool inString = false;
        bool isEscaped = false;
        CommentState commentState = CommentState.None;

        for (int i = startIdx; i < text.Length; i++)
        {
            char c = text[i];

            if (commentState == CommentState.SingleLine)
            {
                if (c == '\n') commentState = CommentState.None;
                continue;
            }
            if (commentState == CommentState.MultiLine)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    commentState = CommentState.None;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                if (isEscaped) isEscaped = false;
                else if (c == '\\') isEscaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    commentState = CommentState.SingleLine;
                    i++;
                    continue;
                }
                if (text[i + 1] == '*')
                {
                    commentState = CommentState.MultiLine;
                    i++;
                    continue;
                }
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                stack.Add('}');
            }
            else if (c == '[')
            {
                stack.Add(']');
            }
            else if (c == '}' || c == ']')
            {
                if (stack.Count == 0 || stack[^1] != c)
                {
                    return false;
                }
                stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0)
                {
                    endIdx = i;
                    return (c == closeChar);
                }
            }
        }

        reachedEofUnclosed = stack.Count > 0 || inString || commentState != CommentState.None;
        return false;
    }

    private static List<string> ExtractCodeFenceBlocks(string rawText) =>
        ExtractCodeFenceBlocks(rawText, out _);

    private static List<string> ExtractCodeFenceBlocks(string rawText, out List<(int Start, int End)> fenceSpans)
    {
        var list = new List<string>();
        fenceSpans = new List<(int Start, int End)>();
        if (string.IsNullOrWhiteSpace(rawText)) return list;

        int searchStart = 0;
        while (searchStart < rawText.Length)
        {
            int fenceStart = FindNextCodeFence(rawText, searchStart, insideFence: false);
            if (fenceStart < 0) break;

            int afterOpenFence = fenceStart + 3;
            int lineEnd = rawText.IndexOf('\n', afterOpenFence);
            int nextFence = FindNextCodeFence(rawText, afterOpenFence, insideFence: true);

            // 단일 행 코드블록 (예: ```json {"id": 1} ```)
            if (nextFence >= 0 && (lineEnd < 0 || nextFence < lineEnd))
            {
                string inlineContent = StripLeadingLanguageTag(rawText[afterOpenFence..nextFence]).Trim();
                if (!string.IsNullOrEmpty(inlineContent))
                {
                    list.Add(inlineContent);
                }
                fenceSpans.Add((fenceStart, nextFence + 3));
                searchStart = nextFence + 3;
                continue;
            }

            // 다중 행 코드블록
            int contentStart;
            if (lineEnd < 0)
            {
                contentStart = FindContentStartInFirstLine(rawText, afterOpenFence, rawText.Length);
            }
            else
            {
                int jsonCharIdx = FindFirstJsonBracketInRange(rawText, afterOpenFence, lineEnd);
                contentStart = jsonCharIdx >= 0 ? jsonCharIdx : lineEnd + 1;
            }

            int fenceEnd = FindNextCodeFence(rawText, contentStart, insideFence: true);
            if (fenceEnd >= 0)
            {
                string fencedContent = rawText[contentStart..fenceEnd].Trim();
                if (!string.IsNullOrEmpty(fencedContent))
                {
                    list.Add(fencedContent);
                }
                fenceSpans.Add((fenceStart, fenceEnd + 3));
                searchStart = fenceEnd + 3;
            }
            else
            {
                string fencedContent = rawText[contentStart..].Trim();
                if (!string.IsNullOrEmpty(fencedContent))
                {
                    list.Add(fencedContent);
                }
                fenceSpans.Add((fenceStart, rawText.Length));
                break;
            }
        }

        return list;
    }

    /// <summary>
    /// JSON 문자열 리터럴 내부에 포함된 백틱(```)을 코드블록 경계로 오인하지 않도록 추적하며 다음 ``` 위치를 반환합니다.
    /// </summary>
    private static int FindNextCodeFence(string text, int startIndex, bool insideFence)
    {
        int bracketDepth = insideFence ? 1 : 0;
        bool inString = false;
        bool isEscaped = false;
        CommentState commentState = CommentState.None;

        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];

            if (commentState == CommentState.SingleLine)
            {
                if (c == '\n') commentState = CommentState.None;
                continue;
            }
            if (commentState == CommentState.MultiLine)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    commentState = CommentState.None;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                if (c == '\n')
                {
                    inString = false;
                    isEscaped = false;
                }
                else if (isEscaped)
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
                continue;
            }

            if (c == '`' && i + 2 < text.Length && text[i + 1] == '`' && text[i + 2] == '`')
            {
                return i;
            }

            if (bracketDepth > 0)
            {
                if (c == '/' && i + 1 < text.Length)
                {
                    if (text[i + 1] == '/')
                    {
                        commentState = CommentState.SingleLine;
                        i++;
                        continue;
                    }
                    if (text[i + 1] == '*')
                    {
                        commentState = CommentState.MultiLine;
                        i++;
                        continue;
                    }
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{' || c == '[')
                {
                    bracketDepth++;
                }
                else if (c == '}' || c == ']')
                {
                    if (bracketDepth > (insideFence ? 1 : 0))
                    {
                        bracketDepth--;
                    }
                    else if (!insideFence)
                    {
                        bracketDepth = 0;
                    }
                }
            }
            else
            {
                if ((c == '{' && LooksLikeJsonObjectStart(text, i)) || c == '[')
                {
                    bracketDepth = 1;
                }
            }
        }

        return -1;
    }

    private static int FindFirstJsonBracketInRange(string text, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (text[i] == '{' || text[i] == '[') return i;
        }
        return -1;
    }

    private static int FindContentStartInFirstLine(string text, int start, int end)
    {
        int bracketIdx = FindFirstJsonBracketInRange(text, start, end);
        return bracketIdx >= 0 ? bracketIdx : start;
    }

    private static string StripLeadingLanguageTag(string inlineText)
    {
        int i = 0;
        while (i < inlineText.Length && (char.IsLetterOrDigit(inlineText[i]) || inlineText[i] == '-' || inlineText[i] == '_'))
        {
            i++;
        }
        if (i > 0 && i < inlineText.Length && (char.IsWhiteSpace(inlineText[i]) || inlineText[i] == '{' || inlineText[i] == '['))
        {
            return inlineText[i..];
        }
        return inlineText;
    }

    private static bool IsDictionaryType(Type type)
    {
        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type)) return true;

        if (type.IsGenericType)
        {
            var genDef = type.GetGenericTypeDefinition();
            if (genDef == typeof(IDictionary<,>) || genDef == typeof(IReadOnlyDictionary<,>) || genDef == typeof(Dictionary<,>))
                return true;
        }
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType)
            {
                var genDef = iface.GetGenericTypeDefinition();
                if (genDef == typeof(IDictionary<,>) || genDef == typeof(IReadOnlyDictionary<,>))
                    return true;
            }
        }

        return false;
    }

    private static bool IsArrayOrCollectionType(Type type)
    {
        if (type == typeof(string) || IsDictionaryType(type)) return false;
        if (type.IsArray) return true;
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) return true;

        return false;
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

    private static bool HasMatchingSchemaProperties(string json, Type targetType)
    {
        if (targetType == typeof(object) ||
            targetType == typeof(JsonElement) ||
            targetType == typeof(JsonDocument) ||
            typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(targetType) ||
            IsDictionaryType(targetType) ||
            IsArrayOrCollectionType(targetType))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, LenientDocOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;

            var props = targetType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (props.Length == 0) return true;

            foreach (var jsonProp in doc.RootElement.EnumerateObject())
            {
                for (int i = 0; i < props.Length; i++)
                {
                    if (string.Equals(jsonProp.Name, props[i].Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    var attr = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<JsonPropertyNameAttribute>(props[i]);
                    if (attr != null && string.Equals(jsonProp.Name, attr.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}