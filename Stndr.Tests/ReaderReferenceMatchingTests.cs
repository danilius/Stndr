using Xunit;

namespace Stndr.Tests;

public sealed class ReaderReferenceMatchingTests
{
    [Theory]
    [InlineData("1:2", "1.2")]
    [InlineData("Genesis 1:2", "Genesis 1.2")]
    [InlineData("Berakhot 2a:3", "Berakhot 2a.3")]
    public void Reader_reference_matching_accepts_web_and_unit_reference_formats(
        string rowReference,
        string normalizedTarget)
    {
        Assert.True(MainWindow.IsReaderReferenceMatch(rowReference, normalizedTarget));
    }

    [Fact]
    public void Reader_reference_matching_rejects_a_different_location()
    {
        Assert.False(MainWindow.IsReaderReferenceMatch("Genesis 1:3", "Genesis 1.2"));
    }
}
