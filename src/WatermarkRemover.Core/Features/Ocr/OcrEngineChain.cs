namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>Outcome of probing one chain link.</summary>
public sealed record OcrEngineLink(string Name, IOcrEngine? Engine, bool Available, string Reason);

/// <summary>
/// Resolves the best usable OCR engine by probing registered factories in priority order,
/// degrading safely: a machine missing one engine simply falls through to the next. The same
/// probe result feeds both runtime selection and the diagnostics report, so the two can never
/// drift apart. When no engine is usable the chain yields an <see cref="UnavailableOcrEngine"/>
/// that fails per job (not at startup) with the full probe history.
/// </summary>
public sealed class OcrEngineChain
{
    public OcrEngineChain(IEnumerable<IOcrEngineFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);

        var links = new List<OcrEngineLink>();
        foreach (IOcrEngineFactory factory in factories)
        {
            try
            {
                if (factory.TryCreate(out IOcrEngine? engine, out string reason) && engine is not null)
                {
                    links.Add(new OcrEngineLink(factory.Name, engine, true, string.Empty));
                    Engine = engine;
                    Links = links;
                    return;
                }

                links.Add(new OcrEngineLink(factory.Name, null, false, reason));
            }
            catch (Exception ex)
            {
                // A broken engine must not break the chain — record it and keep degrading.
                links.Add(new OcrEngineLink(factory.Name, null, false, ex.Message));
            }
        }

        Engine = new UnavailableOcrEngine(DescribeFailures(links));
        Links = links;
    }

    /// <summary>The first usable engine, or a failing stub when the whole chain is unavailable.</summary>
    public IOcrEngine Engine { get; }

    /// <summary>Probe results in chain order; stops at the first available engine.</summary>
    public IReadOnlyList<OcrEngineLink> Links { get; }

    /// <summary>Whether a real engine was resolved (the stub does not count).</summary>
    public bool HasEngine => Links.Any(l => l.Available);

    /// <summary>One line per probed link: "名称：可用" / "名称：不可用（原因）".</summary>
    public string Describe() =>
        string.Join(Environment.NewLine, Links.Select(DescribeLink));

    private static string DescribeLink(OcrEngineLink link) =>
        link.Available ? $"{link.Name}：可用" : $"{link.Name}：不可用（{link.Reason}）";

    private static string DescribeFailures(IReadOnlyList<OcrEngineLink> links) =>
        links.Count == 0
            ? "未注册任何 OCR 引擎"
            : string.Join("；", links.Select(l => $"{l.Name} 不可用（{l.Reason}）"));
}
