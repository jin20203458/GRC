using GRC.Models;
using System.Collections.Generic;

namespace GRC.Services;

/// <summary>
/// 임베딩 시맨틱 검색 기반 과거 챕터 아카이브 렛저 회상 RAG 인터페이스
/// </summary>
public interface IGatedRAGRetriever
{
    /// <summary>
    /// 질의 임베딩 벡터와 과거 챕터 아카이브 렛저 간 코사인 유사도를 계산하여 0.71 이상인 항목을 핀포인트 인출하고 단일 원장 시간순(인덱스)으로 정렬하여 반환합니다.
    /// </summary>
    List<EventLedgerItem> RetrieveRecalledHistory(
        float[]? queryEmbedding,
        IEnumerable<EventLedgerItem>? archivedLedgers,
        int maxResults = 2
    );
}
