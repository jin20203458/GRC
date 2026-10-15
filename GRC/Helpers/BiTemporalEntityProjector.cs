using GRC.Models;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GRC.Helpers;

/// <summary>
/// 이중 시간(Bi-temporal) 인물 관계 및 감정선 대비 프로젝터
/// </summary>
public static class BiTemporalEntityProjector
{
    /// <summary>
    /// 엔티티 슬롯 목록을 과거 시작점과 현재 상태의 감정적 낙차가 반영된 XML 블록으로 포맷팅합니다.
    /// </summary>
    public static string FormatEntityRelationship(IEnumerable<EntitySlotItem>? entities)
    {
        if (entities == null) return string.Empty;

        var entityList = entities.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
        if (entityList.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<entity_relationship>");

        foreach (var entity in entityList)
        {
            if (!string.IsNullOrWhiteSpace(entity.InitialAnchor))
            {
                sb.AppendLine($"- 인물({entity.Name}):");
                sb.AppendLine($"  * [원초적 과거]: {entity.InitialAnchor}");
                sb.AppendLine($"  * [현재 상태]: {entity.MentalStage} ({entity.StatValue}), {entity.ConditionDesc}");
                sb.AppendLine("  * [서사 지침]: 친절하게 대하면서도 과거에 대한 죄책감과 내면의 감정적 낙차를 섬세하게 드러낼 것.");
            }
            else
            {
                sb.AppendLine($"- 인물({entity.Name}): {entity.MentalStage} ({entity.StatValue}), {entity.ConditionDesc}");
            }
        }

        sb.Append("</entity_relationship>");
        return sb.ToString();
    }
}
