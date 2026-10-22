using WatermarkRemover.Core.Features.Search;
using WatermarkRemover.Index.Contracts;
using WatermarkRemover.Index.Ipc;
using WatermarkRemover.Index.Setup;

namespace WatermarkRemover.Index;

/// <summary>Optional capability: a provider that can report its live state asynchronously.</summary>
public interface IIndexAvailability
{
    IndexStatusDto? LastStatus { get; }

    Task<bool> CheckAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>Optional capability: the page can enable a self-hosted index with one elevation.</summary>
public interface IIndexSetup
{
    bool IsInstalled { get; }

    bool NeedsUpdate { get; }

    string InstalledVersion { get; }

    Task<bool> InstallAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IFileSearchProvider"/> over the local index host. This is the Everything-shaped
/// split: the un-elevated app asks a privileged background process over a named pipe.
/// </summary>
public sealed class IndexServiceSearchProvider : IFileSearchProvider, IIndexAvailability, IIndexSetup
{
    private readonly IIndexClient _client;
    private readonly IndexSetupService _setup;
    private volatile bool _available;

    public IndexServiceSearchProvider()
        : this(new IndexPipeClient(), new IndexSetupService())
    {
    }

    public IndexServiceSearchProvider(IIndexClient client, IndexSetupService setup)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
    }

    public string DisplayName => "内置索引";

    public bool IsAvailable => _available;

    public IndexStatusDto? LastStatus { get; private set; }

    public bool IsInstalled => _setup.IsInstalled;

    public bool NeedsUpdate => _setup.NeedsUpdate;

    public string InstalledVersion => _setup.InstalledVersion;

    public async Task<bool> CheckAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (await PingAsync(cancellationToken).ConfigureAwait(false)) return true;

        // The logon task may simply not have fired yet (first run after login). Starting it
        // through the scheduler needs no elevation because the task already exists.
        if (!_setup.IsInstalled) return false;
        _setup.TryStartInstalled();

        for (int attempt = 0; attempt < 25; attempt++)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            if (await PingAsync(cancellationToken).ConfigureAwait(false)) return true;
        }

        return false;
    }

    public async Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return Array.Empty<FileSearchResult>();

        IndexResponse? response = await _client.SendAsync(
            new IndexRequest { Cmd = "search", Query = query, Max = maxResults },
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);

        if (response is not { Ok: true } || response.Results is null)
        {
            if (response is { Ok: false }) _available = false;
            return Array.Empty<FileSearchResult>();
        }

        _available = true;
        return response.Results
            .Select(hit => new FileSearchResult(hit.Path, hit.IsFolder, hit.Size, hit.ModifiedAt))
            .ToList();
    }

    public Task<bool> InstallAsync(CancellationToken cancellationToken = default) =>
        _setup.InstallAsync(cancellationToken);

    private async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        IndexResponse? response = await _client.SendAsync(
            new IndexRequest { Cmd = "status" },
            TimeSpan.FromMilliseconds(400),
            cancellationToken).ConfigureAwait(false);

        if (response is not { Ok: true, Status: not null })
            return false;

        LastStatus = response.Status;
        _available = true;
        return true;
    }
}
