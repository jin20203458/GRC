using GRC.Models;
using System.Threading.Tasks;

namespace GRC.Services;

public interface IGeminiApiService
{
    /// <summary>
    /// 완성된 Request 객체를 구글 API로 전송하고 답변 텍스트를 받아옵니다. (단발성/요약용)
    /// </summary>
    /// <param name="request">최종 JSON 데이터 구조</param>
    /// <param name="overrideTier">설정된 모델 대신 특정 티어(예: FlashLite)를 강제할 때 사용</param>
    Task<string> SendMessageAsync(GeminiRequest request, ModelTier? overrideTier = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 스트리밍 방식으로 답변 조각들을 받아옵니다. (실시간 채팅용)
    /// </summary>
    IAsyncEnumerable<string> SendMessageStreamAsync(GeminiRequest request, ModelTier? overrideTier = null, CancellationToken cancellationToken = default);
}