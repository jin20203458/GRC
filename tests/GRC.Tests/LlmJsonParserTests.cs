using System;
using System.Collections.Generic;
using System.Text;
using GRC.Helpers;
using GRC.Models;
using Xunit;

namespace GRC.Tests;

public class LlmJsonParserTests
{
    private record SampleUserDto(string Name, int Age, double Score);
    private struct SampleMetricStruct
    {
        public int Id { get; set; }
        public string Title { get; set; }
    }

    private class SampleComplexDto
    {
        public string Title { get; set; } = "";
        public int Count { get; set; }
        public double Ratio { get; set; }
        public AgentStep Step { get; set; }
        public List<string> Tags { get; set; } = [];
    }

    [Fact]
    public void ExtractJson_CleanObject_ReturnsExactJson()
    {
        const string input = "{\"name\": \"Arthur\", \"age\": 30, \"score\": 95.5}";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ExtractJson_MarkdownFenceWithJsonTag_ExtractsPureJson()
    {
        const string input = """
        Here is the character data:
        ```json
        {
          "name": "Merlin",
          "age": 150,
          "score": 99.9
        }
        ```
        Hope this helps!
        """;

        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.StartsWith("{", result);
        Assert.EndsWith("}", result);
        Assert.Contains("\"name\": \"Merlin\"", result);
    }

    [Fact]
    public void ExtractJson_MarkdownFenceWithoutLanguage_ExtractsPureJson()
    {
        const string input = """
        ```
        {
          "name": "Gawain",
          "age": 28,
          "score": 88.0
        }
        ```
        """;

        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Contains("\"name\": \"Gawain\"", result);
    }

    [Fact]
    public void ExtractJson_NestedBracesAndEscapedQuotes_PreservesInternalStructure()
    {
        const string input = """
        {
          "dialogue": "He said, \"Beware of {danger}!\" and smiled.",
          "metadata": {
            "depth": 2,
            "raw": "{inner_brace}"
          }
        }
        """;

        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Contains("\\\"Beware of {danger}!\\\"", result);
        Assert.Contains("\"raw\": \"{inner_brace}\"", result);
    }

    [Fact]
    public void ExtractJson_PrecedingConversationalDecoyBrace_SelectsValidJsonPayload()
    {
        const string input = "Please review format {schema_template} carefully. Payload: {\"name\": \"Lancelot\", \"age\": 32, \"score\": 91.0}";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"name\": \"Lancelot\", \"age\": 32, \"score\": 91.0}", result);
    }

