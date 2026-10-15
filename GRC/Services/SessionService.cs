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
            string sessionDir = Path.Combine(_sessionsDirectory, fileName);
            if (!Directory.Exists(sessionDir)) Directory.CreateDirectory(sessionDir);
            string filePath = Path.Combine(sessionDir, "Session.json");

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
            string filePath = Path.Combine(_sessionsDirectory, fileName, "Session.json");

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

        var dirs = Directory.GetDirectories(_sessionsDirectory)
            .Select(Path.GetFileName)
            .Where(x => x != null)
            .Cast<string>();

        return Task.FromResult(dirs);
    }

    public async Task DeleteSessionAsync(string fileName)
    {
        await _fileLock.WaitAsync();
        try
        {
            var sessionDir = Path.Combine(_sessionsDirectory, fileName);
            if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, true);
        }
        finally { _fileLock.Release(); }
    }
}