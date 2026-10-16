using GRC.Models;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GRC.Helpers;

/// <summary>
/// 인물 상태 및 수치 프로젝터
/// </summary>
public static class BiTemporalEntityProjector
{
    /// <summary>
    /// 엔티티 슬롯 목록을 간결하고 명확한 XML 블록으로 포맷팅합니다.
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
            if (!string.IsNullOrWhiteSpace(entity.StatValue))
            {
                sb.AppendLine($"- 인물({entity.Name}): {entity.ConditionDesc} ({entity.StatValue})");
            }
            else
            {
                sb.AppendLine($"- 인물({entity.Name}): {entity.ConditionDesc}");
            }
        }

        sb.Append("</entity_relationship>");
        return sb.ToString();
    }
}
