using System.Text.RegularExpressions;

namespace SimplArchive.UiEndToEndTests;

// Every search/filter field is clearable (#503). The behaviour itself is toolkit-bound — a real TextBox, a real
// click, a real caret — and is covered end-to-end by the `--searchclear-test` hook. What a test CAN hold on to is
// the wiring, which is the half that rots: the × was inline markup on the Search tab and nowhere else for a year,
// and nothing pointed that out.
//
// So this asserts the RULE rather than a count: a field the user types a filter into carries the attached
// property. A sixth one added next month fails here on the day it is added, which a hand-maintained number would
// not do.
public class DesktopSearchFieldClearableTests
{
    // Bound to a *Query/*Filter property, or watermarked with a Filter/Search/Find placeholder — the two shapes a
    // field-you-type-a-filter-into actually takes in this codebase.
    private static readonly Regex FiltersOnBinding = new(@"Text=""\{Binding [^}""]*(Query|Filter)\b", RegexOptions.Compiled);
    private static readonly Regex FiltersOnWatermark = new(@"Watermark=""\{loc:Tr (Filter|Search|Find)", RegexOptions.Compiled);

    [Fact]
    public void Every_search_or_filter_text_box_is_clearable()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(ViewsDirectory(), "*.axaml"))
        {
            foreach (var element in TextBoxElements(File.ReadAllText(file)))
            {
                var isFilterField = FiltersOnBinding.IsMatch(element) || FiltersOnWatermark.IsMatch(element);
                if (isFilterField && !element.Contains("SearchField.Clearable=\"True\""))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {Squash(element)}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"These filter fields are not clearable — add v:SearchField.Clearable=\"True\" (#503):{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_rule_finds_the_fields_it_is_meant_to_be_guarding()
    {
        // Without this, a regex that silently matched nothing would report a clean sweep forever. Five fields
        // exist today: the search query, the created-by filter, the index-field facet value, the audit action
        // filter, and find-in-document.
        var found = Directory.GetFiles(ViewsDirectory(), "*.axaml")
            .SelectMany(f => TextBoxElements(File.ReadAllText(f)))
            .Count(e => FiltersOnBinding.IsMatch(e) || FiltersOnWatermark.IsMatch(e));

        Assert.True(found >= 5, $"the rule matched only {found} fields; it has stopped seeing the shape it guards");
    }

    // Each `<TextBox …>` up to the end of its opening tag — enough to read its attributes, whether it is
    // self-closing or has children.
    private static IEnumerable<string> TextBoxElements(string xaml)
    {
        foreach (Match match in Regex.Matches(xaml, @"<TextBox\b[^>]*>"))
        {
            yield return match.Value;
        }
    }

    private static string Squash(string element) =>
        Regex.Replace(element, @"\s+", " ").Trim();

    private static string ViewsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "SimplArchive.DesktopClient", "Views");
    }
}
