namespace GRC.Models;

public class ChapterContext
{
    public string Plot { get; set; } = "새로운 챕터가 시작되었습니다.";
    public Dictionary<string, string> CustomStats { get; set; } = new();
    public Dictionary<string, string> Chars { get; set; } = new();
    public List<string> Items { get; set; } = new();
    public List<string> Places { get; set; } = new();
    public List<EventLedgerItem> EventLedgers { get; set; } = new();

    public string ToPromptString()
    {
        string customStatsStr = CustomStats.Count > 0 ? string.Join(", ", CustomStats.Select(x => $"{x.Key}: {x.Value}")) : "특이사항 없음";
        string charsStr = Chars.Count > 0 ? string.Join("\n- ", Chars.Select(x => $"{x.Key}: {x.Value}")) : "특이사항 없음";
        string itemsStr = Items.Count > 0 ? string.Join(", ", Items.Select(i => $"[{i}]")) : "없음";
        string placesStr = Places.Count > 0 ? string.Join(", ", Places.Select(p => $"[{p}]")) : "특이사항 없음";

        return $$"""
<background_summary>
{{Plot}}
</background_summary>

<current_snapshot>
- 스탯: {{customStatsStr}}
- 인물 상태: {{charsStr}}
- 소지품: {{itemsStr}}
- 위치: {{placesStr}}
</current_snapshot>
""";
    }

    /// <summary>
    /// Plot을 제외한 상태 데이터만 포맷합니다. 상태창 갱신 전용 API 호출에 사용됩니다.
    /// </summary>
    public string ToStatusSnapshotString()
    {
        string customStatsStr = CustomStats.Count > 0 ? string.Join(", ", CustomStats.Select(x => $"{x.Key}: {x.Value}")) : "없음";
        string charsStr = Chars.Count > 0 ? string.Join(", ", Chars.Select(x => $"{x.Key}: {x.Value}")) : "없음";
        string itemsStr = Items.Count > 0 ? string.Join(", ", Items) : "없음";
        string placesStr = Places.Count > 0 ? string.Join(", ", Places) : "없음";

        return $"""
- uiBadges: {customStatsStr}
- characterConditionDesc: {charsStr}
- items: {itemsStr}
- places: {placesStr}
""";
    }
}