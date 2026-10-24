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
    IAppSettingsService appSettingsService,
    IAudioService audioService) : IChatWorkflowService
{
    public string? GetMetaDirective(string inputText, bool isFastForwardEnabled, bool isChapterChanged = false)
    {
        if (isChapterChanged)
        {
            return """
        <meta_directive type="chapter_transition">
        새로운 챕터가 시작되었습니다. 진행 중이던 기존 사건이나 상황을 일단락 짓고, 시간의 흐름이나 새로운 장소/인물을 조명하여 다음 챕터의 새로운 국면으로 전환하십시오.
        </meta_directive>
        """;
        }

        if (isFastForwardEnabled)
        {
            return """
            <meta_directive type="fast_forward">
            유저의 현재 행동을 기점으로 반복적인 과정은 압축하고, 시간의 흐름과 달성한 결과를 밀도 있게 서술한 뒤 곧바로 다음 사건이 발생하는 시점으로 전개하십시오.
            </meta_directive>
            """;
        }
        else if (string.IsNullOrWhiteSpace(inputText))
        {
            return """
            <meta_directive type="auto_continue">
            직전 턴에서 이어진 장면의 다음 단락을 자연스럽게 이어 작성하십시오. 주변 환경을 묘사하거나 NPC의 행동 또는 새로운 사건을 발생시켜 스토리를 능동적으로 전개하십시오.
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
         Func<StatusPayload, Task>? onStatusUpdated = null,
         CancellationToken cancellationToken = default,
         Action<Task>? onStatusTaskStarted = null)
    {
        var result = new ChatStreamResult();
        StringBuilder fullResponse = new();

        var currentSettings = await appSettingsService.LoadSettingsAsync();
        int currentChatDelay = currentSettings.ChatDelay;

        var channel = Channel.CreateUnbounded<char>();
        var prefetchTasks = new ConcurrentQueue<Task<string>>();

        try
        {
            if (!string.IsNullOrWhiteSpace(metaDirective))
            {
                Debug.WriteLine($"[ChatWorkflowService] MetaDirective 적용됨: {metaDirective}");
            }

            // [호출 1] 서사 전용 API 요청 빌드 (Temperature=preset.Temperature, 서사만 출력)
            var requestPayload = await memoryService.BuildNarrativeRequestAsync(userMessage, preset, metaDirective);

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
                bool isTypingSoundStarted = false;
                try
                {
                    await foreach (var c in channel.Reader.ReadAllAsync(cancellationToken))
                    {
                        bool isQuote = (c == '"' || c == '\u201c' || c == '\u201d');
                        if (isQuote)
                        {
                            uiIsDialogue = !uiIsDialogue;
                        }

                        bool shouldMuteTyping = uiIsDialogue || isQuote;
                        if (shouldMuteTyping)
                        {
                            if (isTypingSoundStarted)
                            {
                                audioService.StopTypingSound();
                                isTypingSoundStarted = false;
                            }
                        }
                        else
                        {
                            if (!isTypingSoundStarted)
                            {
                                audioService.StartTypingSound();
                                isTypingSoundStarted = true;
                            }
                        }

                        onCharReceived?.Invoke(c);

                        if (isQuote && !uiIsDialogue) // 대사가 끝나는 시점
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
                                        audioService.StopTypingSound();
                                        isTypingSoundStarted = false;
                                        await onAudioReady(audioPath);
                                    }
                                }
                                catch (Exception ex) { Debug.WriteLine($"[TTS Error]: {ex.Message}"); }
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
                }
                finally
                {
                    audioService.StopTypingSound();
                }
            }, cancellationToken);

            // ==============================================================
            // [생산자]: 스트리밍 수신 태스크 (서사 텍스트 전체를 채널로 전달)
            // ==============================================================
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    bool isDialogue = false;
                    bool isThought = false;

                    StringBuilder dialogueBuffer = new();
                    StringBuilder narrationBuffer = new();

                    await foreach (var chunk in apiService.SendMessageStreamAsync(requestPayload, null, cancellationToken))
                    {
                        foreach (char c in chunk)
                        {
                            // 마크다운 강조 기호(*) 실시간 필터링: 스트리밍 중과 완료 후 불일치 및 따옴표 오인식 원천 방지
                            if (c == '*') continue;

                            fullResponse.Append(c);

                            // 모든 텍스트를 UI 채널로 전달 (서사 모델은 status 태그를 출력하지 않음)
                            channel.Writer.TryWrite(c);

                            if (c == '「' || c == '」')
                            {
                                isThought = !isThought;
                            }
                            else if (c == '"' || c == '\u201c' || c == '\u201d')
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
                finally
                {
                    // 에러가 발생하거나 작업이 취소되더라도 반드시 소비자에게 수신 종료를 알림
                    channel.Writer.Complete();
                }
            }, cancellationToken);

            await producerTask;

            // 서사 텍스트 정제 (마크다운 찌꺼기 제거)
            string responseText = fullResponse.ToString();

            responseText = Regex.Replace(responseText, @"\*\*\s*([""「])", "$1");
            responseText = Regex.Replace(responseText, @"([""」])\s*\*\*", "$1");

            if (string.IsNullOrWhiteSpace(responseText))
            {
                responseText = "(서사가 생성되었습니다)";
            }

            result.FinalText = responseText;

            // ==============================================================
            // [호출 2] 상태창 갱신 API (Temperature=0.6, FlashLite, 방금 1턴 대화만 입력)
            // 백그라운드 스레드에서 비동기 처리하여 입력창 해제 딜레이 0초 실현
            // ==============================================================
            if (!cancellationToken.IsCancellationRequested && !string.IsNullOrWhiteSpace(responseText))
            {
                var statusTask = Task.Run(async () =>
                {
                    try
                    {
                        var statusRequest = memoryService.BuildStatusRequest(
                            userMessage.Text, responseText, preset.StatusUpdateGuide, currentSettings.SafetyThreshold);

                        string statusResponse = await apiService.SendMessageAsync(statusRequest, ModelTier.FlashLite);

                        if (!string.IsNullOrWhiteSpace(statusResponse) && !statusResponse.StartsWith("[System"))
                        {
                            string? cleanJson = GRC.Helpers.LlmJsonParser.ExtractJson(statusResponse);
                            if (!string.IsNullOrEmpty(cleanJson))
                            {
                                var payload = GRC.Helpers.LlmJsonParser.DeserializeSafe<StatusPayload>(cleanJson);
                                if (payload != null && onStatusUpdated != null)
                                {
                                    await onStatusUpdated(payload);
                                }
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"[StatusAPI Error - BG] 상태창 API 비정상 응답: {statusResponse}");
                        }
                    }
                    catch (Exception ex)
                    {
                        // 상태 갱신 실패 시 로그만 남기고 이전 상태 유지 (Graceful Degradation)
                        Debug.WriteLine($"[StatusAPI Error - BG] 상태창 백그라운드 갱신 실패: {ex.Message}");
                    }
                });

                onStatusTaskStarted?.Invoke(statusTask);
            }

            if (onDownloadComplete != null && result.IsSuccess)
            {
                _ = onDownloadComplete(result); // Fire-and-Forget으로 백그라운드 실행
            }
            await typingTask;
        }
        catch (OperationCanceledException)
        {
            // 생성 중단(Cancel) 처리 — 서사 모델은 status 태그를 출력하지 않으므로 텍스트를 그대로 사용
            result.FinalText = fullResponse.ToString().TrimEnd() + "\n\n*[시스템: 사용자에 의해 생성이 중단되었습니다.]*";
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
}