using GRC.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace GRC.Services;

public class AppSettingsService : IAppSettingsService
{
    private readonly string _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppSettings.json");
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public async Task<AppSettings> LoadSettingsAsync()
    {
        var defaultSettings = new AppSettings(
      ApiKey: "",
      ProjectId: "",
      Location: "asia-northeast3",
      UseVertexAI: true,
      SelectedModel: ModelTier.Flash36,
      SafetyThreshold: BlockThreshold.BLOCK_NONE
  );

        // 2. 파일이 없을 때 기본값 저장 후 반환
        if (!File.Exists(_filePath))
        {
            await SaveSettingsAsync(defaultSettings);
            return defaultSettings;
        }

        try
        {
            string jsonString = await File.ReadAllTextAsync(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(jsonString, _options);
            return settings ?? defaultSettings;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings Load Error]: {ex.Message}");
            return defaultSettings;

        }
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        string jsonString = JsonSerializer.Serialize(settings, _options);
        await File.WriteAllTextAsync(_filePath, jsonString);
    }
}