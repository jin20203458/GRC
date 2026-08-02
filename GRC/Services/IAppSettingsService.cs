using GRC.Models;
using System.Threading.Tasks;

namespace GRC.Services;

/// <summary>
/// 애플리케이션 설정 로드 및 저장을 담당하는 서비스 인터페이스입니다.
/// </summary>
public interface IAppSettingsService
{
    /// <summary>
    /// 설정 파일(AppSettings.json)로부터 설정을 비동기적으로 로드합니다.
    /// 파일이 없거나 오류가 발생하면 기본값을 반환합니다.
    /// </summary>
    /// <returns>로드된 AppSettings 객체</returns>
    Task<AppSettings> LoadSettingsAsync();

    /// <summary>
    /// 현재 설정을 AppSettings.json 파일에 비동기적으로 저장합니다.
    /// </summary>
    /// <param name="settings">저장할 설정 객체</param>
    /// <returns>작업 완료를 나타내는 Task</returns>
    Task SaveSettingsAsync(AppSettings settings);

    /// <summary>
    /// Vertex AI 서비스 계정 인증 파일(google-credentials.json) 존재 여부를 확인합니다.
    /// </summary>
    bool IsCredentialFileExists();

    /// <summary>
    /// 외부 json 경로의 파일을 Config/google-credentials.json 위치로 복사합니다.
    /// </summary>
    Task<bool> CopyCredentialFileAsync(string sourceFilePath);
}