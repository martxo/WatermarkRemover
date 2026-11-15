using WatermarkRemover.Core.Netdisk;
using System.IO;

namespace WatermarkRemover.App.Services;

public sealed record NetdiskUploadTarget(
    string ProviderId,
    string AccountName,
    string DriveName,
    string RemoteDirectory);

/// <summary>UI-independent uploads with serialized sessions and latest-credential persistence.</summary>
public sealed class NetdiskTransferService
{
    private readonly NetdiskProviderCatalog _catalog;
    private readonly Func<string, string, NetdiskAccountState?> _accountResolver;
    private readonly Action<NetdiskAccountState> _accountPersisted;
    private readonly SemaphoreSlim _uploadGate = new(1, 1);

    public NetdiskTransferService(
        NetdiskProviderCatalog catalog,
        Func<string, string, NetdiskAccountState?> accountResolver,
        Action<NetdiskAccountState> accountPersisted)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _accountResolver = accountResolver ?? throw new ArgumentNullException(nameof(accountResolver));
        _accountPersisted = accountPersisted ?? throw new ArgumentNullException(nameof(accountPersisted));
    }

    public async Task UploadFileAsync(
        NetdiskUploadTarget target,
        string localPath,
        CancellationToken cancellationToken = default,
        bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(localPath);

        await _uploadGate.WaitAsync(cancellationToken);
        try
        {
            var account = ResolveAccount(target);
            var provider = ResolveProvider(target);
            await UploadWithSessionAsync(target, account, provider, async (session, folderId) =>
            {
                await session.UploadAsAsync(
                    folderId, localPath, Path.GetFileName(localPath), overwrite, null, cancellationToken);
            }, cancellationToken);
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    public async Task<IReadOnlyList<(string Path, string Error)>> UploadFilesAsync(
        NetdiskUploadTarget target,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return Array.Empty<(string Path, string Error)>();

        await _uploadGate.WaitAsync(cancellationToken);
        try
        {
            var account = ResolveAccount(target);
            var provider = ResolveProvider(target);
            var failures = new List<(string Path, string Error)>();
            try
            {
                await UploadWithSessionAsync(target, account, provider, async (session, folderId) =>
                {
                    foreach (string path in files)
                    {
                        try
                        {
                            await session.UploadAsAsync(
                                folderId, path, Path.GetFileName(path), false, null, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            failures.Add((path, ex.Message));
                        }
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return files.Select(path => (Path: path, Error: ex.Message)).ToList();
            }

            return failures;
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    private NetdiskAccountState ResolveAccount(NetdiskUploadTarget target) =>
        _accountResolver(target.ProviderId, target.AccountName)
        ?? throw new InvalidOperationException($"未找到网盘账号「{target.AccountName}」。");

    private INetdiskProvider ResolveProvider(NetdiskUploadTarget target) =>
        _catalog.All.FirstOrDefault(provider => provider.Id == target.ProviderId)
        ?? throw new InvalidOperationException($"未找到网盘产品「{target.ProviderId}」。");

    private async Task UploadWithSessionAsync(
        NetdiskUploadTarget target,
        NetdiskAccountState account,
        INetdiskProvider provider,
        Func<INetdiskSession, string, Task> upload,
        CancellationToken cancellationToken)
    {
        await using var session = await provider.ConnectAsync(account.ToCredentials(), cancellationToken);
        try
        {
            string folderId = await ResolveUploadFolderAsync(session, target, cancellationToken);
            await upload(session, folderId);
        }
        finally
        {
            _accountPersisted(new NetdiskAccountState(
                target.ProviderId, target.AccountName, session.CurrentCredentials.Values));
        }
    }

    private static async Task<string> ResolveUploadFolderAsync(
        INetdiskSession session,
        NetdiskUploadTarget target,
        CancellationToken cancellationToken)
    {
        string driveName = target.DriveName?.Trim() ?? string.Empty;
        if (driveName.Length > 0)
        {
            var drive = session.Drives.FirstOrDefault(d =>
                string.Equals(d.Name, driveName, StringComparison.OrdinalIgnoreCase));
            if (drive is not null &&
                !string.Equals(drive.Id, session.CurrentDriveId, StringComparison.Ordinal))
            {
                await session.SwitchDriveAsync(drive.Id, cancellationToken);
            }
        }

        string directory = target.RemoteDirectory?.Trim() ?? string.Empty;
        return directory.Length == 0
            ? string.Empty
            : await session.EnsureFolderAsync(directory, cancellationToken);
    }
}
