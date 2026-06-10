using System.Threading.Tasks;

namespace GRC.Services;

public interface IGoogleTtsService
{
    Task<string> GenerateSpeechAsync(string text, string characterName, string narration, string activeLorebook);
}