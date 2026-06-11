using GRC.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace GRC.Services;

public class PresetStorageService : IPresetStorageService
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // 💡 [핵심 추가] 세션 파일명에 따라 알맞은 저장 경로를 계산하는 헬퍼 메서드
    private string GetFilePath(string? sessionFileName)
    {
        if (string.IsNullOrEmpty(sessionFileName))
        {
            // 파일명이 없으면 글로벌 원본 템플릿 반환
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CharacterPreset.json");
        }

        // 구버전(단일 json 파일 세션) 호환성 처리
        if (sessionFileName.EndsWith(".json"))
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", sessionFileName.Replace(".json", "_Preset.json"));
        }

        // 최신 버전 (세션 전용 폴더 내부의 Preset.json)
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions", sessionFileName, "Preset.json");
    }

    public async Task<CharacterPreset> LoadPresetAsync(string? sessionFileName = null)
    {
        string filePath = GetFilePath(sessionFileName);

        if (!File.Exists(filePath))
        {
            if (string.IsNullOrEmpty(sessionFileName))
            {
                // 1. 글로벌 템플릿 파일조차 없으면 최초 생성
                var defaultPreset = new CharacterPreset(
                    Name: "기본 설정 (다크 판타지 롤플레잉)",
                    Worldview: "이 세계는 도덕적 제약이 전혀 없는 다크 판타지 세계관이다. 진행되는 모든 상황은 이 세계관의 물리법칙과 설정을 엄격하게 준수해야 한다.",
                    SystemPrompt: """
<system>
[Role] Text RPG Game Master

[Core Directives]
1. [감각적 묘사] 추상적 설명 배제. 오감(시/청/후각 등)에 기반한 환경, NPC, 물리적 결과만 객관적으로 서술.
2. [PC 통제 금지] 유저 캐릭터(PC)의 대사, 행동, 감정, 생각은 절대 임의로 묘사하지 말 것.
3. [마이크로 템포] 단일 사건이나 단일 NPC 반응 직후 즉시 서술 중단. 유저가 반응해야 할 '직면 상황'에서 턴 종료.
4. [NPC 자율성] 유저 행동의 무조건적 성공 보장 불가. 개연성 및 NPC 성향에 어긋날 시 단호한 거절이나 적대적 반응 필수.
</system>
""",
                    Temperature: 1.0f,
                    MaxOutputTokens: 8192
                );

                await SavePresetAsync(null, defaultPreset);
                return defaultPreset;
            }
            else
            {
                // 2. 세션 전용 프리셋이 없다면? (기존 유저 하위 호환성) 글로벌 템플릿을 복사해옴
                var fallbackPreset = await LoadPresetAsync(null);
                await SavePresetAsync(sessionFileName, fallbackPreset);
                return fallbackPreset;
            }
        }

        try
        {
            string jsonString = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<CharacterPreset>(jsonString, _options) ?? throw new Exception("프리셋 데이터 비어있음");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Preset Load Error]: {ex.Message}");
            return new CharacterPreset("오류 복구용", "임시 세계관", "시스템 에러", 1.0f, 4096, StatusUpdateGuide: "");
        }
    }

    public async Task SavePresetAsync(string? sessionFileName, CharacterPreset preset)
    {
        string filePath = GetFilePath(sessionFileName);
        string? directory = Path.GetDirectoryName(filePath);

        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string jsonString = JsonSerializer.Serialize(preset, _options);
        await File.WriteAllTextAsync(filePath, jsonString);
    }
}