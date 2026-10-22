namespace WatermarkRemover.Index.Ntfs;

/// <summary>
/// Picks the volumes worth indexing. Mirrors Everything's default
/// (auto_include_fixed_volumes): local fixed disks only, NTFS only.
/// </summary>
public static class VolumeCatalog
{
    public static IReadOnlyList<string> GetFixedNtfsRoots()
    {
        var roots = new List<string>();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                if (!drive.IsReady) continue;
                roots.Add(drive.RootDirectory.FullName);
            }
            catch (IOException)
            {
                // A volume that vanished mid-enumeration is simply not indexed.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: skip rather than abort the whole catalog.
            }
        }

        return roots;
    }
}
