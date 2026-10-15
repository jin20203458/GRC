using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Globalization;

namespace GRC.Models;

public class StatusPayload
{
    [JsonPropertyName("uiBadges")]
    public Dictionary<string, string> CustomStats { get; set; } = new();

    [JsonPropertyName("characterConditionDesc")]
    public Dictionary<string, string> Chars { get; set; } = new();
    public List<string> Items { get; set; } = new();
    public List<string> Places { get; set; } = new();

    [JsonPropertyName("unresolvedClues")]
    public List<string>? UnresolvedClues { get; set; }
}

public class CharacterStatusUI
{
    public string Name { get; set; } = "Unknown";
    public string StatusText { get; set; } = "";
}

public class InventoryItemUI
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string BadgeColor { get; set; } = "";
}

public class CustomStatUI
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    public bool IsGauge { get; set; }
    public double CurrentValue { get; set; }
    public double MaxValue { get; set; }

    public static CustomStatUI Create(string? name, string? value)
    {
        var item = new CustomStatUI
        {
            Name = name ?? "",
            Value = value ?? ""
        };

        if (!string.IsNullOrWhiteSpace(value) && value.Contains('/'))
        {
            var parts = value.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double current) &&
                double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double max) &&
                max > 0 && double.IsFinite(current) && double.IsFinite(max))
            {
                item.IsGauge = true;
                item.CurrentValue = current;
                item.MaxValue = max;
            }
        }
        return item;
    }
}