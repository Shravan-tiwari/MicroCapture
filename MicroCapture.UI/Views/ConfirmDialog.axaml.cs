using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MicroCapture.UI.Views;

/// <summary>Small reusable "are you sure?" prompt — this app had no confirmation-dialog
/// pattern before the batch-action features (Apply to All/Selection, Delete Selected), which
/// are the first destructive/wide-scope actions that need one. Shown via <see cref="AskAsync"/>
/// as a modal child of the given owner window.</summary>
public partial class ConfirmDialog : Window
{
    private bool _confirmed;

    public ConfirmDialog()
    {
        InitializeComponent();
        var confirmButton = this.FindControl<Button>("ConfirmButton");
        var cancelButton = this.FindControl<Button>("CancelButton");
        if (confirmButton != null) confirmButton.Click += (_, _) => { _confirmed = true; Close(); };
        if (cancelButton != null) cancelButton.Click += (_, _) => { _confirmed = false; Close(); };
    }

    public static async System.Threading.Tasks.Task<bool> AskAsync(Window owner, string message, string title = "Confirm")
    {
        var dialog = new ConfirmDialog { Title = title };
        var messageText = dialog.FindControl<TextBlock>("MessageText");
        if (messageText != null) messageText.Text = message;
        await dialog.ShowDialog(owner);
        return dialog._confirmed;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
