using GRC.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace GRC.Helpers;

public static class ChatDataHelper
{
    // 1. 커스텀 스탯 문자열 파싱 (ex: "체력:100, 굴복도:50" -> Dictionary)
    public static Dictionary<string, string> ParseCustomStats(string? inputStats)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(inputStats)) return result;

        var parts = inputStats.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var split = part.Split(new[] { ':' }, 2);
            if (split.Length == 2)
            {
                string key = split[0].Trim().Trim('[', ']');
                result[key] = split[1].Trim();
            }
        }
        return result;
    }

    // 2. 안전한 세션 폴더명(파일명) 생성 로직
    public static string GenerateSessionFileName(string baseName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        string safeFolderName = string.Join("", baseName.Split(invalidChars)).Trim().Replace(" ", "_");
        if (string.IsNullOrWhiteSpace(safeFolderName)) safeFolderName = "Session";

        string uniqueCode = Guid.NewGuid().ToString("N")[..6];
        return $"{safeFolderName}_{DateTime.Now:yyyyMMdd}_{uniqueCode}";
    }

    // 3. 분기된 세션의 FullHistory 파일 저장 로직
    public static async Task SaveBranchedHistoryAsync(string sessionFileName, List<ChatMessage> history)
    {
        string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", sessionFileName);
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);

        string logPath = Path.Combine(logDir, "FullHistory.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        await File.WriteAllTextAsync(logPath, JsonSerializer.Serialize(history, options));
    }
}
