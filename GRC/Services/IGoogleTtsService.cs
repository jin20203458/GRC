using GRC.Models;
using System.Threading;
using System.Threading.Tasks;

namespace GRC.Services;

public interface IGoogleTtsService
{
    Task<string> GenerateSpeechAsync(
        string text,
        string characterName,
        string narration,
        string activeLorebook,
        string? voiceOverride = null,
        TtsLanguage? languageOverride = null,
        CancellationToken cancellationToken = default);

    Task<string> PreviewSpeechAsync(
        string text,
        string voiceName,
        TtsLanguage language,
        CancellationToken cancellationToken = default);
}