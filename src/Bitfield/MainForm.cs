namespace Bitfield;

// The shell the download view, peer list and piece map are hung off. It holds
// nothing yet: the transfer engine lands in Bitfield.Core first, and the window
// grows around it one milestone at a time.
sealed class MainForm : Form
{
    public MainForm()
    {
        Text = "Bitfield Torrent";
        ClientSize = new Size(1100, 700);
        MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterScreen;
    }
}
