using GRC.Models;
using System.Collections.Generic;

namespace GRC.Services;

/// <summary>
/// 병렬 합집합(Sparse OR Dense 0.85) 하이브리드 로어북 검색기 인터페이스
/// </summary>
public interface IHybridLorebookRetriever
{
    /// <summary>
    /// 유저 질의와 임베딩 벡터를 기반으로 키워드 완전 일치(Sparse) 또는 고유사도(Dense >= 0.85) 로어북을 병렬 합집합으로 검색합니다.
    /// 토큰 예산(기본 2000 토큰) 한도 내에서 우선순위 순으로 동적 패킹합니다.
    /// </summary>
    List<LorebookEntry> Retrieve(
        string userQuery,
        float[]? queryEmbedding,
        IEnumerable<LorebookEntry> allLorebooks,
        int maxTokenBudget = 2000
    );
}