    [Fact]
    public void ExtractJson_QuotedTextBeforeJson_DoesNotBreakParser()
    {
        const string input = "The instruction was \"Find {holy_grail}\": {\"name\": \"Galahad\", \"age\": 22, \"score\": 100.0}";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"name\": \"Galahad\", \"age\": 22, \"score\": 100.0}", result);
    }

    [Fact]
    public void ExtractJsonArray_CleanArray_ReturnsExactJson()
    {
        const string input = "[\"Excalibur\", \"Rhongomyniad\", \"Secace\"]";
        string? result = LlmJsonParser.ExtractJsonArray(input);

        Assert.NotNull(result);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ExtractJsonArray_WithCodeFenceAndEscapedQuotes_ExtractsCleanArray()
    {
        const string input = """
        ```json
        [
          "Choice 1: \"Attack with sword\"",
          "Choice 2: \"Retreat immediately\""
        ]
        ```
        """;

        string? result = LlmJsonParser.ExtractJsonArray(input);

        Assert.NotNull(result);
        Assert.StartsWith("[", result);
        Assert.EndsWith("]", result);
        Assert.Contains("Attack with sword", result);
    }

    [Fact]
    public void DeserializeSafe_NumbersAsStrings_DeserializesSuccessfully()
    {
        // LLM이 숫자를 문자열("42", "0.75")로 출력하는 전형적인 에지케이스 검증
        const string input = """
        {
          "title": "Knight Status",
          "count": "42",
          "ratio": "0.75",
          "step": "Planning",
          "tags": ["hero", "sword"]
        }
        """;

        var result = LlmJsonParser.DeserializeSafe<SampleComplexDto>(input);

        Assert.NotNull(result);
        Assert.Equal("Knight Status", result.Title);
        Assert.Equal(42, result.Count);
        Assert.Equal(0.75, result.Ratio);
        Assert.Equal(AgentStep.Planning, result.Step);
        Assert.Equal(2, result.Tags.Count);
    }

    [Fact]
    public void DeserializeSafe_EnumFromStringCaseInsensitive_DeserializesSuccessfully()
    {
        const string input = "{\"title\": \"Review\", \"count\": 1, \"ratio\": 1.0, \"step\": \"planReview\"}";
        var result = LlmJsonParser.DeserializeSafe<SampleComplexDto>(input);

        Assert.NotNull(result);
        Assert.Equal(AgentStep.PlanReview, result.Step);
    }

    [Fact]
    public void DeserializeSafe_CommentsAndTrailingCommas_DeserializesSuccessfully()
    {
        const string input = """
        {
          // 시스템 코멘트
          "title": "Castle",
          "count": 5, /* 성채 개수 */
          "ratio": 1.25,
          "step": "WorldviewGen",
          "tags": ["wall", "gate",],
        }
        """;

        var result = LlmJsonParser.DeserializeSafe<SampleComplexDto>(input);

        Assert.NotNull(result);
        Assert.Equal("Castle", result.Title);
        Assert.Equal(5, result.Count);
        Assert.Equal(AgentStep.WorldviewGen, result.Step);
        Assert.Equal(2, result.Tags.Count);
    }

    [Fact]
    public void DeserializeSafe_RecordType_DeserializesSuccessfully()
    {
        const string input = "{\"name\": \"Kay\", \"age\": \"35\", \"score\": \"85.0\"}";
        var result = LlmJsonParser.DeserializeSafe<SampleUserDto>(input);

        Assert.NotNull(result);
        Assert.Equal("Kay", result.Name);
        Assert.Equal(35, result.Age);
        Assert.Equal(85.0, result.Score);
    }

    [Fact]
    public void DeserializeSafe_StructType_DeserializesSuccessfully()
    {
        const string input = "{\"id\": 101, \"title\": \"HealthGauge\"}";
        var result = LlmJsonParser.DeserializeSafe<SampleMetricStruct>(input);

        Assert.Equal(101, result.Id);
        Assert.Equal("HealthGauge", result.Title);
    }

    [Fact]
    public void DeserializeSafe_TruncatedJsonObject_RepairsAndDeserializes()
    {
        // LLM 토큰 제한으로 문자열 중간에서 끊긴 경우 복구 검증
        const string truncated = """
        {
          "title": "Epic Quest",
          "count": 10,
          "ratio": 2.5,
          "step": "Planning",
          "tags": ["adventure", "dragon
        """;

        var result = LlmJsonParser.DeserializeSafe<SampleComplexDto>(truncated);

        Assert.NotNull(result);
        Assert.Equal("Epic Quest", result.Title);
        Assert.Equal(10, result.Count);
        Assert.Equal(AgentStep.Planning, result.Step);
        Assert.Contains("adventure", result.Tags);
        Assert.Contains("dragon", result.Tags);
    }

    [Fact]
    public void DeserializeArraySafe_TruncatedJsonArray_RepairsAndDeserializes()
    {
        const string truncated = "[\"Option A\", \"Option B\", \"Option C cut off";
        var result = LlmJsonParser.DeserializeArraySafe<List<string>>(truncated);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("Option A", result[0]);
        Assert.Equal("Option B", result[1]);
        Assert.Equal("Option C cut off", result[2]);
    }

    [Fact]
    public void DeserializeSafe_NullOrWhitespace_ReturnsDefault()
    {
        Assert.Null(LlmJsonParser.DeserializeSafe<SampleComplexDto>(null!));
        Assert.Null(LlmJsonParser.DeserializeSafe<SampleComplexDto>(""));
        Assert.Null(LlmJsonParser.DeserializeSafe<SampleComplexDto>("   "));
    }

    [Fact]
    public void DeserializeSafe_CorruptedGarbage_ReturnsDefaultWithoutThrowing()
    {
        const string garbage = "This is completely random text with no brackets whatsoever.";
        var result = LlmJsonParser.DeserializeSafe<SampleComplexDto>(garbage);

        Assert.Null(result);
    }

    #region Edge Case & Defect Reproduction Tests

    private record RootDto(InnerDto? Outer, string? Trunc);
    private record InnerDto(int Inner);
    private record SimpleItem(int Id, string? Title);

    [Fact]
    public void ExtractJsonArray_WithPreambleBracketAndTruncatedArray_DoesNotReturnPreambleAndRepairsArray()
    {
        // 현상 1: 서두에 [참고]가 있고 본문 JSON 배열이 잘린 경우, "[참고]"를 반환하지 않고 절단 복구 수행
        const string input = "[참고] 아래는 배열입니다:\n[{\"id\": 1, \"title\": \"test";
        string? result = LlmJsonParser.ExtractJsonArray(input);

        Assert.NotNull(result);
        Assert.StartsWith("[", result);
        Assert.EndsWith("]", result);
        Assert.DoesNotContain("참고", result);
        Assert.Contains("\"id\": 1", result);
    }

    [Fact]
    public void ExtractJson_WithUnclosedPreambleBrace_ExtractsValidJsonBlock()
    {
        // 현상 3: 서두 사설에 닫히지 않은 { 기호가 있어도 본문의 정상 JSON 추출 성공
        const string input = "주의: { 기호 사용에 유의하세요.\n{\"name\": \"Arthur\", \"age\": 30, \"score\": 95.5}";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"name\": \"Arthur\", \"age\": 30, \"score\": 95.5}", result);
    }

    [Fact]
    public void ExtractJsonArray_WithUnclosedPreambleBracket_ExtractsValidJsonArray()
    {
        // 서두 사설에 닫히지 않은 [ 기호가 있어도 본문 정상 배열 추출 성공
        const string input = "참고 [1: 아래 목록을 확인하세요.\n[\"Excalibur\", \"Rhongomyniad\"]";
        string? result = LlmJsonParser.ExtractJsonArray(input);

        Assert.NotNull(result);
        Assert.Equal("[\"Excalibur\", \"Rhongomyniad\"]", result);
    }

    [Fact]
    public void ExtractJson_TruncatedRootWithInnerCompleteObject_RepairsRootAndDoesNotReturnInnerObject()
    {
        // Sliding Start Scan 시 절단된 루트의 하위 완결 객체({"inner": 1})만 오추출하여 루트가 유실되는 현상 방지
        const string input = "{\"outer\": {\"inner\": 1}, \"trunc\": \"val";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.StartsWith("{\"outer\":", result);
        Assert.EndsWith("}", result);
        Assert.Contains("\"inner\": 1", result);
        Assert.Contains("\"trunc\": \"val\"", result);
    }

    [Fact]
    public void DeserializeSafe_TruncatedRootWithInnerCompleteObject_DeserializesRootNotInner()
    {
        // 역직렬화 시 {"inner": 1}로 빈 RootDto를 만들지 않고 루트 전체 복구 후 역직렬화
        const string input = "{\"outer\": {\"inner\": 1}, \"trunc\": \"value";
        var result = LlmJsonParser.DeserializeSafe<RootDto>(input);

        Assert.NotNull(result);
        Assert.NotNull(result.Outer);
        Assert.Equal(1, result.Outer.Inner);
        Assert.Equal("value", result.Trunc);
    }

    [Fact]
    public void ExtractJson_CommentsWithBracesAndQuotes_PreservesStructure()
    {
        // 주석 내부에 따옴표나 괄호가 포함되어도 문자열/괄호 상태 머신이 오염되지 않음
        const string input = """
        {
          // comment with } brace and " quote
          "name": "Arthur",
          /* block comment with { brace and " quote */
          "age": 30,
          "score": 95.5
        }
        """;
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Contains("\"name\": \"Arthur\"", result);
        Assert.Contains("\"age\": 30", result);
    }

    [Fact]
    public void ExtractJson_SingleLineCommentTruncatedAtEof_RepairsSuccessfully()
    {
        // 한 줄 주석 도중 개행 없이 EOF 도달 시 닫는 괄호가 주석에 삼켜지지 않음
        const string input = "{\"name\": \"Arthur\", \"age\": 30 // truncated comment";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.StartsWith("{", result);
        Assert.EndsWith("}", result);
        Assert.Contains("\"name\": \"Arthur\"", result);
    }

    [Fact]
    public void ExtractJson_MultiLineCommentTruncatedAtEof_RepairsSuccessfully()
    {
        // 미완성 블록 주석(/*) 도중 EOF 도달 시 복구 성공
        const string input = "{\"name\": \"Arthur\", \"age\": 30, /* unclosed comment";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.StartsWith("{", result);
        Assert.EndsWith("}", result);
        Assert.Contains("\"name\": \"Arthur\"", result);
    }

    [Fact]
    public void ExtractJson_TruncatedAtIncompleteUnicodeEscape_RepairsSuccessfully()
    {
        // \u12 등 4자리 미달 유니코드 이스케이프에서 절단된 경우
        const string input = "{\"name\": \"text\\u12";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"name\": \"text\"}", result);
    }

    [Fact]
    public void ExtractJson_TruncatedAtEscapedBackslash_PreservesEscapedBackslash()
    {
        // \\ 뒤에 문자열이 끝난 경우 (이스케이프 백슬래시 리터럴 유지)
        const string input = "{\"name\": \"path\\\\";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"name\": \"path\\\\\"}", result);
    }

    [Fact]
    public void ExtractJson_TruncatedAtStandaloneBackslash_RemovesBackslashAndCloses()
    {
        // 단독 \ 에서 절단된 경우
        const string input = "{\"name\": \"text\\";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"name\": \"text\"}", result);
    }

    [Fact]
    public void ExtractJson_TruncatedAtEscapedQuotesInKeyWithColon_RollsBackDanglingKey()
    {
        // Key에 \"가 포함되어 있고 콜론 직후 절단된 경우
        const string input = "{\"name\": \"Arthur\", \"my\\\"key\":";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"name\": \"Arthur\"}", result);
    }

    [Fact]
    public void ExtractJson_TruncatedDuringKey_RollsBackDanglingKey()
    {
        // Key 작성 도중 절단된 경우
        const string input = "{\"name\": \"Arthur\", \"user_na";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"name\": \"Arthur\"}", result);
    }

    [Fact]
    public void ExtractJson_TruncatedAfterKeyBeforeColon_RollsBackDanglingKey()
    {
        // Key 문자열 종료 후 콜론 직전 절단된 경우
        const string input = "{\"name\": \"Arthur\", \"user_name\"";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"name\": \"Arthur\"}", result);
    }

    [Fact]
    public void ExtractJson_TruncatedAtIncompleteLiteral_RollsBackDanglingProperty()
    {
        // 미완성 리터럴(tru, nul, fal) 절단 시 롤백
        const string input1 = "{\"name\": \"Arthur\", \"flag\": tru";
        string? result1 = LlmJsonParser.ExtractJson(input1);
        Assert.Equal("{\"name\": \"Arthur\"}", result1);

        const string input2 = "[1, fal";
        string? result2 = LlmJsonParser.ExtractJsonArray(input2);
        Assert.Equal("[1]", result2);
    }

    [Fact]
    public void ExtractJson_TruncatedAtIncompleteNumber_RollsBackDanglingProperty()
    {
        // 미완성 숫자(12., -, 1e) 절단 시 롤백
        const string input1 = "{\"name\": \"Arthur\", \"ratio\": 12.";
        string? result1 = LlmJsonParser.ExtractJson(input1);
        Assert.Equal("{\"name\": \"Arthur\"}", result1);

        const string input2 = "{\"name\": \"Arthur\", \"count\": -";
        string? result2 = LlmJsonParser.ExtractJson(input2);
        Assert.Equal("{\"name\": \"Arthur\"}", result2);
    }

    [Fact]
    public void ExtractJson_TruncatedAtCompletedLiteralOrNumber_PreservesValue()
    {
        // 완결된 리터럴/숫자 직후 절단 시 값 보존
        const string input1 = "{\"name\": \"Arthur\", \"flag\": true";
        string? result1 = LlmJsonParser.ExtractJson(input1);
        Assert.Equal("{\"name\": \"Arthur\", \"flag\": true}", result1);

        const string input2 = "{\"name\": \"Arthur\", \"count\": 42";
        string? result2 = LlmJsonParser.ExtractJson(input2);
        Assert.Equal("{\"name\": \"Arthur\", \"count\": 42}", result2);
    }

    [Fact]
    public void ExtractJson_NonJsonTextWithSingleBrace_ReturnsNull()
    {
        // JSON이 아닌 일반 사설 문장에 { 기호만 있는 경우 "{}"로 오복구하지 않고 null 반환
        const string input = "이 문장은 단지 { 기호만 포함된 일반 한국어 설명입니다.";
        string? result = LlmJsonParser.ExtractJson(input);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractCodeFenceBlocks_SingleLineFenceWithTrailingNewline_ExtractsJson()
    {
        // 한 줄 코드블록 뒤에 개행과 사설이 오는 경우 JSON 블록 증발 방지
        const string input = "```json {\"id\": 100} ```\n추가 설명 문장입니다.";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"id\": 100}", result);
    }

    [Fact]
    public void DeserializeSafe_TruncatedArray_DeserializesSuccessfully()
    {
        // DeserializeSafe<List<T>>에 절단된 배열이 전달되었을 때 복구 및 역직렬화
        const string input = "[{\"id\": 1, \"title\": \"first\"}, {\"id\": 2, \"title\": \"sec";
        var result = LlmJsonParser.DeserializeSafe<List<SimpleItem>>(input);

        Assert.NotNull(result);
        Assert.True(result.Count >= 1);
        Assert.Equal(1, result[0].Id);
        Assert.Equal("first", result[0].Title);
    }

    #endregion

    #region Realistic & Hostile Real-World LLM Edge Cases

    [Fact]
    public void DeserializeSafe_MultipleCodeFences_SelectsActualMatchingSchemaBlock()
    {
        // LLM이 예시 스키마 코드블록을 먼저 출력하고, 실제 페이로드를 두 번째 코드블록에 출력하는 현실 케이스
        const string input = """
        Here is the expected schema:
        ```json
        {
          "example": true,
          "note": "This is only an example"
        }
        ```
        And here is the actual agent plan:
        ```json
        {
          "worldviewOutline": "Dark Gothic World",
          "scenarioOutline": "The church bell tolls at midnight",
          "promptOutline": "Maintain dark fantasy tone"
        }
        ```
        Hope this satisfies the requirements!
        """;

        var result = LlmJsonParser.DeserializeSafe<AgentPlan>(input);

        Assert.NotNull(result);
        Assert.Equal("Dark Gothic World", result.WorldviewOutline);
        Assert.Equal("The church bell tolls at midnight", result.ScenarioOutline);
        Assert.Equal("Maintain dark fantasy tone", result.PromptOutline);
    }

    [Fact]
    public void DeserializeSafe_ThinkingProcessWithDecoyJson_SelectsActualPayload()
    {
        // Gemini / Claude 생각 체인(<thought>...) 내부에 가짜 템플릿 JSON이 포함된 경우
        const string input = """
        <thought>
        Let's design the character. Format is:
        {"name": "decoy", "age": 0}
        Now assembling real character:
        </thought>
        ```json
        {
          "name": "Lancelot",
          "age": 32,
          "score": 98.5
        }
        ```
        """;

        var result = LlmJsonParser.DeserializeSafe<SampleUserDto>(input);

        Assert.NotNull(result);
        Assert.Equal("Lancelot", result.Name);
        Assert.Equal(32, result.Age);
        Assert.Equal(98.5, result.Score);
    }

    [Fact]
    public void DeserializeSafe_LorebookEntries_TruncatedInThirdEntry_RecoversCompletedEntries()
    {
        // 로어북 생성 중 토큰 제한으로 3번째 항목 작성 도중 절단된 현실 케이스
        const string input = """
        [
          {
            "name": "Excalibur",
            "keywords": ["sword", "holy"],
            "content": "The legendary sword of king.",
            "category": "아이템",
            "priority": 0
          },
          {
            "name": "Avalon",
            "keywords": ["island", "utopia"],
            "content": "The blessed realm.",
            "category": "장소",
            "priority": 1
          },
          {
            "name": "Mordred",
            "keywords": ["knight", "traitor
        """;

        var result = LlmJsonParser.DeserializeArraySafe<List<LorebookEntry>>(input);

        Assert.NotNull(result);
        Assert.True(result.Count >= 2);
        Assert.Equal("Excalibur", result[0].Name);
        Assert.Equal("Avalon", result[1].Name);
        if (result.Count >= 3)
        {
            Assert.Equal("Mordred", result[2].Name);
            Assert.Contains("knight", result[2].Keywords);
        }
    }

    [Fact]
    public void DeserializeSafe_DictionaryType_TruncatedAtKeyValue_RecoversCompletedKvp()
    {
        // 커스텀 스탯 생성 중 콜론 직후 값이 누락된 채 절단된 경우: 미완성 프로퍼티 롤백
        const string input = """
        {
          "체력": "100/100",
          "마나": "50/50",
          "타락도": "15/100",
          "신앙심": 
        """;

        var result = LlmJsonParser.DeserializeSafe<Dictionary<string, string>>(input);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("100/100", result["체력"]);
        Assert.Equal("50/50", result["마나"]);
        Assert.Equal("15/100", result["타락도"]);
        Assert.False(result.ContainsKey("신앙심"));
    }

    [Fact]
    public void DeserializeSafe_DictionaryType_TruncatedInStringValue_PreservesPartialValue()
    {
        // 값 문자열 작성 중 절단된 경우: 따옴표를 닫아 부분 문자열 값 보존
        const string input = """
        {
          "체력": "100/100",
          "신앙심": "8
        """;

        var result = LlmJsonParser.DeserializeSafe<Dictionary<string, string>>(input);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("100/100", result["체력"]);
        Assert.Equal("8", result["신앙심"]);
    }

    [Fact]
    public void ExtractJson_TrailingConversationalChitChatAfterValidJson_ExtractsExactJson()
    {
        // JSON 객체 뒤에 긴 한국어 대화형 사설 및 피드백 요청이 붙은 경우
        const string input = """
        {"name": "Arthur", "age": 30, "score": 95.5}

        위 데이터는 원탁의 기사 설정을 반영하여 작성되었습니다.
        추가로 필요한 스탯이나 로어북이 있다면 언제든지 말씀해 주세요!
        """;

        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"name\": \"Arthur\", \"age\": 30, \"score\": 95.5}", result);
    }

    [Fact]
    public void ExtractJson_MathFormulaAndDecoyBracesInPreamble_ExtractsCleanJson()
    {
        // 수학 기호나 함수 설명(f(x) = { ... })이 서두에 포함된 적대적 사설
        const string input = """
        조건식 f(x) = { x + 1, if x >= 0 } 및 집합 S = { a, b, c }를 고려한 결과:
        {"id": 404, "title": "MathResult"}
        """;

        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"id\": 404, \"title\": \"MathResult\"}", result);
    }

    [Fact]
    public void ExtractJson_DeeplyNested8Levels_TruncatedAtDeepestLevel_RecoversCleanJson()
    {
        // 8단계 깊이의 중첩 객체/배열 트리에서 가장 깊은 곳에서 절단된 경우
        const string input = "{\"l1\": {\"l2\": [{\"l3\": {\"l4\": {\"l5\": [{\"l6\": {\"l7\": {\"l8\": \"deep_val";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.StartsWith("{\"l1\":", result);
        Assert.EndsWith("}", result);
        // JSONDocument로 재파싱하여 구조적 유효성 확인
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void ExtractJson_AllStandardJsonEscapes_PreservesContent()
    {
        // JSON 표준 이스케이프 문자(\", \\, \/, \b, \f, \n, \r, \t)가 모두 포함된 복합 문자열
        const string input = "{\"text\": \"Tab\\tNewline\\nQuote\\\"Slash\\/Backslash\\\\Bell\\bFormfeed\\f\"}";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ExtractJson_SurrogatePairAndEmoji_PreservesValidEmoji()
    {
        // 이모지 및 서로게이트 페어(⚔️, 🛡️, \uD83D\uDE00)
        const string input = "{\"emoji\": \"⚔️🛡️🔥\", \"surrogate\": \"\\uD83D\\uDE00\\uD83D\\uDE01\"}";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Contains("⚔️🛡️🔥", result);
        Assert.Contains("\\uD83D\\uDE00", result);
    }

    [Fact]
    public void ExtractJson_NumberVariants_FloatNegativeExponent_ParsesSuccessfully()
    {
        // 부동소수점, 음수, 지수 표기법(1e-4, 2.5E+3)
        const string input = "{\"a\": -0.5, \"b\": 1e-4, \"c\": 2.5E+3, \"d\": 0, \"e\": 10000000000}";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ExtractJson_TruncatedNumbers_RollsBackCleanly()
    {
        // 다양한 형태의 미완성 숫자 절단 롤백
        const string input1 = "{\"a\": 100, \"b\": 1.5e";
        Assert.Equal("{\"a\": 100}", LlmJsonParser.ExtractJson(input1));

        const string input2 = "{\"a\": 100, \"b\": 0.";
        Assert.Equal("{\"a\": 100}", LlmJsonParser.ExtractJson(input2));

        const string input3 = "{\"a\": 100, \"b\": -";
        Assert.Equal("{\"a\": 100}", LlmJsonParser.ExtractJson(input3));

        const string input4 = "{\"a\": 100, \"b\": 2.5E+";
        Assert.Equal("{\"a\": 100}", LlmJsonParser.ExtractJson(input4));
    }

    [Fact]
    public void ExtractJson_ArrayWithMixedNullsAndPrimitives_TruncatedAtEnd()
    {
        // null, boolean, number, string이 혼합된 배열에서 끝의 미완성 원소 롤백
        const string input = "[null, true, false, 123, \"text\", nul";
        string? result = LlmJsonParser.ExtractJsonArray(input);

        Assert.Equal("[null, true, false, 123, \"text\"]", result);
    }

    [Fact]
    public void ExtractJson_EmptyContainers_ExtractsCleanly()
    {
        // 빈 객체 및 빈 배열
        const string input1 = "{}";
        Assert.Equal("{}", LlmJsonParser.ExtractJson(input1));

        const string input2 = "[]";
        Assert.Equal("[]", LlmJsonParser.ExtractJsonArray(input2));

        const string input3 = "{\"emptyObj\": {}, \"emptyArr\": []}";
        Assert.Equal(input3, LlmJsonParser.ExtractJson(input3));
    }

    [Fact]
    public void ExtractJson_MarkdownBoldFencedJson_ExtractsCleanJson()
    {
        // 볼드체 마크다운 안에 코드블록이 감싸진 경우: **```json ... ```**
        const string input = "**```json\n{\"id\": 777, \"status\": \"bold\"}\n```**";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"id\": 777, \"status\": \"bold\"}", result);
    }

    [Fact]
    public void ExtractCodeFenceBlocks_CodeFenceWithExcessiveSpaces_ExtractsJson()
    {
        // 언어 태그 주변에 비정상적인 공백이 많은 코드블록: ```   json   \n ... ```
        const string input = "```   json   \n{\"id\": 888}\n```";
        string? result = LlmJsonParser.ExtractJson(input);

        Assert.NotNull(result);
        Assert.Equal("{\"id\": 888}", result);
    }

    [Fact]
    public void StressTest_LargePayload200Items_Truncated_RecoversFastAndValid()
    {
        // 200개 객체를 가진 대용량 배열(약 25KB)이 199번째 객체 중간에서 절단된 초고부하 케이스
        var sb = new System.Text.StringBuilder();
        sb.Append("[\n");
        for (int i = 0; i < 198; i++)
        {
            sb.Append($"  {{\"id\": {i}, \"name\": \"item_{i}\", \"desc\": \"description for item {i}\"}},\n");
        }
        sb.Append("  {\"id\": 199, \"name\": \"item_199\", \"desc\": \"trun"); // 199번째에서 절단

        string largeTruncated = sb.ToString();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? repaired = LlmJsonParser.ExtractJsonArray(largeTruncated);
        sw.Stop();

        Assert.NotNull(repaired);
        Assert.StartsWith("[", repaired);
        Assert.EndsWith("]", repaired);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Execution took {sw.ElapsedMilliseconds}ms, which exceeds 50ms budget!");

        using var doc = System.Text.Json.JsonDocument.Parse(repaired);
        Assert.Equal(199, doc.RootElement.GetArrayLength()); // 0~198 (199개) 완성 보존
    }

    private class SnakeCaseDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("world_view")]
        public string WorldView { get; set; } = "";
    }

    [Fact]
    public void ExtractJson_ClosedCodeFenceWithTruncatedJson_RepairsOuterRootInsteadOfInnerObject()
    {
        // 닫는 ```가 존재하지만 내부 JSON이 절단된 경우, 하위 {"inner": 1}을 오추출하지 않고 루트를 복구
        const string input1 = """
        ```json
        {"outer": {"inner": 1}, "trunc": 
        ```
        """;
        string? result1 = LlmJsonParser.ExtractJson(input1);
        Assert.NotNull(result1);
        Assert.Equal("{\"outer\": {\"inner\": 1}}", result1);

        // 문자열 내부 절단 후 닫는 ```와 사설의 따옴표/괄호가 이어지는 경우
        const string input2 = """
        ```json
        {"outer": {"inner": 1}, "trunc": "hello
        ```
        참고: "goodbye}"
        """;
        string? result2 = LlmJsonParser.ExtractJson(input2);
        Assert.NotNull(result2);
        Assert.Equal("{\"outer\": {\"inner\": 1}, \"trunc\": \"hello\"}", result2);
    }

    [Fact]
    public void ExtractJson_TripleBackticksInsideJsonStringValue_DoesNotPrematurelyCloseFence()
    {
        // JSON 문자열 값 내부에 마크다운 백틱(```json {} ```)이 포함된 경우 코드블록 경계로 오인하지 않음
        const string fencedInput = """
        ```json
        {
          "worldviewOutline": "Gothic",
          "promptOutline": "Use ```json {} ``` format when responding."
        }
        ```
        """;
        var plan = LlmJsonParser.DeserializeSafe<AgentPlan>(fencedInput);
        Assert.NotNull(plan);
        Assert.Equal("Gothic", plan.WorldviewOutline);
        Assert.Equal("Use ```json {} ``` format when responding.", plan.PromptOutline);

        const string rawInput = "{\"worldviewOutline\": \"Cyberpunk\", \"promptOutline\": \"Use ```json {} ``` format.\"}";
        var rawPlan = LlmJsonParser.DeserializeSafe<AgentPlan>(rawInput);
        Assert.NotNull(rawPlan);
        Assert.Equal("Cyberpunk", rawPlan.WorldviewOutline);
        Assert.Equal("Use ```json {} ``` format.", rawPlan.PromptOutline);
    }

    [Fact]
    public void ExtractJson_CommentImmediatelyAdjacentToPrimitiveToken_PreservesTokenValue()
    {
        // 리터럴/숫자 토큰 직후 공백 없이 주석(//, /* */)이 붙고 절단된 경우
        const string input1 = "{\"name\": \"Arthur\", \"flag\": true// comment\n";
        string? result1 = LlmJsonParser.ExtractJson(input1);
        Assert.Equal("{\"name\": \"Arthur\", \"flag\": true}", result1);

        const string input2 = "{\"name\": \"Arthur\", \"count\": 42/* inline comment */, \"trunc\": ";
        string? result2 = LlmJsonParser.ExtractJson(input2);
        Assert.Equal("{\"name\": \"Arthur\", \"count\": 42}", result2);
    }

    [Fact]
    public void DeserializeSafe_JsonPropertyNameAttributeAndIDictionary_MatchesSchemaCorrectly()
    {
        const string input = """
        Decoy: {"unrelated": 123}
        Actual: {"world_view": "Steampunk"}
        """;
        var dto = LlmJsonParser.DeserializeSafe<SnakeCaseDto>(input);
        Assert.NotNull(dto);
        Assert.Equal("Steampunk", dto.WorldView);

        var dict = LlmJsonParser.DeserializeSafe<IDictionary<string, int>>("{\"hp\": 100, \"mp\": 50}");
        Assert.NotNull(dict);
        Assert.Equal(100, dict["hp"]);
    }

    #endregion
}

