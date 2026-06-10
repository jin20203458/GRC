using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace GRC.Helpers;

public enum ChunkType { UserBubble, ModelDialogue, ModelNarration, System, ModelInnerThought }

public class MessageChunk
{
    public ChunkType Type { get; set; }
    public string Text { get; set; } = string.Empty;
}


public class MessageRoleplayConverter : IMultiValueConverter
{

    private static readonly Regex _splitPattern = new(@"(「.*?(?:」|$)|[""“”].*?(?:[""“”]|$))", RegexOptions.Singleline | RegexOptions.Compiled);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return null;

        string text = values[0] as string ?? string.Empty;
        string role = values[1] as string ?? "model";


        var chunks = new List<MessageChunk>();

        if (role == "user")
        {
            chunks.Add(new MessageChunk { Type = ChunkType.UserBubble, Text = text });
            return chunks;
        }

        if (role == "system")
        {
            chunks.Add(new MessageChunk { Type = ChunkType.System, Text = text });
            return chunks;
        }

        var parts = _splitPattern.Split(text);

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;

            if (part.StartsWith("「"))
            {
                chunks.Add(new MessageChunk { Type = ChunkType.ModelInnerThought, Text = part.Trim('\r', '\n') });
            }
            // 2. 가볍고 빠른 첫 글자 체크로 변경
            else if (part.Length > 0 && (part[0] == '"' || part[0] == '“' || part[0] == '”'))
            {
                chunks.Add(new MessageChunk { Type = ChunkType.ModelDialogue, Text = part.Trim('\r', '\n') });
            }
            else
            {
                chunks.Add(new MessageChunk { Type = ChunkType.ModelNarration, Text = part.Trim('\r', '\n') });
            }
        }

        return chunks;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}