using Avalonia;
using Avalonia.Controls;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// One tenant settings-group header (#530, tranche 10): the group key rides as the commands' parameter and
/// the editing state toggles pencil vs Save/Cancel. Styled properties rather than bindings for the three
/// per-instance inputs, so the markup stays one line per group.
/// </summary>
public partial class TenantGroupHeader : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<TenantGroupHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> GroupProperty =
        AvaloniaProperty.Register<TenantGroupHeader, string?>(nameof(Group));

    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<TenantGroupHeader, bool>(nameof(IsEditing));

    public TenantGroupHeader()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == TitleProperty)
            {
                TitleBlock.Text = Title;
            }
            else if (e.Property == GroupProperty)
            {
                PencilButton.CommandParameter = Group;
                SaveButton.CommandParameter = Group;
            }
            else if (e.Property == IsEditingProperty)
            {
                CommitPanel.IsVisible = IsEditing;
            }
        };
        CommitPanel.IsVisible = false;
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Group
    {
        get => GetValue(GroupProperty);
        set => SetValue(GroupProperty, value);
    }

    public bool IsEditing
    {
        get => GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }
}
