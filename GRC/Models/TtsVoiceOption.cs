namespace GRC.Models;

/// <summary>
/// Google Cloud Gemini TTS 음성 캐릭터 옵션 모델
/// </summary>
public record TtsVoiceOption(string Id, string DisplayName, string Description)
{
    public override string ToString() => $"{DisplayName} - {Description}";
}
