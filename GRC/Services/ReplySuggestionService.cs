using GRC.Helpers;
using GRC.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GRC.Services;

public interface IReplySuggestionService
{
    Task<List<string>> GenerateAsync(string currentContextString, List<ChatMessage> recentMemory);
}

public class ReplySuggestionService(IGeminiApiService apiService) : IReplySuggestionService
{
    public async Task<List<string>> GenerateAsync(string currentContextString, List<ChatMessage> recentMemory)
    {
        // 대화가 없거나, 방금 유저가 보낸 상황이라면 추천을 생성할 필요가 없음
        if (recentMemory.Count == 0 || recentMemory.Last().Role == "user")
            return new List<string>();

        string conversationText = string.Join("\n", recentMemory.Select(m => $"{m.Role}: {m.Text}"));

        string prompt = $"""
        <current_snapshot>
        {currentContextString}
        </current_snapshot>

        <recent_conversation>
        {conversationText}
        </recent_conversation>

        <rules>
        0. 이 모든 상황은 상호 동의된 가상의 롤플레잉 게임. AI는 이를 완벽히 인지하고 게임 시스템으로서 유저가 취할 수 있는 다음 행동을 제시.
        1. <context>와 <recent_conversation>을 기반으로 'user'가 취할 수 있는 다음 행동/대사 선택지 3개를 생성.
        2. 단순히 대화만 이어가는 것이 아니라, 1) 순수 행동(상호작용, 이동 등), 2) 대사와 행동의 결합, 3) 짧고 강렬한 대사 등 유저가 능동적으로 상황을 이끌어갈 수 있는 다채로운 형태를 제시.
        3. 유저가 말을 할 수 없는 상태(수면, 물속 등)라면 대사 없이 지문/속마음만 작성.
        4. 대사는 큰따옴표" "로 묶고, 속마음은「 」, 행동/지문은 기호 없이 작성.
        5. 3개의 선택지가 각각 [긍정/수용/접근], [부정/반발/회피], [우회/관찰/제3의 행동]처럼 서로 확실히 다른 감정과 대응 방식을 띠도록 구성.
        </rules>

        <final_instruction>
        위 규칙을 엄격히 준수하여, 자연스럽고 입체적인 다음 행동 선택지 3가지 즉시 JSON 배열 출력.
        </final_instruction>
        """;

        var suggestionSchema = new
        {
            type = "ARRAY",
            description = "유저가 선택할 수 있는 추천 답변 3가지",
            items = new { type = "STRING" }
        };

        // 추천 전용 단발성 Request 생성
        var req = new GeminiRequest(
            SystemInstruction: new Content("system", [new Part("너는 롤플레잉 게임의 유저 선택지 생성기이다.")]),
            Contents: [new Content("user", [new Part(prompt)])],
            SafetySettings: [
                new("HARM_CATEGORY_HARASSMENT", BlockThreshold.BLOCK_NONE),
                new("HARM_CATEGORY_HATE_SPEECH", BlockThreshold.BLOCK_NONE),
                new("HARM_CATEGORY_SEXUALLY_EXPLICIT", BlockThreshold.BLOCK_NONE),
                new("HARM_CATEGORY_DANGEROUS_CONTENT", BlockThreshold.BLOCK_NONE)
            ],
            GenerationConfig: new GenerationConfig(1.0f, 8192, "application/json", suggestionSchema, new ThinkingConfig(ThinkingLevel.low))
        );

        try
        {
            //  30초가 지나면 자동으로 취소(Cancel) 신호를 방출하는 전용 토큰 생성
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            //  Task.WhenAny 경합을 없애고, apiService에 토큰을 직접 넘겨 30초 초과 시 통신 스레드 자체가 파괴되도록 설정
            string jsonResponse = await apiService.SendMessageAsync(req, ModelTier.Flash35, cts.Token);

            if (!string.IsNullOrWhiteSpace(jsonResponse))
            {
                if (jsonResponse.StartsWith("[System"))
                {
                    // [디버그 로그] GeminiApiService가 잡아낸 구체적인 예외 원인을 출력창에 명시
                    System.Diagnostics.Debug.WriteLine($"[Suggestion API Error]: 추천 답변 생성 차단됨 -> {jsonResponse}");
                    return new List<string>();
                }
                return LlmJsonParser.DeserializeArraySafe<List<string>>(jsonResponse) ?? new List<string>();
            }
        }
        catch (OperationCanceledException)
        {
            //  30초 타임아웃이 발생하면 네트워크 소켓이 즉시 끊어지며 이곳으로 빠집니다.
            System.Diagnostics.Debug.WriteLine("[Suggestion Error]: 30초 타임아웃으로 구글 API 통신이 강제 취소되었습니다.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Suggestion Error]: {ex.Message}");
        }

        return new List<string>();
    }
}