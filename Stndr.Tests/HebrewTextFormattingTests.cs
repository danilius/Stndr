using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Stndr.Tests;

public sealed class HebrewTextFormattingTests
{
    [Theory]
    [InlineData("\u05DE\u05BD", "\u05DE")]
    [InlineData("\u05D5\u05BD\u05D9\u05D4\u05D9\u05BE\u05E2\u05E8\u05D1", "\u05D5\u05D9\u05D4\u05D9 \u05E2\u05E8\u05D1")]
    [InlineData("\u05D0\u05C0\u2009\u05D1", "\u05D0 \u05D1")]
    public void Text_only_removes_meteg_paseq_and_maqaf(string source, string expected)
    {
        Assert.Equal(expected, HebrewTextFormatting.ApplyMarksMode(source, HebrewMarksMode.TextOnly));
    }

    [Fact]
    public void Nikkud_mode_keeps_vowels_and_maqaf_but_removes_meteg()
    {
        Assert.Equal(
            "\u05DE\u05B4 \u05D5\u05D9\u05D4\u05D9\u05BE\u05E2\u05E8\u05D1",
            HebrewTextFormatting.ApplyMarksMode(
                "\u05DE\u05B4\u05BD \u05D5\u05BD\u05D9\u05D4\u05D9\u05BE\u05E2\u05E8\u05D1",
                HebrewMarksMode.Nikkud));
    }

    [Fact]
    public void Full_marks_mode_preserves_the_source()
    {
        const string source = "\u05D5\u05BD\u05D9\u05D4\u05D9\u05BE\u05E2\u05E8\u05D1 \u05D0\u05C0\u2009\u05D1";
        Assert.Equal(
            source,
            HebrewTextFormatting.ApplyMarksMode(source, HebrewMarksMode.NikkudAndCantillation));
    }

    [Theory]
    [InlineData(
        "Genesis",
        "1.27",
        "\u05D0\u05DC\u05D4\u05D9\u05DD\u05D0\u05EA",
        "\u05D0\u05DC\u05D4\u05D9\u05DD \u05D0\u05EA")]
    [InlineData(
        "Joshua",
        "1.14",
        "\u05D4\u05D9\u05E8\u05D3\u05DF\u05D5\u05D0\u05EA\u05DD",
        "\u05D4\u05D9\u05E8\u05D3\u05DF \u05D5\u05D0\u05EA\u05DD")]
    public void Installed_text_only_preserves_word_boundaries(
        string title,
        string reference,
        string gluedWords,
        string separatedWords)
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("STNDR_DATA"),
            @"F:\Stndr data",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Stndr")
        };
        var dataFolder = candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            SefariaOfflineLibraryInstaller.IsInstalled(candidate));
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var book = library.GetInstalledVersionsForTitle(title)
            .Where(SefariaLibraryService.IsHebrew)
            .OrderByDescending(version => version.SegmentCount)
            .First();
        var unit = library.ReadInstalledBookUnits(book)
            .Single(candidate => candidate.Reference == reference);
        var sanitized = MainWindow.SanitizeReaderHtmlForWeb(
            unit.Text,
            true,
            HebrewMarksMode.TextOnly);

        Assert.DoesNotContain(gluedWords, sanitized);
        Assert.Contains(separatedWords, sanitized);
        Assert.DoesNotContain('\u05BD', sanitized);
        Assert.DoesNotContain('\u05BE', sanitized);
        Assert.DoesNotContain('\u05C0', sanitized);
        Assert.DoesNotContain('\u2009', sanitized);
    }
}
