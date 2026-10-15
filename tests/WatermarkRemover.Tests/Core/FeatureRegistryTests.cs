using WatermarkRemover.Core.Features;

namespace WatermarkRemover.Tests.Core;

public class FeatureRegistryTests
{
    private sealed class StubFeature : IFeature
    {
        public StubFeature(string id, string extension)
        {
            Id = id;
            Extension = extension;
        }

        public string Id { get; }
        public string DisplayName => Id;
        public string Extension { get; }

        public bool CanHandle(string filePath) =>
            string.Equals(Path.GetExtension(filePath), Extension, StringComparison.OrdinalIgnoreCase);

        public FeatureResult Run(string inputPath, FeatureContext context) =>
            FeatureResult.NoChange(inputPath, "stub");
    }

    [Fact]
    public void FindForFile_matches_by_extension_case_insensitively()
    {
        var registry = new FeatureRegistry(new IFeature[] { new StubFeature("pdf.f", ".pdf") });

        Assert.Equal("pdf.f", registry.FindForFile("C:\\doc.PDF")?.Id);
        Assert.Equal("pdf.f", registry.FindForFile("C:\\doc.pdf")?.Id);
    }

    [Fact]
    public void FindForFile_returns_null_for_unknown_types()
    {
        var registry = new FeatureRegistry(new IFeature[] { new StubFeature("pdf.f", ".pdf") });

        Assert.Null(registry.FindForFile("C:\\photo.jpg"));
    }

    [Fact]
    public void First_registered_feature_wins()
    {
        var first = new StubFeature("first", ".pdf");
        var second = new StubFeature("second", ".pdf");
        var registry = new FeatureRegistry(new IFeature[] { first, second });

        Assert.Same(first, registry.FindForFile("C:\\doc.pdf"));
    }

    [Fact]
    public void FindById_returns_the_registered_feature()
    {
        var feature = new StubFeature("pdf.f", ".pdf");
        var registry = new FeatureRegistry(new IFeature[] { feature });

        Assert.Same(feature, registry.FindById("pdf.f"));
        Assert.Null(registry.FindById("nope"));
    }

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new FeatureRegistry(new IFeature[] { new StubFeature("dup", ".a"), new StubFeature("dup", ".b") }));
    }

    [Fact]
    public void Empty_registry_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new FeatureRegistry(Array.Empty<IFeature>()));
    }
}
