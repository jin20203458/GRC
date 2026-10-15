using GRC.Models;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace GRC.Tests;

public class EmbeddingDtoTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [Fact]
    public void AIStudio_EmbedContentRequest_SerializesProperly()
    {
        var req = new EmbedContentRequest(new Content("user", [new Part("테스트 임베딩 문장")]));
        string json = JsonSerializer.Serialize(req, _jsonOptions);

        Assert.Contains("\"content\"", json);
        Assert.Contains("\"parts\"", json);
        Assert.Contains("테스트 임베딩 문장", json);
    }

    [Fact]
    public void AIStudio_EmbedContentResponse_DeserializesProperly()
    {
        string json = """
        {
          "embedding": {
            "values": [0.123, -0.456, 0.789]
          }
        }
        """;

        var res = JsonSerializer.Deserialize<EmbedContentResponse>(json, _jsonOptions);

        Assert.NotNull(res);
        Assert.NotNull(res.Embedding);
        Assert.Equal(3, res.Embedding.Values.Length);
        Assert.Equal(0.123f, res.Embedding.Values[0], precision: 3);
        Assert.Equal(-0.456f, res.Embedding.Values[1], precision: 3);
        Assert.Equal(0.789f, res.Embedding.Values[2], precision: 3);
    }

    [Fact]
    public void VertexAI_VertexEmbedRequest_SerializesProperly()
    {
        var req = new VertexEmbedRequest([new VertexEmbedInstance("Vertex 임베딩 문장")]);
        string json = JsonSerializer.Serialize(req, _jsonOptions);

        Assert.Contains("\"instances\"", json);
        Assert.Contains("\"content\"", json);
        Assert.Contains("Vertex 임베딩 문장", json);
    }

    [Fact]
    public void VertexAI_VertexEmbedResponse_DeserializesProperly()
    {
        string json = """
        {
          "predictions": [
            {
              "embeddings": {
                "values": [0.012, 0.034, 0.056]
              }
            }
          ]
        }
        """;

        var res = JsonSerializer.Deserialize<VertexEmbedResponse>(json, _jsonOptions);

        Assert.NotNull(res);
        Assert.NotNull(res.Predictions);
        Assert.Single(res.Predictions);
        Assert.Equal(3, res.Predictions[0].Embeddings.Values.Length);
        Assert.Equal(0.012f, res.Predictions[0].Embeddings.Values[0], precision: 3);
    }

    [Fact]
    public void CognitiveMemoryModels_RecordProperties_WorkProperly()
    {
        var ledgerItem = new EventLedgerItem(8, "유적에서 흑철검을 발견함", [0.1f, 0.2f]);
        Assert.Equal(8, ledgerItem.Turn);
        Assert.Equal("유적에서 흑철검을 발견함", ledgerItem.FactBullet);
        Assert.NotNull(ledgerItem.Embedding);
        Assert.Equal(2, ledgerItem.Embedding.Length);

        var entitySlot = new EntitySlotItem("엘리아스", "45%", "신뢰 형성 단계", "마력 고갈로 쇠약함");
        Assert.Equal("엘리아스", entitySlot.Name);
        Assert.Equal("45%", entitySlot.StatValue);
        Assert.Equal("신뢰 형성 단계", entitySlot.MentalStage);
        Assert.Equal("마력 고갈로 쇠약함", entitySlot.ConditionDesc);
    }
}
