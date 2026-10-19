namespace WatermarkRemover.Core.Features.Naming;

/// <summary>
/// Facade for content-based naming: recognised text lines in, an
/// <see cref="AutoNameResult"/> out (or null when the document cannot be named confidently).
/// Pipeline: classify the document type, then extract the customer name via the type's labels.
/// </summary>
public static class SmartNamingService
{
    /// <summary>
    /// Returns null when the type is unrecognised or the name cannot be extracted. Each decision
    /// point reports through <paramref name="trace"/> so a failing machine's log tells which step
    /// gave up ("识别后不能自动重命名" is otherwise invisible).
    /// </summary>
    public static AutoNameResult? TryBuildName(IReadOnlyList<string> ocrLines, Action<string, Exception?>? trace = null)
    {
        if (ocrLines is null || ocrLines.Count == 0)
        {
            trace?.Invoke("命名：OCR 未识别到任何文字，跳过自动命名", null);
            return null;
        }

        var profile = DocumentClassifier.Classify(string.Join("\n", ocrLines));
        if (profile is null)
        {
            trace?.Invoke($"命名：分类未识别（OCR {ocrLines.Count} 行），跳过自动命名", null);
            return null;
        }

        if (profile.NameStrategy == NameExtractionStrategy.FixedRegion)
        {
            // Fixed-layout type: the name is read geometrically from the page image (the
            // App-side region locator). Guessing from shredded full-page OCR text produced
            // garbage names (高级领导征信) — disabled on purpose for these types.
            trace?.Invoke($"命名：类型={profile.TypeShortName} 为固定版式，文本通道不猜名（走区域定位）", null);
            return null;
        }

        string? personName = NameExtractor.Extract(ocrLines, profile);
        if (personName is null)
        {
            trace?.Invoke($"命名：姓名提取失败（类型={profile.TypeShortName}），跳过自动命名", null);
            return null;
        }

        var result = AutoNameResult.Create(personName, profile.TypeShortName);
        trace?.Invoke($"命名：自动命名成功 → {result.SuggestedStem}", null);
        return result;
    }
}
