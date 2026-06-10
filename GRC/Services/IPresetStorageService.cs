using GRC.Models;
using System.Threading.Tasks;

namespace GRC.Services;

public interface IPresetStorageService
{
    /// <summary>
    /// 로컬 JSON 파일에서 캐릭터 프리셋(시스템 프롬프트 등)을 비동기로 불러옵니다.
    /// </summary>
    Task<CharacterPreset> LoadPresetAsync(string? sessionFileName = null);


    /// <summary>
    /// 현재의 캐릭터 프리셋을 로컬 JSON 파일에 비동기로 저장합니다.
    /// </summary>
    Task SavePresetAsync(string? sessionFileName, CharacterPreset preset);
}