using System.Windows;

namespace NotiRelay;

/// <summary>
/// Asked at launch when messages arrived while NotiRelay was closed. Shown on top of
/// other windows and without an owner, so it is visible even when the app starts in
/// the tray. Closing it (X or Esc) counts as No.
/// </summary>
public partial class MissedMessagesDialog : Window
{
    public MissedMessagesDialog(string details)
    {
        InitializeComponent();

        QuestionText.Text = MissedMessages.Question;
        DetailsText.Text = details;

        Loaded += (_, _) => Activate();
    }

    /// <summary>True when the user chose Yes.</summary>
    public static bool Ask(string details) =>
        new MissedMessagesDialog(details).ShowDialog() == true;

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
