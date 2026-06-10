using GRC.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GRC.Services;

public class ChatStreamResult
{
    public string FinalText { get; set; } = string.Empty;
    public StatusPayload? StatusPayload { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
}

public interface IChatWorkflowService
{
    string? GetMetaDirective(string inputText, bool isFastForwardEnabled, 
        Dictionary<string, string>? currentStats, List<string>? triggeredEvents, 
        bool isChapterChanged = false);

    Task<ChatStreamResult> ProcessChatStreamAsync(
        ChatMessage finalUserMessage,
        CharacterPreset currentPreset,
        string? metaDirective,
        Action<char> onCharReceived,
        Func<string, string, Task<string>>? onDialoguePrefetch,
        Func<string, Task>? onAudioReady,
        Func<ChatStreamResult, Task>? onDownloadComplete,
        CancellationToken cancellationToken);
}