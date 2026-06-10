using System;
using System.IO;
using System.Threading.Tasks;

namespace GRC.Helpers;

public static class MemoryEventLogger
{
    public static async Task LogMemoryEventAsync(string logMessage)
    {
        try
        {
            // TokenLogger에서 관리 중인 현재 세션 파일명 활용
            string sessionId = TokenLogger.CurrentSessionFileName;
            if (string.IsNullOrEmpty(sessionId)) return;

            string logPath;
            // TokenLogger의 GetLogPath 로직 참고 (하위 호환성 및 새 폴더 분기 대응)
            if (sessionId.EndsWith(".json"))
            {
                logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", sessionId.Replace(".json", "_MemoryLog.txt"));
            }
            else
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", sessionId);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                logPath = Path.Combine(dir, "MemoryLog.txt");
            }

            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {logMessage}\n";
            await File.AppendAllTextAsync(logPath, logLine);
        }
        catch
        {
            // 파일 쓰기 실패 시 앱이 터지는 것을 방지하기 위해 무시
        }
    }
}