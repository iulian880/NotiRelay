using System.Windows;

namespace NotiRelay;

/// <summary>
/// Warning prompt about notifications that would be sent: asked at launch when old ones are
/// waiting, and by Resume sending. Shown on top of other windows and without an owner, so it
/// is visible even when the app starts in the tray. Closing it (X or Esc) counts as No.
/// Nothing is sent while it is open.
/// </summary>
public partial class MissedMessagesDialog : Window
{
    public MissedMessagesDialog(PromptText prompt)
    {
        InitializeComponent();

        Title = "NotiRelay · " + prompt.Title;
        HeadingText.Text = prompt.Title;
        QuestionText.Text = prompt.Question;
        DetailsText.Text = prompt.Details;
        NoteText.Text = prompt.Note;
        YesButton.Content = prompt.YesText;
        NoButton.Content = prompt.NoText;

        Loaded += (_, _) => Activate();
    }

    /// <summary>True when the user chose Yes.</summary>
    public static bool Ask(PromptText prompt) =>
        new MissedMessagesDialog(prompt).ShowDialog() == true;

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
