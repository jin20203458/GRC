using GRC.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GRC.Services;

/// <summary>
/// 4턴(8메시지) 주기로 Gemini 3.5 Flash-Lite 모델을 호출하여 객관적 사건 팩트 불릿을 추출하고
/// 768차원 임베딩을 부착하는 중기 기억 생산자 인터페이스
/// </summary>
public interface IEventLedgerExtractor
{
    /// <summary>
    /// 최근 대화(8메시지 내외)를 분석하여 소설식 묘사를 배제한 1~2문장의 핵심 팩트 불릿 목록을 추출합니다.
    /// </summary>
    /// <param name="recentTurns">분석 대상 최근 대화 목록</param>
    /// <param name="currentChapter">현재 챕터 번호 (기본값 1)</param>
    /// <param name="cancellationToken">취소 토큰</param>
    /// <returns>임베딩이 부착된 불변 사건 렛저 항목 목록</returns>
    Task<List<EventLedgerItem>> ExtractLedgerFactsAsync(
        IEnumerable<ChatMessage> recentTurns,
        int currentChapter = 1,
        CancellationToken cancellationToken = default
    );
}
