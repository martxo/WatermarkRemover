using Microsoft.VisualBasic.FileIO;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Sends a file or folder to the Recycle Bin. Search delete is one click next to 打开, so it must
/// stay undoable — a permanent delete would be too easy to trigger by accident. Behind an
/// interface so the view model can be tested without touching the real shell.
/// </summary>
public interface IRecycleBinService
{
    /// <summary>Moves the path to the Recycle Bin; false leaves the filesystem alone.</summary>
    bool TryDelete(string path, bool isDirectory, out string? error);
}

public sealed class RecycleBinService : IRecycleBinService
{
    public static readonly RecycleBinService Default = new();

    public bool TryDelete(string path, bool isDirectory, out string? error)
    {
        try
        {
            if (isDirectory)
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
            else
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // Unsupported locations (some network shares), locks and permission failures all land
            // here; the message is the shell's own, shown verbatim on the status line.
            error = ex.Message;
            return false;
        }
    }
}
