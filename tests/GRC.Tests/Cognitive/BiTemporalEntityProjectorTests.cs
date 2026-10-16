using GRC.Helpers;
using GRC.Models;
using System.Collections.Generic;
using Xunit;

namespace GRC.Tests;

public class BiTemporalEntityProjectorTests
{
    [Fact]
    public void FormatEntityRelationship_WhenStatValueProvided_OutputsConditionWithStat()
    {
        // Arrange
        var entities = new List<EntitySlotItem>
        {
            new(
                Name: "엘리아스",
                ConditionDesc: "마력 고갈로 쇠약함",
                StatValue: "45%"
            )
        };

        // Act
        string xml = BiTemporalEntityProjector.FormatEntityRelationship(entities);

        // Assert
        Assert.Contains("<entity_relationship>", xml);
        Assert.Contains("- 인물(엘리아스): 마력 고갈로 쇠약함 (45%)", xml);
        Assert.Contains("</entity_relationship>", xml);
    }

    [Fact]
    public void FormatEntityRelationship_WhenStatValueEmpty_OutputsConditionOnlyWithoutEmptyParentheses()
    {
        // Arrange
        var entities = new List<EntitySlotItem>
        {
            new(
                Name: "루시엘",
                ConditionDesc: "가벼운 부상",
                StatValue: ""
            )
        };

        // Act
        string xml = BiTemporalEntityProjector.FormatEntityRelationship(entities);

        // Assert
        Assert.Contains("<entity_relationship>", xml);
        Assert.Contains("- 인물(루시엘): 가벼운 부상", xml);
        Assert.DoesNotContain("()", xml);
        Assert.Contains("</entity_relationship>", xml);
    }

    [Fact]
    public void FormatEntityRelationship_WhenNullOrEmpty_ReturnsEmptyString()
    {
        // Act & Assert
        Assert.Equal(string.Empty, BiTemporalEntityProjector.FormatEntityRelationship(null));
        Assert.Equal(string.Empty, BiTemporalEntityProjector.FormatEntityRelationship([]));
    }
}
