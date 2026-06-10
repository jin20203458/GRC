using System.Text.Json.Serialization;
using System.Windows;

public class StatusPayload
{
    [JsonPropertyName("uiBadges")]
    public Dictionary<string, string> CustomStats { get; set; }

    [JsonPropertyName("characterConditionDesc")]
    public Dictionary<string, string> Chars { get; set; }
    public List<string> Items { get; set; }
    public List<string> Places { get; set; }
}

public class CharacterStatusUI
{
    public string Name { get; set; } = "Unknown";
    public string StatusText { get; set; }
}

public class InventoryItemUI
{
    public string Name { get; set; }
    public string Category { get; set; }
    public string BadgeColor { get; set; }
}

public class CustomStatUI
{
    public string Name { get; set; }
    public string Value { get; set; }

    public bool IsGauge { get; set; }
    public double CurrentValue { get; set; }
    public double MaxValue { get; set; }
}