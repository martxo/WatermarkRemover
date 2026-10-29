namespace WatermarkRemover.Imaging;

/// <summary>How selected images are packed. Deliberately document-agnostic: no branch here
/// knows about 身份证 / 户口本 / 执照 — the caller picks a pack, any document can use any pack.</summary>
public enum ComposeLayoutKind
{
    /// <summary>One image per page (原样，只是去掉背景).</summary>
    Single,

    /// <summary>Images side by side on one page (身份证正反面).</summary>
    Row,

    /// <summary>Images stacked top to bottom on one page (竖版页/户口本).</summary>
    Column,

    /// <summary>Two columns of cells on one page (多张证件拼一页).</summary>
    Grid,

    /// <summary>A4 sheet with the two ID sides at their physical size, front above, back below.</summary>
    IdCardA4,
}

/// <summary>One packing template. <paramref name="Capacity"/> is how many images share a page;
/// a selection larger than that is split into consecutive pages of the same file.</summary>
public sealed record ComposeLayout(string Id, string Name, ComposeLayoutKind Kind, int Capacity, string Hint);

/// <summary>The built-in packs. Adding a new pack is one data line — no composer change.</summary>
public static class ComposeLayoutCatalog
{
    public static ComposeLayout Single { get; } = new(
        "single", "单张（每张一页）", ComposeLayoutKind.Single, 1,
        "每张图片单独一页，适合执照、票据等整页文件。");

    public static ComposeLayout Row { get; } = new(
        "row", "并排（左右）", ComposeLayoutKind.Row, 2,
        "两张图片左右并排成一张，适合身份证、银行卡正反面。");

    public static ComposeLayout Column { get; } = new(
        "column", "上下排列", ComposeLayoutKind.Column, 2,
        "两张图片上下拼成一张，适合户口本等竖版页面。");

    public static ComposeLayout Grid { get; } = new(
        "grid", "四宫格", ComposeLayoutKind.Grid, 4,
        "最多四张拼在一页，适合多张证件或小票。");

    /// <summary>The 身份证 template: not a pack of arbitrary cells but a fixed A4 sheet whose two
    /// cards are printed at 85.6×54 mm — the size a copy shop expects. Each face is centred in
    /// its own half of the page (upper / lower); the whole sheet is A4 portrait at 300 dpi.</summary>
    public static ComposeLayout IdCardA4 { get; } = new(
        "idcard-a4", "身份证 A4（原件大小）", ComposeLayoutKind.IdCardA4, 2,
        "两张卡分别在上/下半页居中，85.6×54mm 真实大小，输出 A4 单页，可直接打印。");

    public static IReadOnlyList<ComposeLayout> All { get; } =
        new[] { Single, Row, Column, Grid, IdCardA4 };

    /// <summary>
    /// A starting point the user can override. Two images whose shape is close to a bank-card
    /// ratio (1.586), or a share titled 身份证, read as the two sides of an ID card and get the
    /// fixed A4 template; other pairs keep the generic side-by-side pack; everything else keeps
    /// one page per image.
    /// </summary>
    public static ComposeLayout Suggest(IReadOnlyList<(int Width, int Height)> sizes, bool titleSaysIdCard = false)
    {
        if (sizes.Count == 1) return Single;
        if (sizes.Count != 2) return Single;

        bool idShaped = sizes.All(s =>
            s.Width > 0 && s.Height > 0 &&
            Math.Abs(s.Width / (double)s.Height - 1.586) <= 0.12);
        return titleSaysIdCard || idShaped ? IdCardA4 : Row;
    }
}

/// <summary>One crop choice for the picker; <see cref="Name"/> is what the user sees.</summary>
public sealed record CropModeChoice(string Name, CropMode Mode, string Hint);

/// <summary>The crop choices offered next to the pack picker — deliberately generic modes, not
/// per-document presets, so a new document style never needs a code change.</summary>
public static class CropModeCatalog
{
    public static IReadOnlyList<CropModeChoice> All { get; } = new[]
    {
        new CropModeChoice(
            "自动裁边",
            CropMode.Auto,
            "去掉桌面/背景，但白底扫描页保持原样。推荐。"),
        new CropModeChoice(
            "裁到内容",
            CropMode.Content,
            "总是裁到证件/纸张内容边缘，适合证件放在白纸上的照片。"),
        new CropModeChoice(
            "不裁剪",
            CropMode.None,
            "保持图片原样。"),
    };
}
