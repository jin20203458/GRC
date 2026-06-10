public class ChapterContext
{
    public string Plot { get; set; } = "새로운 챕터가 시작되었습니다.";
    public Dictionary<string, string> CustomStats { get; set; } = new();
    public Dictionary<string, string> Chars { get; set; } = new();
    public List<string> Items { get; set; } = new();
    public List<string> Places { get; set; } = new();
    public List<string> TriggeredMetaEvents { get; set; } = new();

    public string ToPromptString()
    {
        string customStatsStr = CustomStats.Count > 0 ? string.Join(", ", CustomStats.Select(x => $"[{x.Key}]: {x.Value}")) : "특이사항 없음";
        string charsStr = Chars.Count > 0 ? string.Join("\n  * ", Chars.Select(x => $"{x.Key}: {x.Value}")) : "특이사항 없음";
        string itemsStr = Items.Count > 0 ? string.Join(", ", Items.Select(i => $"[{i}]")) : "없음";
        string placesStr = Places.Count > 0 ? string.Join(", ", Places.Select(p => $"[{p}]")) : "특이사항 없음";

        return $"""
    <background_summary>
    [누적된 줄거리]
    {Plot}
    </background_summary>

    <current_snapshot>
    [시스템 고정 추적 스탯 (uiBadges)]
    * {customStatsStr}
    [NPC 및 파티원 서사적 상태]
    * {charsStr}
    [현재 인벤토리]
    * {itemsStr}
    [현재 위치]
    * {placesStr}
    </current_snapshot>
    """;
    }
}