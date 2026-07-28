using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace SimplArchive.DesktopClient.Views;

// Renders a search-highlight snippet (ADR "Search result highlighting") into a TextBlock's Inlines: the
// server sends the fragment with matched terms wrapped in <em>…</em> and everything else HTML-escaped, so we
// split on the tags, HTML-decode each run, and make the emphasized runs bold. Avalonia's TextBlock can't bind
// Inlines directly, hence this attached property.
public static partial class InlineHighlighter
{
    public static readonly AttachedProperty<string?> HtmlProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Html", typeof(InlineHighlighter));

    public static void SetHtml(TextBlock element, string? value) => element.SetValue(HtmlProperty, value);

    public static string? GetHtml(TextBlock element) => element.GetValue(HtmlProperty);

    [GeneratedRegex("<em>(.*?)</em>", RegexOptions.Singleline)]
    private static partial Regex EmTag();

    static InlineHighlighter()
    {
        HtmlProperty.Changed.AddClassHandler<TextBlock>((textBlock, e) =>
        {
            textBlock.Inlines?.Clear();
            var html = e.NewValue as string;
            if (string.IsNullOrEmpty(html))
            {
                return;
            }

            var last = 0;
            foreach (Match match in EmTag().Matches(html))
            {
                if (match.Index > last)
                {
                    textBlock.Inlines?.Add(new Run(WebUtility.HtmlDecode(html[last..match.Index])));
                }

                textBlock.Inlines?.Add(new Run(WebUtility.HtmlDecode(match.Groups[1].Value)) { FontWeight = FontWeight.Bold });
                last = match.Index + match.Length;
            }

            if (last < html.Length)
            {
                textBlock.Inlines?.Add(new Run(WebUtility.HtmlDecode(html[last..])));
            }
        });
    }
}
