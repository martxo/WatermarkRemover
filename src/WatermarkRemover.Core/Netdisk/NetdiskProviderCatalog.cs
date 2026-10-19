using System.Reflection;

namespace WatermarkRemover.Core.Netdisk;

/// <summary>
/// The registered netdisk products (阿里云盘, …). Discovery makes the extension point real: a new
/// product is one parameterless <see cref="INetdiskProvider"/> class in the
/// WatermarkRemover.Netdisk assembly and it appears in the 网盘 entry and the settings form with
/// no wiring anywhere else.
/// </summary>
public sealed class NetdiskProviderCatalog
{
    private readonly List<INetdiskProvider> _providers = new();

    /// <summary>Registered products, in registration order (the first is the UI default).</summary>
    public IReadOnlyList<INetdiskProvider> All => _providers;

    public void Register(INetdiskProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers.Add(provider);
    }

    /// <summary>
    /// Registers every concrete parameterless <see cref="INetdiskProvider"/> in the assembly.
    /// Types without a true parameterless constructor need dependencies, so they are registered
    /// explicitly through <see cref="Register"/> instead.
    /// </summary>
    public void RegisterDiscovered(Assembly assembly, Action<string, Exception?>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(INetdiskProvider).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            try
            {
                Register((INetdiskProvider)Activator.CreateInstance(type)!);
            }
            catch (Exception ex)
            {
                trace?.Invoke($"注册网盘产品 {type.Name} 失败，已跳过。", ex);
            }
        }
    }
}
