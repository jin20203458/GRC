using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GRC.Helpers;

public static class SimpleMarkdownHelper
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(SimpleMarkdownHelper), new PropertyMetadata(string.Empty, OnTextChanged));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock tb)
        {
            tb.Inlines.Clear();
            string text = (string)e.NewValue ?? string.Empty;
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                // 1. 줄바꿈 기준으로 행 분리
                string[] lines = text.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
                bool inCodeBlock = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];

                    // 다중 라인 코드 블록 (```) 처리
                    if (line.TrimStart().StartsWith("```"))
                    {
                        inCodeBlock = !inCodeBlock;
                        var delimiterSpan = new Span(new Run(line)) { Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)) };
                        tb.Inlines.Add(delimiterSpan);
                        if (i < lines.Length - 1) tb.Inlines.Add(new LineBreak());
                        continue;
                    }

                    // 코드 블록 내부 내용 렌더링
                    if (inCodeBlock)
                    {
                        var codeSpan = new Span(new Run(line)) 
                        { 
                            Foreground = new SolidColorBrush(Color.FromRgb(242, 108, 79)), 
                            FontFamily = new FontFamily("Consolas") 
                        };
                        tb.Inlines.Add(codeSpan);
                        if (i < lines.Length - 1) tb.Inlines.Add(new LineBreak());
                        continue;
                    }

                    int headerLevel = 0;
                    string lineContent = line;
                    bool isBlockquote = false;

                    // 인용구 (>) 처리
                    if (lineContent.TrimStart().StartsWith(">"))
                    {
                        isBlockquote = true;
                        lineContent = lineContent.TrimStart().Substring(1).TrimStart();
                    }

                    // 시작 부분의 # 개수 파악
                    while (headerLevel < lineContent.Length && lineContent[headerLevel] == '#')
                    {
                        headerLevel++;
                    }

                    // # 뒤에 공백이 있으면 헤더로 인식
                    if (headerLevel > 0 && headerLevel < lineContent.Length && lineContent[headerLevel] == ' ')
                    {
                        lineContent = lineContent.Substring(headerLevel + 1);
                    }
                    else
                    {
                        headerLevel = 0; // 헤더가 아닌 일반 줄
                    }

                    // 한 줄을 담을 Span 생성
                    var span = new Span();

                    // 인용구 스타일 적용
                    if (isBlockquote)
                    {
                        span.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));
                        span.FontStyle = FontStyles.Italic;
                    }

                    // 헤더 레벨에 따른 스타일 차등 부여
                    if (headerLevel == 1)
                    {
                        span.FontSize = tb.FontSize + 4;
                        span.FontWeight = FontWeights.Bold;
                        span.Foreground = new SolidColorBrush(Color.FromRgb(242, 242, 242));
                    }
                    else if (headerLevel == 2)
                    {
                        span.FontSize = tb.FontSize + 2;
                        span.FontWeight = FontWeights.Bold;
                        span.Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220));
                    }
                    else if (headerLevel >= 3)
                    {
                        span.FontSize = tb.FontSize + 1;
                        span.FontWeight = FontWeights.Bold;
                        span.Foreground = new SolidColorBrush(Color.FromRgb(0, 139, 153)); // 청록색 포인트 테마
                    }

                    // 2. 해당 행 내부의 인라인 문법 파싱 (**, *, `)
                    var inlineRegex = new Regex(@"(\*\*.*?\*\*)|(\*.*?\*)|(`.*?`)");
                    int lastIndex = 0;

                    foreach (Match match in inlineRegex.Matches(lineContent))
                    {
                        if (match.Index > lastIndex)
                        {
                            string plainText = lineContent.Substring(lastIndex, match.Index - lastIndex);
                            span.Inlines.Add(new Run(plainText));
                        }

                        string value = match.Value;
                        if (value.StartsWith("**") && value.EndsWith("**") && value.Length >= 4)
                        {
                            string content = value.Substring(2, value.Length - 4);
                            span.Inlines.Add(new Bold(new Run(content)));
                        }
                        else if (value.StartsWith("*") && value.EndsWith("*") && value.Length >= 2)
                        {
                            if (value == "**")
                            {
                                span.Inlines.Add(new Run(value));
                            }
                            else
                            {
                                string content = value.Substring(1, value.Length - 2);
                                span.Inlines.Add(new Italic(new Run(content)));
                            }
                        }
                        else if (value.StartsWith("`") && value.EndsWith("`") && value.Length >= 2)
                        {
                            if (value == "``")
                            {
                                span.Inlines.Add(new Run(value));
                            }
                            else
                            {
                                string content = value.Substring(1, value.Length - 2);
                                var codeRun = new Run(content) { Foreground = new SolidColorBrush(Color.FromRgb(242, 108, 79)) };
                                span.Inlines.Add(codeRun);
                            }
                        }

                        lastIndex = match.Index + match.Length;
                    }

                    if (lastIndex < lineContent.Length)
                    {
                        span.Inlines.Add(new Run(lineContent.Substring(lastIndex)));
                    }

                    tb.Inlines.Add(span);

                    // 마지막 줄이 아니면 개행 추가
                    if (i < lines.Length - 1)
                    {
                        tb.Inlines.Add(new LineBreak());
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Markdown Error] Exception occurred during rendering: {ex.Message}\nStack Trace: {ex.StackTrace}");
            }
        }
    }

    /// <summary>
    /// LLM이 태그 없이 응답을 마크다운 코드 블록(```xml ... ```)으로 감싸서 보냈을 때 이를 벗겨내는 헬퍼 메서드입니다.
    /// </summary>
    public static string CleanUpMarkdownFallback(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        int firstTick = text.IndexOf("```", StringComparison.Ordinal);

        // 1. 마크다운 기호가 아예 없으면 원본 그대로 반환
        if (firstTick == -1) return text.Trim();

        int lastTick = text.LastIndexOf("```", StringComparison.Ordinal);

        // 2. 백틱 덩어리가 1개밖에 없는 경우 (형식 붕괴)
        if (firstTick == lastTick)
        {
            return text.Replace("```", "").Trim();
        }

        // 3. 정상적으로 쌍이 맞는 경우에만 내부 추출 시도
        int firstNewLine = text.IndexOf('\n', firstTick);
        if (firstNewLine == -1 || firstNewLine > lastTick)
        {
            return text.Replace("```", "").Trim();
        }

        int contentStart = firstNewLine + 1;
        return text.Substring(contentStart, lastTick - contentStart).Trim();
    }
}