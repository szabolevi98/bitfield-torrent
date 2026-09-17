namespace Bitfield;

static class Program
{
    /// <summary>
    /// Takes a torrent file or magnet link and a directory, so that the window
    /// can be opened straight onto a download — which is what a file
    /// association hands over, and what saves clicking through two dialogs to
    /// see the thing working.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(
            args.Length > 0 ? args[0] : null,
            args.Length > 1 ? args[1] : null));
    }
}
