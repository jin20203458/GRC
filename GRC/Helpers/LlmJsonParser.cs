using System;
using System.Text.Json;

namespace GRC.Helpers 
{
    public static class LlmJsonParser
    {
        /// <summary>
        /// LLM의 응답 텍스트에서 마크다운, 앞뒤 잡담, 잉여 괄호를 무시하고 순수 JSON 문자열만 완벽하게 추출합니다.
        /// </summary>
        public static string? ExtractJson(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse)) return null;

            int startIndex = rawResponse.IndexOf('{');
            if (startIndex < 0) return null; // JSON 객체가 아예 없음

            int openCount = 0;
            int endIndex = -1;
            bool inString = false;
            bool isEscaped = false;

            // 중첩 괄호 및 문자열 내부를 완벽히 추적하여 객체의 끝을 찾음
            for (int i = startIndex; i < rawResponse.Length; i++)
            {
                char c = rawResponse[i];

                if (inString)
                {
                    if (c == '\\') isEscaped = !isEscaped;
                    else if (c == '"' && !isEscaped) { inString = false; isEscaped = false; }
                    else isEscaped = false;
                }
                else
                {
                    if (c == '"') inString = true;
                    else if (c == '{') openCount++;
                    else if (c == '}')
                    {
                        openCount--;
                        if (openCount == 0)
                        {
                            endIndex = i;
                            break;
                        }
                    }
                }
            }

            if (endIndex > startIndex)
            {
                return rawResponse.Substring(startIndex, endIndex - startIndex + 1);
            }

            return null; // 괄호 짝이 맞지 않아 추출 실패
        }

        /// <summary>
        /// 추출과 역직렬화를 한 번에 안전하게 처리하는 래퍼 메서드 (에러 발생 시 방어)
        /// </summary>
        public static T? DeserializeSafe<T>(string rawResponse) where T : class
        {
            string? cleanJson = ExtractJson(rawResponse);
            if (cleanJson == null) return null;

            try
            {
                // LLM의 자잘한 실수를 눈감아주는 관대한 옵션
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };

                return JsonSerializer.Deserialize<T>(cleanJson, options);
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LlmJsonParser] 역직렬화 실패: {ex.Message}");
                return null;
            }
        }


        /// <summary>
        /// LLM 응답에서 순수 JSON 배열([ ]) 문자열만 추출합니다.
        /// </summary>
        public static string? ExtractJsonArray(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse)) return null;

            int startIndex = rawResponse.IndexOf('['); // 배열 시작 기호만 찾음
            if (startIndex < 0) return null;

            int openCount = 0;
            int endIndex = -1;
            bool inString = false;
            bool isEscaped = false;

            // 중첩 괄호 및 문자열 내부를 완벽히 추적하여 배열의 끝을 찾음
            for (int i = startIndex; i < rawResponse.Length; i++)
            {
                char c = rawResponse[i];

                if (inString)
                {
                    if (c == '\\') isEscaped = !isEscaped;
                    else if (c == '"' && !isEscaped) { inString = false; isEscaped = false; }
                    else isEscaped = false;
                }
                else
                {
                    if (c == '"') inString = true;
                    else if (c == '[') openCount++;
                    else if (c == ']')
                    {
                        openCount--;
                        if (openCount == 0)
                        {
                            endIndex = i;
                            break;
                        }
                    }
                }
            }

            if (endIndex > startIndex)
            {
                return rawResponse.Substring(startIndex, endIndex - startIndex + 1);
            }

            return null; // 괄호 짝이 맞지 않아 추출 실패
        }

        /// <summary>
        /// 배열 추출과 역직렬화를 한 번에 안전하게 처리하는 래퍼 메서드
        /// </summary>
        public static T? DeserializeArraySafe<T>(string rawResponse) where T : class
        {
            string? cleanJson = ExtractJsonArray(rawResponse);
            if (cleanJson == null) return null;

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };
                return JsonSerializer.Deserialize<T>(cleanJson, options);
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LlmJsonParser] 배열 역직렬화 실패: {ex.Message}");
                return null;
            }
        }
    }
}