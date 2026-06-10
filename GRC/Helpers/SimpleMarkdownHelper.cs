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

            tb.Inlines.Add(new Run(text));
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