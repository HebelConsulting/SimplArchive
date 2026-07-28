using System;
using Avalonia.Markup.Xaml;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Localization;

// XAML markup extension: {loc:Tr Key} resolves to the localized string (from the shared SimplArchive.Localization
// resources) for Key at load time. Since the culture is applied at login before the main window is built, no
// live re-evaluation is needed. Avalonia strips the "Extension" suffix, so the usage is {loc:Tr SomeKey}.
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
