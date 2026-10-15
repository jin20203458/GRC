using GRC.Models;
using System.Collections.Generic;

namespace GRC.Services;

/// <summary>
/// 병렬 합집합(Sparse OR Dense 0.85) 하이브리드 로어북 검색기 인터페이스
/// 3,000토큰 예산 동적 패킹, 3턴 잔존(Sticky 2) 및 상태창 인물 로어북 잔여 예산 보충 탑재
/// </summary>
public interface IHybridLorebookRetriever
{
    /// <summary>
    /// 유저 질의와 임베딩 벡터를 기반으로 키워드 완전 일치(Sparse) 또는 고유사도(Dense >= 0.85) 로어북을 병렬 합집합으로 검색합니다.
    /// 1순위: 직접 매칭 (3턴 잔존 부여), 2순위: 이전 잔존 로어북, 3순위: 상태창 인물(activeCharNames) 로어북 순으로 3,000토큰 예산 한도 내에서 동적 패킹합니다.
    /// </summary>
    List<LorebookEntry> Retrieve(
        string userQuery,
        float[]? queryEmbedding,
        IEnumerable<LorebookEntry> allLorebooks,
        IEnumerable<string>? activeCharNames = null,
        int maxTokenBudget = 3000
    );

    /// <summary>
    /// 세션 초기화 시 로어북 잔존(Sticky) 상태를 초기화합니다.
    /// </summary>
    void ClearSticky();
}
