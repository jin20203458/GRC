using GRC.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GRC.Services;

public class SessionService : ISessionService
{
    private readonly string _sessionsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions");
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public SessionService()
    {
        if (!Directory.Exists(_sessionsDirectory))
        {
            Directory.CreateDirectory(_sessionsDirectory);
        }
    }

    public async Task SaveSessionAsync(string fileName, ChatSession session)
    {
        await _fileLock.WaitAsync();
        try
        {
            string filePath;
            // 기존 파일 하위 호환성 및 새 폴더 로직 분기
            if (fileName.EndsWith(".json"))
            {
                filePath = Path.Combine(_sessionsDirectory, fileName);
            }
            else
            {
                string sessionDir = Path.Combine(_sessionsDirectory, fileName);
                if (!Directory.Exists(sessionDir)) Directory.CreateDirectory(sessionDir);
                filePath = Path.Combine(sessionDir, "Session.json");
            }

            var json = JsonSerializer.Serialize(session, _options);
            string tempPath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }
        }
        finally { _fileLock.Release(); }
    }

    public async Task<ChatSession?> LoadSessionAsync(string fileName)
    {
        await _fileLock.WaitAsync();
        try
        {
            string filePath = fileName.EndsWith(".json")
                ? Path.Combine(_sessionsDirectory, fileName)
                : Path.Combine(_sessionsDirectory, fileName, "Session.json");

            if (!File.Exists(filePath)) return null;
            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<ChatSession>(json, _options);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Session Load Error]: {ex.Message}");
            return null;
        }
        finally { _fileLock.Release(); }
    }

    public Task<IEnumerable<string>> GetSessionFilesAsync()
    {
        if (!Directory.Exists(_sessionsDirectory))
            return Task.FromResult(Enumerable.Empty<string>());

        // 폴더 이름들과 기존 .json 파일 이름들을 모두 가져옵니다.
        var dirs = Directory.GetDirectories(_sessionsDirectory).Select(Path.GetFileName);
        var files = Directory.GetFiles(_sessionsDirectory, "*.json").Select(Path.GetFileName);

        return Task.FromResult(dirs.Concat(files).Where(x => x != null).Cast<string>());
    }

    public async Task DeleteSessionAsync(string fileName)
    {
        await _fileLock.WaitAsync();
        try
        {
            if (fileName.EndsWith(".json"))
            {
                // 기존 레거시 삭제 (3개 파일 각각 삭제)
                var filePath = Path.Combine(_sessionsDirectory, fileName);
                if (File.Exists(filePath)) File.Delete(filePath);
                var fullHistoryPath = Path.Combine(_sessionsDirectory, fileName.Replace(".json", "_FullHistory.json"));
                if (File.Exists(fullHistoryPath)) File.Delete(fullHistoryPath);
                var tokenLogPath = Path.Combine(_sessionsDirectory, fileName.Replace(".json", "_TokenLog.csv"));
                if (File.Exists(tokenLogPath)) File.Delete(tokenLogPath);

            }
            else
            {
                // 폴더 통째로 삭제
                var sessionDir = Path.Combine(_sessionsDirectory, fileName);
                if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, true);
            }
        }
        finally { _fileLock.Release(); }
    }
}