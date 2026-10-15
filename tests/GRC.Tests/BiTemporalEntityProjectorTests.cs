using GRC.Helpers;
using GRC.Models;
using System.Collections.Generic;
using Xunit;

namespace GRC.Tests;

public class BiTemporalEntityProjectorTests
{
    [Fact]
    public void FormatEntityRelationship_WhenInitialAnchorProvided_OutputsContrastStructure()
    {
        // Arrange
        var entities = new List<EntitySlotItem>
        {
            new(
                Name: "레이나",
                StatValue: "70%",
                MentalStage: "깊은 신뢰",
                ConditionDesc: "안정 상태",
                InitialAnchor: "가문을 멸문시킨 원수 용병에 대한 뼛속 깊은 증오"
            )
        };

        // Act
        string xml = BiTemporalEntityProjector.FormatEntityRelationship(entities);

        // Assert
        Assert.Contains("<entity_relationship>", xml);
        Assert.Contains("- 인물(레이나):", xml);
        Assert.Contains("* [원초적 과거]: 가문을 멸문시킨 원수 용병에 대한 뼛속 깊은 증오", xml);
        Assert.Contains("* [현재 상태]: 깊은 신뢰 (70%), 안정 상태", xml);
        Assert.Contains("* [서사 지침]: 친절하게 대하면서도 과거에 대한 죄책감과 내면의 감정적 낙차를 섬세하게 드러낼 것.", xml);
        Assert.Contains("</entity_relationship>", xml);
    }

    [Fact]
    public void FormatEntityRelationship_WhenInitialAnchorEmpty_OutputsSingleStateFallback()
    {
        // Arrange
        var entities = new List<EntitySlotItem>
        {
            new(
                Name: "엘리아스",
                StatValue: "45%",
                MentalStage: "경계 해제",
                ConditionDesc: "마력 고갈"
            )
        };

        // Act
        string xml = BiTemporalEntityProjector.FormatEntityRelationship(entities);

        // Assert
        Assert.Contains("<entity_relationship>", xml);
        Assert.Contains("- 인물(엘리아스): 경계 해제 (45%), 마력 고갈", xml);
        Assert.DoesNotContain("[원초적 과거]", xml);
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
