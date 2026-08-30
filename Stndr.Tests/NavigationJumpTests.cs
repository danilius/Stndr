using Xunit;

namespace Stndr.Tests;

public sealed class NavigationJumpTests
{
    [Theory]
    [InlineData("46b3", "46b", 3)]
    [InlineData("46b.3", "46b", 3)]
    [InlineData("46b:3", "46b", 3)]
    [InlineData("46B:3", "46b", 3)]
    [InlineData("46b", "46b", null)]
    [InlineData("מו:ג", "46b", 3)]
    [InlineData("מו.ג", "46a", 3)]
    [InlineData("מ״ו:ג", "46b", 3)]
    [InlineData("מו:", "46b", null)]
    public void Parses_talmud_page_and_optional_section(
        string query,
        string expectedPage,
        int? expectedSection)
    {
        var parsed = MainWindow.TryParseTalmudNavigationJump(query, out var page, out var section);

        Assert.True(parsed);
        Assert.Equal(expectedPage, page);
        Assert.Equal(expectedSection, section);
    }

    [Theory]
    [InlineData("")]
    [InlineData("46")]
    [InlineData("46c3")]
    [InlineData("46b:")]
    [InlineData("מו")]
    [InlineData("מו:c")]
    public void Rejects_invalid_talmud_navigation_queries(string query)
    {
        Assert.False(MainWindow.TryParseTalmudNavigationJump(query, out _, out _));
    }
}
