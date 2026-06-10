using GRC.Helpers;
using GRC.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
namespace GRC.Services;

public class ChatWorkflowService(
    IGeminiApiService apiService,
    IMemoryManagerService memoryService,
    IAppSettingsService appSettingsService) : IChatWorkflowService
{
    public string? GetMetaDirective(string inputText, bool isFastForwardEnabled, 
        Dictionary<string, string>? currentStats, List<string>? triggeredEvents, 
        bool isChapterChanged = false)
    {
        if (isChapterChanged)
        {
            return """
        <meta_directive>
        [시스템 개입: 챕터 전환 (New Chapter)]
        새로운 챕터가 시작되었습니다. 
        진행 중이던 기존 사건이나 상황을 억지스럽지 않게 자연스럽게 마무리(일단락) 지으십시오.
        그 후, 시간의 흐름을 요약하거나 새로운 장소/인물을 조명하는 방식을 통해 이야기의 막을 넘기듯 다음 챕터의 새로운 국면으로 부드럽게 전환하십시오.
        </meta_directive>
        """;
        }

        // 1. 동적 상태창 이벤트 검사
        if (currentStats != null && currentStats.Count > 0)
        {
            string[] triggerKeywords = { "호감", "애정", "집착", "관계", "순종", "굴복", "타락", "혐오", "증오", "죄책감", "수치심", "이성", "침식", "성욕", "붕괴" };
            var targetKey = currentStats.Keys.FirstOrDefault(k =>
            triggerKeywords.Any(keyword => k.Contains(keyword)) &&
            (triggeredEvents == null || !triggeredEvents.Contains(k)));

            if (targetKey != null)
            {
                var (statValue, maxValue) = ParseStatValue(currentStats[targetKey]);
                if (statValue >= maxValue && maxValue > 0)
                {
                    triggeredEvents?.Add(targetKey);
                    Debug.WriteLine($"[MetaDirective Triggered MAX] {targetKey} reached {statValue}/{maxValue}");
                    return $"""
                    <meta_directive>
                    [시스템 개입: 극적 상황 발생 (수치 도달)]
                    '{targetKey}' 수치가 {maxValue}(최대치)에 도달했습니다!
                    이번 턴에 평범한 묘사를 중단하고, 유저에게 현재의 극대화된 '{targetKey}'을(를) 여과 없이 표현하는 결정적인 이벤트를 주도적으로 묘사하십시오.
                    </meta_directive>
                    """;
                }
                else if (statValue <= 0)
                {
                    Debug.WriteLine($"[MetaDirective Triggered MIN] {targetKey} reached {statValue}");
                    triggeredEvents?.Add(targetKey);
                    return $"""
                    <meta_directive>
                    [시스템 개입: 극적 상황 발생 (수치 상실 및 소멸)]
                    '{targetKey}' 수치가 0에 도달하여 완전히 소멸되었습니다.
                    이번 턴에 평범한 묘사를 중단하고, 캐릭터에게서 '{targetKey}'이(가) 완벽하게 사라졌을 때 나타나는 극단적인 태도 변화나 결정적인 결과를 주도적으로 묘사하십시오.
                    </meta_directive>
                    """;
                }
            }
        }

        // 2. 스킵 및 Continue 로직
        if (isFastForwardEnabled)
        {
            return """
            <meta_directive>
            [시스템 개입: 서사 압축 및 고속 전개(Fast-Forward)]
            유저의 현재 행동을 기점으로 반복적이고 소모적인 과정은 영화의 몽타주 기법처럼 짧게 요약하십시오. 개연성을 해치며 맥락 없이 건너뛰지 말고, '시간의 흐름'과 '그동안 달성한 결과물'을 밀도 높은 지문으로 묘사한 뒤, 곧바로 다음의 의미 있는 사건(Next Scene)이 발생하는 시점으로 자연스럽게 전환하십시오.
            </meta_directive>
            """;
        }
        else if (string.IsNullOrWhiteSpace(inputText))
        {
            return """
            <meta_directive>
            [시스템 명령: 서사 이어쓰기 (Auto-Continue)]
            직전 턴에서 묘사했던 상황, 대화, 혹은 멈췄던 장면에서 곧바로 이어지는 '다음 단락'을 소설가처럼 자연스럽게 이어서 작성하십시오.
            원래 진행되던 흐름에 맞춰 주변 환경을 묘사하거나, NPC를 움직이거나, 새로운 사건을 발생시켜 스토리를 다음 국면으로 능동적으로 전개하십시오.
            </meta_directive>
            """;
        }

        return null;
    }

    public async Task<ChatStreamResult> ProcessChatStreamAsync(
         ChatMessage userMessage,
         CharacterPreset preset,
         string? metaDirective = null,
         Action<char>? onCharReceived = null,
         Func<string, string, Task<string>>? onDialoguePrefetch = null,
         Func<string, Task>? onAudioReady = null,
         Func<ChatStreamResult, Task>? onDownloadComplete = null,
         CancellationToken cancellationToken = default)
    {
        var result = new ChatStreamResult();
        StringBuilder fullResponse = new();
        StringBuilder statusBuffer = new();

        var currentSettings = await appSettingsService.LoadSettingsAsync();
        int currentChatDelay = currentSettings.ChatDelay;

        var channel = Channel.CreateUnbounded<char>();
        var prefetchTasks = new ConcurrentQueue<Task<string>>();

        try
        {
            var requestPayload = await memoryService.BuildRequestAsync(userMessage, preset, metaDirective);

            if (memoryService.ConsumeAnchorFlag() && requestPayload.GenerationConfig != null)
            {
                requestPayload = requestPayload with { GenerationConfig = requestPayload.GenerationConfig with { ThinkingConfig = new ThinkingConfig(ThinkingLevel.high) } };
                Debug.WriteLine("[ThinkingConfig Applied] Anchor detected, applying high ThinkingLevel for more coherent responses.");
            }

            // ==============================================================
            // [소비자]: UI 타이핑 전용 백그라운드 태스크 (설정된 속도로 천천히 출력)
            // ==============================================================
            var typingTask = Task.Run(async () =>
            {
                bool uiIsDialogue = false;
                await foreach (var c in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    onCharReceived?.Invoke(c);

                    if (c == '"' || c == '“' || c == '”')
                    {
                        uiIsDialogue = !uiIsDialogue;
                        if (!uiIsDialogue) // 대사가 끝나는 시점
                        {
                            if (prefetchTasks.TryDequeue(out var ttsTask))
                            {
                                try
                                {
                                    string audioPath = await ttsTask;

                                    // 다운로드 대기 중 취소 버튼을 눌렀다면 종료
                                    if (cancellationToken.IsCancellationRequested) break;

                                    if (!string.IsNullOrEmpty(audioPath) && onAudioReady != null)
                                    {
                                        await onAudioReady(audioPath);
                                    }
                                }
                                catch (Exception ex) { Debug.WriteLine($"[TTS Error]: {ex.Message}"); }
                            }
                        }
                    }

                    if (currentChatDelay > 0)
                    {
                        await Task.Delay(currentChatDelay, cancellationToken);
                    }
                    else
                    {
                        await Task.Yield();
                    }
                }
            }, cancellationToken);

            // ==============================================================
            // [생산자]: 스트리밍 수신 및 버퍼 파싱 태스크 (빠르게 다운로드)
            // ==============================================================
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    bool isStatusTagStarted = false;
                    bool isDialogue = false;
                    bool isThought = false;

                    StringBuilder dialogueBuffer = new();
                    StringBuilder narrationBuffer = new();

                    await foreach (var chunk in apiService.SendMessageStreamAsync(requestPayload, null, cancellationToken))
                    {
                        foreach (char c in chunk)
                        {
                            fullResponse.Append(c);
                            string currentFullText = fullResponse.ToString();

                            // 상태창 태그 파싱
                            if (!isStatusTagStarted && currentFullText.EndsWith("<status>"))
                            {
                                isStatusTagStarted = true;
                                continue;
                            }
                            else if (isStatusTagStarted && currentFullText.EndsWith("</status>"))
                            {
                                isStatusTagStarted = false;
                                continue;
                            }

                            if (isStatusTagStarted)
                            {
                                statusBuffer.Append(c);
                                continue;
                            }

                            // 일반 텍스트 처리
                            if (!isStatusTagStarted)
                            {
                                // 백그라운드에서 버퍼(채널)로 빛의 속도로 던짐 (UI 스레드 간섭 X)
                                channel.Writer.TryWrite(c);

                                if (c == '「' || c == '」')
                                {
                                    isThought = !isThought;
                                }
                                else if (c == '"' || c == '“' || c == '”')
                                {
                                    isDialogue = !isDialogue;

                                    if (isDialogue)
                                    {
                                        dialogueBuffer.Clear();
                                    }
                                    else
                                    {
                                        string completedDialogue = dialogueBuffer.ToString().Trim();
                                        string contextualNarration = narrationBuffer.ToString().Trim();

                                        // 💡 신버전 로직: 대사와 서사를 함께 TTS 엔진에 예약
                                        if (!string.IsNullOrWhiteSpace(completedDialogue) && onDialoguePrefetch != null)
                                        {
                                            prefetchTasks.Enqueue(onDialoguePrefetch(contextualNarration, completedDialogue));
                                        }
                                        narrationBuffer.Clear();
                                    }
                                }
                                else if (isDialogue)
                                {
                                    dialogueBuffer.Append(c);
                                }
                                else if (!isThought && c != '\n' && c != '\r')
                                {
                                    narrationBuffer.Append(c);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    // 에러가 발생하거나 작업이 취소되더라도 반드시 소비자에게 수신 종료를 알림
                    channel.Writer.Complete();
                }
            }, cancellationToken);

            await producerTask;

            // 2. 다운로드가 끝났으므로 텍스트 정제 및 상태창 데이터 역직렬화를 미리 수행합니다.
            string responseText = fullResponse.ToString();
            if (statusBuffer.Length > 0)
            {
                string? cleanJson = GRC.Helpers.LlmJsonParser.ExtractJson(statusBuffer.ToString());
                if (!string.IsNullOrEmpty(cleanJson))
                    result.StatusPayload = GRC.Helpers.LlmJsonParser.DeserializeSafe<StatusPayload>(cleanJson);
            }

            if (statusBuffer.Length > 0)
            {
                string? cleanJson = GRC.Helpers.LlmJsonParser.ExtractJson(statusBuffer.ToString());
                if (!string.IsNullOrEmpty(cleanJson))
                {
                    result.StatusPayload = GRC.Helpers.LlmJsonParser.DeserializeSafe<StatusPayload>(cleanJson);
                }
            }

            int statusStartIndex = responseText.IndexOf("<status>");
            if (statusStartIndex != -1)
            {
                responseText = responseText.Substring(0, statusStartIndex).Trim();
            }
            else
            {
                int lastBracket = responseText.LastIndexOf('<');
                if (lastBracket != -1 && "<status>".StartsWith(responseText.Substring(lastBracket)))
                {
                    responseText = responseText.Substring(0, lastBracket).Trim();
                }
            }

            if (string.IsNullOrEmpty(responseText))
            {
                responseText = "*(상태가 갱신되었습니다)*";
            }

            // 불필요한 마크다운 찌꺼기 제거
            responseText = Regex.Replace(responseText, @"\*\*\s*([""“”「])", "$1");
            responseText = Regex.Replace(responseText, @"([""“”」])\s*\*\*", "$1");

            result.FinalText = responseText;

            if (onDownloadComplete != null && result.IsSuccess)
            {
                _ = onDownloadComplete(result); // Fire-and-Forget으로 백그라운드 실행
            }
            await typingTask;
        }
        catch (OperationCanceledException)
        {
            // 생성 중단(Cancel) 처리
            string interruptedText = fullResponse.ToString();
            int statusCutIndex = interruptedText.IndexOf("<st");

            if (statusCutIndex != -1)
            {
                interruptedText = interruptedText.Substring(0, statusCutIndex).TrimEnd();
            }

            result.FinalText = interruptedText + "\n\n*[시스템: 사용자에 의해 생성이 중단되었습니다.]*";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProcessChatStream Error] {ex.Message}");
            result.ErrorMessage = ex.Message;
            // 에러가 발생해도 지금까지 받은 텍스트는 보여주도록 처리
            if (string.IsNullOrEmpty(result.FinalText)) result.FinalText = fullResponse.ToString();
        }

        return result;
    }

    private (int Current, int Max) ParseStatValue(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return (0, 100);

        // "10/300" 형태처럼 '/' 기호가 포함된 경우
        if (rawValue.Contains("/"))
        {
            var parts = rawValue.Split('/');
            if (parts.Length == 2 &&
                int.TryParse(Regex.Match(parts[0], @"-?\d+").Value, out int current) &&
                int.TryParse(Regex.Match(parts[1], @"-?\d+").Value, out int max))
            {
                return (current, max);
            }
        }

        // 단일 숫자일 경우 (예: "50") 기존처럼 최대치를 알 수 없으므로 100으로 간주
        var match = Regex.Match(rawValue, @"-?\d+");
        if (match.Success && int.TryParse(match.Value, out int res))
        {
            return (res, 100);
        }

        return (0, 100);
    }
}