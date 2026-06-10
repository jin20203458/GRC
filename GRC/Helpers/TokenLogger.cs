using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GRC.Helpers;

public static class TokenLogger
{
    // 현재 진행 중인 세션 파일명을 기억할 변수
    public static string CurrentSessionFileName { get; set; } = string.Empty;

    // 파일 동시 접근을 막기 위한 락
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static string GetLogPath(string sessionId)
    {
        if (sessionId.EndsWith(".json"))
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", sessionId.Replace(".json", "_TokenLog.csv"));

        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", sessionId);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return Path.Combine(dir, "TokenLog.csv");
    }

    public static async Task LogUsageAsync(string requestType, int promptTokens, int candidateTokens, int thoughtsTokens, int totalTokens)
    {
        if (string.IsNullOrEmpty(CurrentSessionFileName)) return;

        string logPath = GetLogPath(CurrentSessionFileName);
        await _lock.WaitAsync();
        try
        {
            bool writeHeader = !File.Exists(logPath);

            string logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{requestType},{promptTokens},{candidateTokens},{thoughtsTokens},{totalTokens}\n";

            if (writeHeader)
            {
                await File.AppendAllTextAsync(logPath, "Timestamp,RequestType,PromptTokens,CandidateTokens,ThoughtsTokens,TotalTokens\n");
            }

            await File.AppendAllTextAsync(logPath, logLine);
        }
        catch
        {
            // 통계용 로그이므로 실패해도 앱이 터지지 않도록 무시
        }
        finally
        {
            _lock.Release();
        }
    }
}