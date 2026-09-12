using System.Globalization;
using Microsoft.Playwright;

namespace SimplArchive.UiEndToEndTests;

// A selected list row stays readable in dark mode (#1119).
//
// Dark mode in this client is an IN-APP choice, while the --sa-* custom properties switch on
// prefers-color-scheme. Nothing stamped the explicit choice onto the document, so with the OS in light and
// the app in dark, MudBlazor painted the surface dark and the text light while --sa-selection still answered
// with its LIGHT value: the selected row went near-white under light text and could not be read. Hover fixed
// it, because hover is the one part of that row styled from a mud- variable rather than an --sa- one — which
// is exactly how it was reported ("can't be read while the mouse is not over").
//
// The assertion is CONTRAST, not the attribute. Checking that data-theme is stamped would pass just as
// happily with the tokens still resolving to the wrong theme, which is the failure this is here to catch.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebDarkModeContrastTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDarkModeContrastTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_selected_row_is_readable_in_dark_mode_while_the_os_is_in_light()
    {
        var page = await Ui.LoginAsync(_app);

        // The mismatch IS the bug, so pin the OS side rather than inheriting the runner's: with both sides in
        // dark the tokens happen to be right and this test would prove nothing.
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await page.GetByTitle("Toggle light/dark mode").First.ClickAsync();

        var list = page.Locator("[data-pane='list']");
        await page.GetByText("Demo Repository").First.ClickAsync();
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = "Contracts" }).First;
        await Assertions.Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();

        var selected = list.Locator(".wb-list-row-selected").First;
        await Assertions.Expect(selected).ToBeVisibleAsync();

        // Read the colours the browser actually resolved. Move the pointer away first: hovering is what made
        // the broken state readable, so measuring under the cursor would measure the wrong rule.
        await page.Mouse.MoveAsync(0, 0);
        var colours = await selected.EvaluateAsync<string[]>(
            "el => { const s = getComputedStyle(el); return [s.backgroundColor, s.color]; }");

        var ratio = ContrastRatio(Parse(colours[0]), Parse(colours[1]));
        Assert.True(ratio >= 4.5,
            $"a selected row must stay readable in dark mode: background {colours[0]} against text {colours[1]} "
            + $"is a contrast ratio of {ratio:F2}:1, below the 4.5:1 that body text needs");
    }

    private static (double R, double G, double B) Parse(string rgb)
    {
        var parts = rgb[(rgb.IndexOf('(') + 1)..rgb.IndexOf(')')].Split(',');
        return (double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture),
                double.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    // WCAG 2.1 relative luminance and contrast ratio — the same arithmetic a browser's accessibility panel
    // reports, so a failure here is quotable rather than a number this test invented.
    private static double ContrastRatio((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        var (lighter, darker) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance((double R, double G, double B) c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(double value)
    {
        var v = value / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
