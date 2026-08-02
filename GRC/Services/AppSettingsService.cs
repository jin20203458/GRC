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
        EnsureDirectoriesExist();

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
        EnsureDirectoriesExist();
        string jsonString = JsonSerializer.Serialize(settings, _options);
        await File.WriteAllTextAsync(_filePath, jsonString);
    }

    public bool IsCredentialFileExists()
    {
        string credentialPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "google-credentials.json");
        return File.Exists(credentialPath);
    }

    public async Task<bool> CopyCredentialFileAsync(string sourceFilePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return false;

            EnsureDirectoriesExist();
            string targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "google-credentials.json");
            
            // 기존 파일이 열려있거나 읽기 전용인 경우에 대비하여 복사
            byte[] bytes = await File.ReadAllBytesAsync(sourceFilePath);
            await File.WriteAllBytesAsync(targetPath, bytes);

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Credential Copy Error]: {ex.Message}");
            return false;
        }
    }

    private void EnsureDirectoriesExist()
    {
        string configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
        string sessionsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sessions");

        if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
        if (!Directory.Exists(sessionsDir)) Directory.CreateDirectory(sessionsDir);
    }
}