using System;
using System.IO;
using System.Threading.Tasks;

namespace GRC.Helpers;

public static class MemoryEventLogger
{
    public static async Task LogMemoryEventAsync(string logMessage, string? sessionId = null)
    {
        try
        {
            // 명시적으로 세션 ID가 제공되면 우선 사용, 없으면 TokenLogger 전역 값으로 Fallback
            string targetSessionId = !string.IsNullOrEmpty(sessionId) ? sessionId : TokenLogger.CurrentSessionFileName;
            if (string.IsNullOrEmpty(targetSessionId)) return;

            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", targetSessionId);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "MemoryLog.txt");

            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {logMessage}\n";
            await File.AppendAllTextAsync(logPath, logLine);
        }
        catch
        {
            // 파일 쓰기 실패 시 앱이 터지는 것을 방지하기 위해 무시
        }
    }
}