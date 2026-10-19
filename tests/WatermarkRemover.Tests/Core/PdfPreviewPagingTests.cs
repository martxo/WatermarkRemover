using WatermarkRemover.Core.Features.Preview;

namespace WatermarkRemover.Tests.Core;

public class PdfPreviewPagingTests
{
    [Theory]
    [InlineData(0, 3, 0)]
    [InlineData(2, 3, 2)]
    [InlineData(-1, 3, 0)]
    [InlineData(99, 3, 2)]
    [InlineData(5, 0, 0)]
    public void Clamp_keeps_the_index_inside_the_document(int pageIndex, int pageCount, int expected)
    {
        Assert.Equal(expected, PdfPreviewPaging.Clamp(pageIndex, pageCount));
    }

    [Fact]
    public void Previous_is_unavailable_on_the_first_page()
    {
        Assert.False(PdfPreviewPaging.CanGoPrevious(0));
        Assert.True(PdfPreviewPaging.CanGoPrevious(1));
    }

    [Fact]
    public void Next_is_unavailable_on_the_last_page()
    {
        Assert.True(PdfPreviewPaging.CanGoNext(0, 3));
        Assert.True(PdfPreviewPaging.CanGoNext(1, 3));
        Assert.False(PdfPreviewPaging.CanGoNext(2, 3));
    }

    [Fact]
    public void An_empty_document_allows_no_paging_at_all()
    {
        Assert.False(PdfPreviewPaging.CanGoNext(0, 0));
        Assert.False(PdfPreviewPaging.CanGoPrevious(0));
    }

    [Fact]
    public void Indicator_is_one_based_and_tolerates_an_out_of_range_index()
    {
        Assert.Equal("第 3 / 12 页", PdfPreviewPaging.FormatIndicator(2, 12));
        Assert.Equal("第 5 / 5 页", PdfPreviewPaging.FormatIndicator(99, 5));
        Assert.Equal("无页面", PdfPreviewPaging.FormatIndicator(0, 0));
    }
}
