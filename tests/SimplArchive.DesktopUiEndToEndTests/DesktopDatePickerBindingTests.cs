using System.Reflection;
using System.Xml.Linq;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

/// <summary>
/// Avalonia ships TWO date controls whose SelectedDate has DIFFERENT types: <c>CalendarDatePicker</c> takes
/// <c>DateTime?</c>, <c>DatePicker</c> takes <c>DateTimeOffset?</c>. Binding the wrong one compiles, passes
/// every C# test, and fails only at BINDING time — "Could not cast DateTimeOffset to DateTime?" — leaving the
/// control silently uneditable. That is what happened to every Date and DateTime MASK field: the owner met it
/// on the Enrollment mask, and it had been there since the field editor was written.
/// </summary>
/// <remarks>
/// <para>
/// The check is exact where the AXAML says what it binds to — a <c>DataTemplate</c> with <c>x:DataType</c>
/// names the type, which is precisely the shape the mask-field editor uses — and silent elsewhere, because a
/// pane's DataContext is not declared in the file and guessing it produces noise.
/// </para>
/// <para>
/// The loose alternative was tried first and is recorded here because it looked convincing and was useless:
/// "some view-model has a property of this name with the right type" passes happily while the bound one is
/// wrong — <c>FieldFilterRowViewModel.DateValue</c> is a <c>DateTime?</c> and satisfied the rule for the very
/// binding that was broken. A guard that green-lights the defect it was written for is worse than none.
/// </para>
/// </remarks>
public class DesktopDatePickerBindingTests
{
    [Fact]
    public void Every_typed_CalendarDatePicker_binding_targets_a_nullable_DateTime()
    {
        var checkedBindings = new List<string>();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ViewsDirectory(), "*.axaml", SearchOption.AllDirectories))
        {
            foreach (var picker in XDocument.Load(file).Descendants()
                .Where(e => e.Name.LocalName == "CalendarDatePicker"))
            {
                if (BindingPath(picker.Attribute("SelectedDate")?.Value) is not { } path
                    || DeclaredDataType(picker) is not { } declared)
                {
                    continue; // untyped context: the file does not say, and guessing is how a guard becomes noise
                }

                var viewModel = typeof(MainWindowViewModel).Assembly.GetTypes()
                    .FirstOrDefault(t => t.Name == declared);
                var property = viewModel?.GetProperty(path, BindingFlags.Public | BindingFlags.Instance);
                if (property is null)
                {
                    continue;
                }

                checkedBindings.Add($"{declared}.{path}");
                if (property.PropertyType != typeof(DateTime?))
                {
                    var actual = Nullable.GetUnderlyingType(property.PropertyType) is { } inner
                        ? $"{inner.Name}?"
                        : property.PropertyType.Name;
                    offenders.Add($"  {Path.GetFileName(file)}: {{Binding {path}}} on {declared} is "
                        + $"{actual} — CalendarDatePicker.SelectedDate is DateTime? "
                        + "(DatePicker is the DateTimeOffset? one), so this binding fails at runtime.");
                }
            }
        }

        // Anti-vacuous: the mask-field editor's two pickers are the typed case this exists for, so finding
        // nothing means the parse broke, not that the client is clean.
        Assert.NotEmpty(checkedBindings);
        Assert.True(offenders.Count == 0, "A CalendarDatePicker cannot take what it is bound to:\n" + string.Join("\n", offenders));
    }

    /// <summary>The property name out of a <c>{Binding Foo}</c> attribute — null when it is not a plain binding.</summary>
    private static string? BindingPath(string? attribute)
    {
        if (attribute is null || !attribute.TrimStart().StartsWith("{Binding", StringComparison.Ordinal))
        {
            return null;
        }

        var inner = attribute.Trim().TrimStart('{').TrimEnd('}')["Binding".Length..].Trim();
        var path = inner.Split(',')[0].Trim();          // drop Mode=, Converter=, …
        return path.Length == 0 || path.Contains('$') ? null : path.Split('.')[^1];
    }

    /// <summary>The nearest enclosing DataTemplate's <c>x:DataType</c>, unprefixed — the type the bindings
    /// inside it resolve against, and the only place the AXAML states it.</summary>
    private static string? DeclaredDataType(XElement element)
    {
        for (var e = element; e is not null; e = e.Parent)
        {
            var dataType = e.Attributes().FirstOrDefault(a => a.Name.LocalName == "DataType")?.Value;
            if (dataType is { Length: > 0 })
            {
                return dataType.Split(':')[^1];
            }
        }

        return null;
    }

    // The same walk DesktopSearchFieldClearableTests uses to reach the AXAML from the test binaries.
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
