namespace GRC.Models;

public enum TtsLanguage
{
    Korean,
    Japanese
}

public record AppSettings(
    string ApiKey,
    string ProjectId = "",               // 구글 클라우드 프로젝트 ID
    string Location = "asia-northeast3", // 구글 클라우드 리전 (서울)
    bool UseVertexAI = true,             // 크레딧 사용 모드 ON/OFF 스위치
    ModelTier SelectedModel = ModelTier.Flash36,
    BlockThreshold SafetyThreshold = BlockThreshold.BLOCK_NONE,
    BackgroundTheme SelectedTheme = BackgroundTheme.Fantasy,
    int ChatDelay = 25,
    bool IsBgmEnabled = true,
    double BgmVolume = 0.5,
    bool IsTypingSoundEnabled = true,
    double TypingSoundVolume = 0.5,
    bool IsTtsEnabled = true,
    TtsLanguage SelectedTtsLanguage = TtsLanguage.Korean
);

public enum BackgroundTheme
{
    Fantasy,
    Modern,
    Cyberpunk
}