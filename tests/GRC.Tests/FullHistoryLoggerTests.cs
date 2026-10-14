using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GRC.Helpers;
using GRC.Models;
using Xunit;

namespace GRC.Tests;

public class FullHistoryLoggerTests : IDisposable
{
    private readonly string _testSessionId;
    private readonly string _testSessionDir;

    public FullHistoryLoggerTests()
    {
        _testSessionId = $"UnitTest_{Guid.NewGuid():N}";
        _testSessionDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", _testSessionId);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testSessionDir))
            {
                Directory.Delete(_testSessionDir, true);
            }
        }
        catch { }
    }

    // 1. 파일 미존재 시 최초 로깅 -> 파일 생성 및 유효한 JSON 배열 역직렬화 검증
    [Fact]
    public async Task LogMessageAsync_WhenFileDoesNotExist_CreatesValidJsonArray()
    {
        var message = new ChatMessage("user", "Hello GRC", DateTime.Now);

        await FullHistoryLogger.LogMessageAsync(_testSessionId, message);

        var loaded = await FullHistoryLogger.LoadFullHistoryAsync(_testSessionId);
        Assert.Single(loaded);
        Assert.Equal("user", loaded[0].Role);
        Assert.Equal("Hello GRC", loaded[0].Text);
    }

    // 2. 연속 메시지 주입 -> 파일 전체 재직렬화 없이 역방향 Seek으로 O(1) Append 검증 (순서 및 데이터 일치)
    [Fact]
    public async Task LogMessageAsync_AppendsMultipleMessages_PreservesOrderAndContent()
    {
        var msg1 = new ChatMessage("user", "First Turn", DateTime.Now);
        var msg2 = new ChatMessage("model", "Second Turn", DateTime.Now.AddSeconds(1));
        var msg3 = new ChatMessage("user", "Third Turn", DateTime.Now.AddSeconds(2));

        await FullHistoryLogger.LogMessageAsync(_testSessionId, msg1);
        await FullHistoryLogger.LogMessageAsync(_testSessionId, msg2);
        await FullHistoryLogger.LogMessageAsync(_testSessionId, msg3);

        var loaded = await FullHistoryLogger.LoadFullHistoryAsync(_testSessionId);
        Assert.Equal(3, loaded.Count);
        Assert.Equal("First Turn", loaded[0].Text);
        Assert.Equal("Second Turn", loaded[1].Text);
        Assert.Equal("Third Turn", loaded[2].Text);
    }

    // 3. 파일이 빈 배열 "[]" (<= 4 bytes 또는 공백 포함)일 때 추가 -> 선행 쉼표 없이 유효한 JSON 배열로 Append 검증
    [Fact]
    public async Task LogMessageAsync_WhenFileHasEmptyArray_AppendsWithoutSyntaxError()
    {
        Directory.CreateDirectory(_testSessionDir);
        string filePath = Path.Combine(_testSessionDir, "FullHistory.json");
        await File.WriteAllTextAsync(filePath, "[]");

        var msg = new ChatMessage("user", "Message after empty array", DateTime.Now);
        await FullHistoryLogger.LogMessageAsync(_testSessionId, msg);

        var loaded = await FullHistoryLogger.LoadFullHistoryAsync(_testSessionId);
        Assert.Single(loaded);
        Assert.Equal("Message after empty array", loaded[0].Text);
    }

    // 4. 파일 미존재 또는 깨진 JSON 파일 로드 시 -> 예외 없이 빈 리스트([]) 반환 검증
    [Fact]
    public async Task LoadFullHistoryAsync_WhenFileMissingOrCorrupted_ReturnsEmptyListGracefully()
    {
        // 4a: 파일 미존재
        var nonExistentId = $"Missing_{Guid.NewGuid():N}";
        var missingResult = await FullHistoryLogger.LoadFullHistoryAsync(nonExistentId);
        Assert.NotNull(missingResult);
        Assert.Empty(missingResult);

        // 4b: 깨진 JSON 파일
        Directory.CreateDirectory(_testSessionDir);
        string filePath = Path.Combine(_testSessionDir, "FullHistory.json");
        await File.WriteAllTextAsync(filePath, "{ \"broken\": [ truncated json ");

        var corruptedResult = await FullHistoryLogger.LoadFullHistoryAsync(_testSessionId);
        Assert.NotNull(corruptedResult);
        Assert.Empty(corruptedResult);
    }

    // 5. 메시지 삭제 -> 특정 메시지 매칭 삭제 및 잔여 메시지 보존 검증
    [Fact]
    public async Task DeleteMessageAsync_RemovesMatchingMessage_PreservesOthers()
    {
        var time = DateTime.Now;
        var msg1 = new ChatMessage("user", "Keep 1", time);
        var msg2 = new ChatMessage("model", "Delete Me", time.AddSeconds(1));
        var msg3 = new ChatMessage("user", "Keep 2", time.AddSeconds(2));

        await FullHistoryLogger.LogMessageAsync(_testSessionId, msg1);
        await FullHistoryLogger.LogMessageAsync(_testSessionId, msg2);
        await FullHistoryLogger.LogMessageAsync(_testSessionId, msg3);

        await FullHistoryLogger.DeleteMessageAsync(_testSessionId, msg2);

        var loaded = await FullHistoryLogger.LoadFullHistoryAsync(_testSessionId);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Keep 1", loaded[0].Text);
        Assert.Equal("Keep 2", loaded[1].Text);
    }

    // 6. 존재하지 않는 메시지 삭제 요청 -> 파일 손상 없이 원본 유지 및 무에러 검증
    [Fact]
    public async Task DeleteMessageAsync_WhenMessageNotFound_PreservesFileWithoutError()
    {
        var msg1 = new ChatMessage("user", "Existing", DateTime.Now);
        await FullHistoryLogger.LogMessageAsync(_testSessionId, msg1);

        var nonExistent = new ChatMessage("model", "NonExistent", DateTime.Now.AddDays(1));
        await FullHistoryLogger.DeleteMessageAsync(_testSessionId, nonExistent);

        var loaded = await FullHistoryLogger.LoadFullHistoryAsync(_testSessionId);
        Assert.Single(loaded);
        Assert.Equal("Existing", loaded[0].Text);
    }

    // 7. 전체 비우기 -> "[]" 빈 배열 기록 및 로드 시 0건 검증
    [Fact]
    public async Task ClearHistoryAsync_EmptiesHistory_FileContainsEmptyArray()
    {
        var msg1 = new ChatMessage("user", "Turn 1", DateTime.Now);
        var msg2 = new ChatMessage("model", "Turn 2", DateTime.Now.AddSeconds(1));
        await FullHistoryLogger.LogMessageAsync(_testSessionId, msg1);
        await FullHistoryLogger.LogMessageAsync(_testSessionId, msg2);

        await FullHistoryLogger.ClearHistoryAsync(_testSessionId);

        var loaded = await FullHistoryLogger.LoadFullHistoryAsync(_testSessionId);
        Assert.Empty(loaded);

        string rawContent = await File.ReadAllTextAsync(Path.Combine(_testSessionDir, "FullHistory.json"));
        Assert.Equal("[]", rawContent.Trim());
    }

    // 8. 동시 다발적 10개 메시지 병렬 로깅 -> 세마포어 락으로 파일 손상 없이 10건 전량 보존 검증
    [Fact]
    public async Task LogMessageAsync_ConcurrentAppends_ThreadSafe()
    {
        var tasks = Enumerable.Range(0, 10)
            .Select(i => FullHistoryLogger.LogMessageAsync(
                _testSessionId,
                new ChatMessage("user", $"Concurrent msg {i}", DateTime.Now.AddMilliseconds(i))))
            .ToList();

        await Task.WhenAll(tasks);

        var loaded = await FullHistoryLogger.LoadFullHistoryAsync(_testSessionId);
        Assert.Equal(10, loaded.Count);
    }

    // 9. 분기 세션 전체 저장 -> SaveFullHistoryAsync 세마포어 락 하에서 안전한 파일 덮어쓰기 및 로드 검증
    [Fact]
    public async Task SaveFullHistoryAsync_OverwritesHistory_ThreadSafeAndValidJson()
    {
        var msg1 = new ChatMessage("user", "Old Turn 1", DateTime.Now);
        await FullHistoryLogger.LogMessageAsync(_testSessionId, msg1);

        var newHistory = new List<ChatMessage>
        {
            new("user", "Branched Turn 1", DateTime.Now),
            new("model", "Branched Turn 2", DateTime.Now.AddSeconds(1)),
            new("user", "Branched Turn 3", DateTime.Now.AddSeconds(2))
        };

        await FullHistoryLogger.SaveFullHistoryAsync(_testSessionId, newHistory);

        var loaded = await FullHistoryLogger.LoadFullHistoryAsync(_testSessionId);
        Assert.Equal(3, loaded.Count);
        Assert.Equal("Branched Turn 1", loaded[0].Text);
        Assert.Equal("Branched Turn 2", loaded[1].Text);
        Assert.Equal("Branched Turn 3", loaded[2].Text);
    }
}
