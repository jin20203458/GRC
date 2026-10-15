using GRC.Models;
using System.Collections.Generic;

namespace GRC.Services;

/// <summary>
/// 회상 살리언스 스코어링 기반 휴리스틱 게이팅 RAG 검색기 인터페이스
/// </summary>
public interface IGatedRAGRetriever
{
    /// <summary>
    /// 유저 입력의 시간적 회상 의도 점수를 계산합니다.
    /// (시간 부사 +3, 과거 엔티티 +2, 의문형/물음표 +1)
    /// </summary>
    int CalculateRecallScore(string userInput, IEnumerable<string>? pastEntities);

    /// <summary>
    /// 회상 점수가 임계치(3점 이상)를 만족하여 RAG를 작동시켜야 하는지 판정합니다.
    /// </summary>
    bool ShouldTriggerRAG(string userInput, IEnumerable<string>? pastEntities);

    /// <summary>
    /// 질의 임베딩 벡터와 과거 챕터 아카이브 렛저 간 코사인 유사도를 계산하여 0.82 이상인 항목을 핀포인트 인출합니다.
    /// </summary>
    List<EventLedgerItem> RetrieveRecalledHistory(
        float[]? queryEmbedding,
        IEnumerable<EventLedgerItem>? archivedLedgers,
        int maxResults = 2
    );
}
