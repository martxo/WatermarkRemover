namespace WatermarkRemover.Core.Features.Naming;

using WatermarkRemover.Core.Features.Layout;

/// <summary>
/// Facade for content-based naming: recognised text lines in, an
/// <see cref="AutoNameResult"/> out (or null when the document cannot be named confidently).
/// Pipeline: classify the document type, then extract the customer name via the type's labels.
/// </summary>
public static class SmartNamingService
{
    /// <summary>
    /// Bounded-OCR entry. 竖排文本行 first: spread label columns (名 称 / 类 型 / 经 营 者) come
    /// back from the engine as one tall box merging the rows' first characters — 名类 / 类经 —
    /// text that reads exactly like a 2-char name. On geometry alone (height ≫ width) such
    /// strips are dropped here, so neither the label shapes nor the standalone-row fallback can
    /// ever harvest them regardless of OCR line order (名类执照 / 名光执照 bug).
    /// </summary>
    public static AutoNameResult? TryBuildName(IReadOnlyList<RecognizedLine> lines, Action<string, Exception?>? trace = null)
    {
        var texts = new List<string>(lines.Count);
        foreach (RecognizedLine line in lines)
        {
            if (line.Bounds is { } b && b.Height > b.Width * 2)
            {
                trace?.Invoke($"命名：剔除竖排文本行「{line.Text}」（框 {b.Width:F2}x{b.Height:F2}），标签列碎片不参与命名", null);
                continue;
            }
            texts.Add(line.Text);
        }
        return TryBuildName(texts, trace);
    }

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

        if (profile.NameStrategy == NameExtractionStrategy.CompanyLabel)
        {
            // Organisation naming: the value after the name label is a long company name the
            // person-name shapes would truncate — take the whole labelled value instead. A
            // private-account customer is still a 2-4 char person, which the whole-value pass
            // rejects as too short, so the person shapes run as the fallback.
            string? company = NameExtractor.ExtractCompany(ocrLines, profile)
                ?? NameExtractor.Extract(ocrLines, profile);
            if (company is null)
            {
                trace?.Invoke($"命名：企业名提取失败（类型={profile.TypeShortName}），跳过自动命名", null);
                return null;
            }

            var companyResult = AutoNameResult.Create(company, profile.TypeShortName);
            trace?.Invoke($"命名：自动命名成功 → {companyResult.SuggestedStem}", null);
            return companyResult;
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
