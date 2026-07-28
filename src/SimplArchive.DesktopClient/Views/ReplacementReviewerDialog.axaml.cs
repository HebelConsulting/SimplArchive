using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Picks a replacement reviewer when deactivating a user who still holds pending review tasks (ADR "Workflow
// review reassignment"). ShowDialog<Guid?> returns the chosen reviewer's id, or null if cancelled.
public partial class ReplacementReviewerDialog : Window
{
    public sealed record Candidate(Guid Id, string Name);

    public ReplacementReviewerDialog()
        : this("", [])
    {
    }

    public ReplacementReviewerDialog(string userName, IReadOnlyList<(Guid Id, string Name)> candidates)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(userName))
        {
            Prompt.Text = $"{userName} still holds pending review tasks. Choose a replacement reviewer to hand them to before deactivating.";
        }

        ReviewerBox.ItemsSource = candidates.Select(c => new Candidate(c.Id, c.Name)).ToList();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) =>
        Close(ReviewerBox.SelectedItem is Candidate c ? c.Id : (Guid?)null);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
