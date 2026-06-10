using GRC.Models;
using System.Threading.Tasks;

namespace GRC.Services;

public interface ISessionService
{
    // 세션 저장 (파일명 등을 인자로 받아 멀티 세션 지원 가능)
    Task SaveSessionAsync(string fileName, ChatSession session);

    // 마지막 세션 불러오기
    Task<ChatSession?> LoadSessionAsync(string fileName);

    // 세션 목록 가져오기
    Task<IEnumerable<string>> GetSessionFilesAsync(); // 세션 목록 가져오기

    // 세션 삭제
    Task DeleteSessionAsync(string fileName);     
}