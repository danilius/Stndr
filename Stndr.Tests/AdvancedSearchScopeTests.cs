using System.Collections.Generic;
using Xunit;

namespace Stndr.Tests;

public sealed class AdvancedSearchScopeTests
{
    [Fact]
    public void BavliSederScopeOnlyMatchesBaseTractatesInThatBavliSeder()
    {
        const string scope = "Talmud/Bavli/Seder Nezikin";

        Assert.True(MainWindow.MatchesAdvancedSearchCategoryScope(
            Book("Bava Kamma", "Talmud", "Bavli", "Seder Nezikin"),
            scope));
        Assert.False(MainWindow.MatchesAdvancedSearchCategoryScope(
            Book("Jerusalem Talmud Bava Kamma", "Talmud", "Yerushalmi", "Seder Nezikin"),
            scope));
        Assert.False(MainWindow.MatchesAdvancedSearchCategoryScope(
            Book("Rif Bava Kamma", "Talmud", "Bavli", "Seder Nezikin", "Rif"),
            scope));
        Assert.False(MainWindow.MatchesAdvancedSearchCategoryScope(
            Book("Berakhot", "Talmud", "Bavli", "Seder Zeraim"),
            scope));
    }

    private static InstalledSefariaBook Book(string title, params string[] categories)
    {
        return new InstalledSefariaBook
        {
            Title = title,
            Categories = new List<string>(categories)
        };
    }
}
