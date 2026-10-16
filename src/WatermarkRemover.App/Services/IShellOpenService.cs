using System.Diagnostics;
using System.IO;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Revealing files and folders in the system shell. One implementation for every "open the
/// output folder" style action instead of scattered Process.Start calls.
/// </summary>
public interface IShellOpenService
{
    /// <summary>Opens the directory in Explorer. False with a human-readable error on failure.</summary>
    bool TryOpenDirectory(string directory, out string? error);
}

public sealed class ShellOpenService : IShellOpenService
{
    public static readonly ShellOpenService Default = new();

    public bool TryOpenDirectory(string directory, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
