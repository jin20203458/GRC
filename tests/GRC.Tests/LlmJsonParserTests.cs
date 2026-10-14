using System;
using System.Collections.Generic;
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
}
