using GRC.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GRC.Helpers;

public static class FullHistoryLogger
{
    // 파일 동시 접근을 제어하기 위한 비동기 락
    private static readonly SemaphoreSlim _fileLock = new(1, 1);
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static string GetLogPath(string sessionId)
    {
        if (sessionId.EndsWith(".json"))
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", sessionId.Replace(".json", "_FullHistory.json"));

        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", sessionId);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return Path.Combine(dir, "FullHistory.json");
    }

    // 대화 비동기 기록 (백그라운드에서 Fire-and-Forget으로 작동)
    public static async Task LogMessageAsync(string sessionFileName, ChatMessage message)
    {
        if (string.IsNullOrEmpty(sessionFileName)) return;

        string logPath = GetLogPath(sessionFileName);
        await _fileLock.WaitAsync(); // 문이 열릴 때까지 대기

        //  여기서부터 수정됨 (기존 try-catch 블록 통째로 교체) 
        int retryCount = 0;
        int maxRetries = 3;

        while (retryCount < maxRetries)
        {
            try
            {
                // 1. 파일이 아예 없거나 빈 배열(`[]`) 수준으로 작으면 기존처럼 새 배열로 생성
                if (!File.Exists(logPath) || new FileInfo(logPath).Length <= 4)
                {
                    var initialHistory = new List<ChatMessage> { message };
                    await File.WriteAllTextAsync(logPath, JsonSerializer.Serialize(initialHistory, _options));
                    break; // 성공 시 루프 탈출
                }

                // 2. 새 메시지를 JSON 텍스트로 변환 (들여쓰기 적용)
                string newMessageJson = JsonSerializer.Serialize(message, _options);

                // 3. 파일 스트림을 열어서 맨 끝부분 조작 (Append 방식)
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                // 맨 뒤에서부터 역방향으로 닫는 대괄호 ']' 기호를 찾음
                long pos = fs.Length - 1;
                while (pos > 0)
                {
                    fs.Seek(pos, SeekOrigin.Begin);
                    if (fs.ReadByte() == ']') break;
                    pos--;
                }

                if (pos > 0)
                {
                    // ']' 기호 바로 앞의 문자가 '[' 인지 역방향으로 탐색 (빈 배열 검사 방어 로직)
                    bool isEmptyArray = false;
                    long checkPos = pos - 1;
                    while (checkPos >= 0)
                    {
                        fs.Seek(checkPos, SeekOrigin.Begin);
                        int b = fs.ReadByte();
                        if (b == '[') { isEmptyArray = true; break; }
                        if (!char.IsWhiteSpace((char)b)) break; // 공백이 아닌 다른 문자가 나오면 빈 배열이 아님
                        checkPos--;
                    }

                    fs.Seek(pos, SeekOrigin.Begin);

                    // 빈 배열(`[]`) 상태라면 문법이 깨지지 않도록 쉼표(,)를 생략하고 삽입
                    string appendText = isEmptyArray
                        ? "\n" + newMessageJson + "\n]"
                        : ",\n" + newMessageJson + "\n]";

                    byte[] appendBytes = System.Text.Encoding.UTF8.GetBytes(appendText);
                    await fs.WriteAsync(appendBytes, 0, appendBytes.Length);
                }
                else
                {
                    // Fallback: 파일이 깨져서 ']'를 못 찾았다면 안전하게 전체 덮어쓰기
                    fs.Close();
                    string json = await File.ReadAllTextAsync(logPath);
                    var history = JsonSerializer.Deserialize<List<ChatMessage>>(json, _options) ?? new List<ChatMessage>();
                    history.Add(message);
                    await File.WriteAllTextAsync(logPath, JsonSerializer.Serialize(history, _options));
                }
                break; // 성공 시 루프 탈출
            }
            catch (IOException) // 파일 잠금 충돌 시 (백신, OneDrive 등)
            {
                retryCount++;
                if (retryCount >= maxRetries) break;
                await Task.Delay(100); // 0.1초 대기 후 재시도
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FullHistoryLogger Error]: {ex.Message}");
                break;
            }
        }

        _fileLock.Release();
    }

    // UI 화면에 전체 대화를 뿌려주기 위한 별도 로드
    public static async Task<List<ChatMessage>> LoadFullHistoryAsync(string sessionFileName)
    {
        if (string.IsNullOrEmpty(sessionFileName)) return [];

        string logPath = GetLogPath(sessionFileName);
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(logPath)) return [];
            string json = await File.ReadAllTextAsync(logPath);
            return JsonSerializer.Deserialize<List<ChatMessage>>(json, _options) ?? [];
        }
        catch
        {
            return [];
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // 특정 메시지를 찾아 파일에서 제거하는 메서드
    public static async Task DeleteMessageAsync(string sessionFileName, ChatMessage messageToRemove)
    {
        if (string.IsNullOrEmpty(sessionFileName)) return;
        string logPath = GetLogPath(sessionFileName);
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(logPath)) return;
            string json = await File.ReadAllTextAsync(logPath);
            var history = JsonSerializer.Deserialize<List<ChatMessage>>(json, _options) ?? [];

            // 역할, 내용, 시간이 모두 일치하는 객체를 찾아 제거 (불변 객체 특성 대응)
            var target = history.Find(m => m.Role == messageToRemove.Role && m.Text == messageToRemove.Text && m.Timestamp == messageToRemove.Timestamp);
            if (target != null)
            {
                history.Remove(target);
                // 지워진 상태로 덮어쓰기
                await File.WriteAllTextAsync(logPath, JsonSerializer.Serialize(history, _options));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FullHistoryLogger Delete Error]: {ex.Message}");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // 파일 내용을 완전히 백지화(빈 배열)하는 메서드
    public static async Task ClearHistoryAsync(string sessionFileName)
    {
        if (string.IsNullOrEmpty(sessionFileName)) return;
        string logPath = GetLogPath(sessionFileName);
        await _fileLock.WaitAsync();
        try
        {
            if (File.Exists(logPath))
            {
                // 빈 배열을 의미하는 JSON 문자열로 덮어쓰기
                await File.WriteAllTextAsync(logPath, "[]");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FullHistoryLogger Clear Error]: {ex.Message}");
        }
        finally
        {
            _fileLock.Release();
        }
    }
}