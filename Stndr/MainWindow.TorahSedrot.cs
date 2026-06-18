using System;
using System.Collections.Generic;
using System.Linq;

namespace Stndr;

public partial class MainWindow
{
    private static readonly Lazy<Dictionary<string, List<TorahSedra>>> TorahSedrotByBook = new(ParseTorahSedrot);

    private static IReadOnlyList<TorahSedra> GetTorahSedrot(string bookTitle)
    {
        return TorahSedrotByBook.Value.TryGetValue(bookTitle, out var sedrot)
            ? sedrot
            : Array.Empty<TorahSedra>();
    }

    private static TorahSedra? GetSelectedTorahSedra(ReaderTabState state)
    {
        if (string.IsNullOrWhiteSpace(state.SelectedSedraKey))
        {
            return null;
        }

        return GetTorahSedrot(state.Primary.Title).FirstOrDefault(sedra =>
            string.Equals(sedra.Key, state.SelectedSedraKey, StringComparison.Ordinal));
    }

    private static bool EnsureSelectedTorahSedraForCurrentPosition(ReaderTabState state)
    {
        if (GetSelectedTorahSedra(state) is not null)
        {
            return true;
        }

        var row = state.SelectedReaderRow ??
            state.ReaderRows.FirstOrDefault(candidate =>
                !candidate.IsChapterHeading &&
                string.Equals(candidate.ChapterKey, state.CurrentChapterKey, StringComparison.Ordinal)) ??
            state.ReaderRows.FirstOrDefault(candidate => !candidate.IsChapterHeading);
        var reference = FirstNonEmpty(row?.Primary?.Reference, row?.Translation?.Reference);
        if (FindTorahSedraForReaderReference(state.Primary.Title, reference) is not { } sedra)
        {
            return false;
        }

        state.SelectedSedraKey = sedra.Key;
        state.IsSedraContentOpen = true;
        return true;
    }

    private static TorahSedra? FindTorahSedraForReaderReference(string bookTitle, string reference)
    {
        if (!TryParseReaderReference(reference, out var verse))
        {
            return null;
        }

        return GetTorahSedrot(bookTitle).FirstOrDefault(sedra =>
            TryParseTorahRange(sedra.WholeRef, out var start, out var end) &&
            verse.CompareTo(start) >= 0 &&
            verse.CompareTo(end) <= 0);
    }

    private static TorahAliyah? GetAliyahForReaderReference(TorahSedra sedra, string reference)
    {
        if (!TryParseReaderReference(reference, out var verse))
        {
            return null;
        }

        foreach (var aliyah in sedra.Aliyot)
        {
            if (TryParseTorahRange(aliyah.Ref, out var start, out var end) &&
                verse.CompareTo(start) >= 0 &&
                verse.CompareTo(end) <= 0)
            {
                return aliyah;
            }
        }

        return null;
    }

    private static bool IsAliyahStart(TorahAliyah aliyah, string reference)
    {
        return TryParseReaderReference(reference, out var verse) &&
            TryParseTorahRange(aliyah.Ref, out var start, out _) &&
            verse.CompareTo(start) == 0;
    }

    private static bool TryParseReaderReference(string reference, out TorahVerseLocation verse)
    {
        verse = new TorahVerseLocation(0, 0);
        reference = reference.Trim();
        var lastSpace = reference.LastIndexOf(' ');
        if (lastSpace >= 0 && lastSpace < reference.Length - 1)
        {
            reference = reference[(lastSpace + 1)..];
        }

        var rangeStart = reference.Split('-', 2, StringSplitOptions.TrimEntries)[0];
        reference = rangeStart;
        reference = reference.Replace(':', '.');
        var parts = reference.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], out var chapter) ||
            !int.TryParse(parts[1], out var verseNumber) ||
            chapter <= 0 ||
            verseNumber <= 0)
        {
            return false;
        }

        verse = new TorahVerseLocation(chapter, verseNumber);
        return true;
    }

    private static bool TryParseTorahRange(string reference, out TorahVerseLocation start, out TorahVerseLocation end)
    {
        start = new TorahVerseLocation(0, 0);
        end = new TorahVerseLocation(0, 0);

        var firstSpace = reference.IndexOf(' ');
        if (firstSpace < 0 || firstSpace >= reference.Length - 1)
        {
            return false;
        }

        var range = reference[(firstSpace + 1)..].Trim();
        var rangeParts = range.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!TryParseChapterVerse(rangeParts[0], null, out start))
        {
            return false;
        }

        if (rangeParts.Length == 1)
        {
            end = start;
            return true;
        }

        return TryParseChapterVerse(rangeParts[1], start.Chapter, out end);
    }

    private static bool TryParseChapterVerse(string value, int? defaultChapter, out TorahVerseLocation verse)
    {
        verse = new TorahVerseLocation(0, 0);
        value = value.Replace('.', ':').Trim();
        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var chapter) &&
            int.TryParse(parts[1], out var verseNumber))
        {
            verse = new TorahVerseLocation(chapter, verseNumber);
            return chapter > 0 && verseNumber > 0;
        }

        if (parts.Length == 1 &&
            defaultChapter is not null &&
            int.TryParse(parts[0], out verseNumber))
        {
            verse = new TorahVerseLocation(defaultChapter.Value, verseNumber);
            return verseNumber > 0;
        }

        return false;
    }

    private static bool HasTorahSedrot(InstalledSefariaBook book)
    {
        return GetTorahSedrot(book.Title).Count > 0;
    }

    private static Dictionary<string, List<TorahSedra>> ParseTorahSedrot()
    {
        var result = new Dictionary<string, List<TorahSedra>>(StringComparer.Ordinal);
        foreach (var rawLine in TorahSedraData.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawLine.Split('|');
            if (parts.Length != 6)
            {
                continue;
            }

            var bookTitle = parts[0];
            var englishTitle = parts[1];
            var hebrewTitle = parts[2];
            var wholeRef = parts[3];
            var isCombined = string.Equals(parts[4], "combined", StringComparison.Ordinal);
            var aliyot = parts[5]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((reference, index) => new TorahAliyah(index + 1, reference))
                .ToList();
            var key = $"{bookTitle}|{englishTitle}";
            var sedra = new TorahSedra(key, bookTitle, englishTitle, hebrewTitle, wholeRef, aliyot, isCombined);
            if (!result.TryGetValue(bookTitle, out var sedrot))
            {
                sedrot = new List<TorahSedra>();
                result[bookTitle] = sedrot;
            }

            sedrot.Add(sedra);
        }

        return result;
    }

    private const string TorahSedraData = """
Genesis|Bereshit|בראשית|Genesis 1:1-6:8|single|Genesis 1:1-2:3;Genesis 2:4-2:19;Genesis 2:20-3:21;Genesis 3:22-4:18;Genesis 4:19-4:22;Genesis 4:23-5:24;Genesis 5:25-6:8
Genesis|Noach|נח|Genesis 6:9-11:32|single|Genesis 6:9-6:22;Genesis 7:1-7:16;Genesis 7:17-8:14;Genesis 8:15-9:7;Genesis 9:8-9:17;Genesis 9:18-10:32;Genesis 11:1-11:32
Genesis|Lech Lecha|לך לך|Genesis 12:1-17:27|single|Genesis 12:1-12:13;Genesis 12:14-13:4;Genesis 13:5-13:18;Genesis 14:1-14:20;Genesis 14:21-15:6;Genesis 15:7-17:6;Genesis 17:7-17:27
Genesis|Vayera|וירא|Genesis 18:1-22:24|single|Genesis 18:1-18:14;Genesis 18:15-18:33;Genesis 19:1-19:20;Genesis 19:21-21:4;Genesis 21:5-21:21;Genesis 21:22-21:34;Genesis 22:1-22:24
Genesis|Chayei Sara|חיי שרה|Genesis 23:1-25:18|single|Genesis 23:1-23:16;Genesis 23:17-24:9;Genesis 24:10-24:26;Genesis 24:27-24:52;Genesis 24:53-24:67;Genesis 25:1-25:11;Genesis 25:12-25:18
Genesis|Toldot|תולדות|Genesis 25:19-28:9|single|Genesis 25:19-26:5;Genesis 26:6-26:12;Genesis 26:13-26:22;Genesis 26:23-26:29;Genesis 26:30-27:27;Genesis 27:28-28:4;Genesis 28:5-28:9
Genesis|Vayetzei|ויצא|Genesis 28:10-32:3|single|Genesis 28:10-28:22;Genesis 29:1-29:17;Genesis 29:18-30:13;Genesis 30:14-30:27;Genesis 30:28-31:16;Genesis 31:17-31:42;Genesis 31:43-32:3
Genesis|Vayishlach|וישלח|Genesis 32:4-36:43|single|Genesis 32:4-32:13;Genesis 32:14-32:30;Genesis 32:31-33:5;Genesis 33:6-33:20;Genesis 34:1-35:11;Genesis 35:12-36:19;Genesis 36:20-36:43
Genesis|Vayeshev|וישב|Genesis 37:1-40:23|single|Genesis 37:1-37:11;Genesis 37:12-37:22;Genesis 37:23-37:36;Genesis 38:1-38:30;Genesis 39:1-39:6;Genesis 39:7-39:23;Genesis 40:1-40:23
Genesis|Miketz|מקץ|Genesis 41:1-44:17|single|Genesis 41:1-41:14;Genesis 41:15-41:38;Genesis 41:39-41:52;Genesis 41:53-42:18;Genesis 42:19-43:15;Genesis 43:16-43:29;Genesis 43:30-44:17
Genesis|Vayigash|ויגש|Genesis 44:18-47:27|single|Genesis 44:18-44:30;Genesis 44:31-45:7;Genesis 45:8-45:18;Genesis 45:19-45:27;Genesis 45:28-46:27;Genesis 46:28-47:10;Genesis 47:11-47:27
Genesis|Vayechi|ויחי|Genesis 47:28-50:26|single|Genesis 47:28-48:9;Genesis 48:10-48:16;Genesis 48:17-48:22;Genesis 49:1-49:18;Genesis 49:19-49:26;Genesis 49:27-50:20;Genesis 50:21-50:26
Exodus|Shemot|שמות|Exodus 1:1-6:1|single|Exodus 1:1-1:17;Exodus 1:18-2:10;Exodus 2:11-2:25;Exodus 3:1-3:15;Exodus 3:16-4:17;Exodus 4:18-4:31;Exodus 5:1-6:1
Exodus|Vaera|וארא|Exodus 6:2-9:35|single|Exodus 6:2-6:13;Exodus 6:14-6:28;Exodus 6:29-7:7;Exodus 7:8-8:6;Exodus 8:7-8:18;Exodus 8:19-9:16;Exodus 9:17-9:35
Exodus|Bo|בא|Exodus 10:1-13:16|single|Exodus 10:1-10:11;Exodus 10:12-10:23;Exodus 10:24-11:3;Exodus 11:4-12:20;Exodus 12:21-12:28;Exodus 12:29-12:51;Exodus 13:1-13:16
Exodus|Beshalach|בשלח|Exodus 13:17-17:16|single|Exodus 13:17-14:8;Exodus 14:9-14:14;Exodus 14:15-14:25;Exodus 14:26-15:26;Exodus 15:27-16:10;Exodus 16:11-16:36;Exodus 17:1-17:16
Exodus|Yitro|יתרו|Exodus 18:1-20:23|single|Exodus 18:1-18:12;Exodus 18:13-18:23;Exodus 18:24-18:27;Exodus 19:1-19:6;Exodus 19:7-19:19;Exodus 19:20-20:14;Exodus 20:15-20:23
Exodus|Mishpatim|משפטים|Exodus 21:1-24:18|single|Exodus 21:1-21:19;Exodus 21:20-22:3;Exodus 22:4-22:26;Exodus 22:27-23:5;Exodus 23:6-23:19;Exodus 23:20-23:25;Exodus 23:26-24:18
Exodus|Terumah|תרומה|Exodus 25:1-27:19|single|Exodus 25:1-25:16;Exodus 25:17-25:30;Exodus 25:31-26:14;Exodus 26:15-26:30;Exodus 26:31-26:37;Exodus 27:1-27:8;Exodus 27:9-27:19
Exodus|Tetzaveh|תצוה|Exodus 27:20-30:10|single|Exodus 27:20-28:12;Exodus 28:13-28:30;Exodus 28:31-28:43;Exodus 29:1-29:18;Exodus 29:19-29:37;Exodus 29:38-29:46;Exodus 30:1-30:10
Exodus|Ki Tisa|כי תשא|Exodus 30:11-34:35|single|Exodus 30:11-31:17;Exodus 31:18-33:11;Exodus 33:12-33:16;Exodus 33:17-33:23;Exodus 34:1-34:9;Exodus 34:10-34:26;Exodus 34:27-34:35
Exodus|Vayakhel|ויקהל|Exodus 35:1-38:20|single|Exodus 35:1-35:20;Exodus 35:21-35:29;Exodus 35:30-36:7;Exodus 36:8-36:19;Exodus 36:20-37:16;Exodus 37:17-37:29;Exodus 38:1-38:20
Exodus|Pekudei|פקודי|Exodus 38:21-40:38|single|Exodus 38:21-39:1;Exodus 39:2-39:21;Exodus 39:22-39:32;Exodus 39:33-39:43;Exodus 40:1-40:16;Exodus 40:17-40:27;Exodus 40:28-40:38
Exodus|Vayakhel-Pekudei|ויקהל־פקודי|Exodus 35:1-40:38|combined|Exodus 35:1-35:29;Exodus 35:30-37:16;Exodus 37:17-37:29;Exodus 38:1-39:1;Exodus 39:2-39:21;Exodus 39:22-39:43;Exodus 40:1-40:38
Leviticus|Vayikra|ויקרא|Leviticus 1:1-5:26|single|Leviticus 1:1-1:13;Leviticus 1:14-2:6;Leviticus 2:7-2:16;Leviticus 3:1-3:17;Leviticus 4:1-4:26;Leviticus 4:27-5:10;Leviticus 5:11-5:26
Leviticus|Tzav|צו|Leviticus 6:1-8:36|single|Leviticus 6:1-6:11;Leviticus 6:12-7:10;Leviticus 7:11-7:38;Leviticus 8:1-8:13;Leviticus 8:14-8:21;Leviticus 8:22-8:29;Leviticus 8:30-8:36
Leviticus|Shmini|שמיני|Leviticus 9:1-11:47|single|Leviticus 9:1-9:16;Leviticus 9:17-9:23;Leviticus 9:24-10:11;Leviticus 10:12-10:15;Leviticus 10:16-10:20;Leviticus 11:1-11:32;Leviticus 11:33-11:47
Leviticus|Tazria|תזריע|Leviticus 12:1-13:59|single|Leviticus 12:1-13:5;Leviticus 13:6-13:17;Leviticus 13:18-13:23;Leviticus 13:24-13:28;Leviticus 13:29-13:39;Leviticus 13:40-13:54;Leviticus 13:55-13:59
Leviticus|Metzora|מצורע|Leviticus 14:1-15:33|single|Leviticus 14:1-14:12;Leviticus 14:13-14:20;Leviticus 14:21-14:32;Leviticus 14:33-14:53;Leviticus 14:54-15:15;Leviticus 15:16-15:28;Leviticus 15:29-15:33
Leviticus|Tazria-Metzora|תזריע־מצורע|Leviticus 12:1-15:33|combined|Leviticus 12:1-13:23;Leviticus 13:24-13:39;Leviticus 13:40-13:54;Leviticus 13:55-14:20;Leviticus 14:21-14:32;Leviticus 14:33-15:15;Leviticus 15:16-15:33
Leviticus|Achrei Mot|אחרי מות|Leviticus 16:1-18:30|single|Leviticus 16:1-16:17;Leviticus 16:18-16:24;Leviticus 16:25-16:34;Leviticus 17:1-17:7;Leviticus 17:8-18:5;Leviticus 18:6-18:21;Leviticus 18:22-18:30
Leviticus|Kedoshim|קדושים|Leviticus 19:1-20:27|single|Leviticus 19:1-19:14;Leviticus 19:15-19:22;Leviticus 19:23-19:32;Leviticus 19:33-19:37;Leviticus 20:1-20:7;Leviticus 20:8-20:22;Leviticus 20:23-20:27
Leviticus|Achrei Mot-Kedoshim|אחרי מות־קדושים|Leviticus 16:1-20:27|combined|Leviticus 16:1-16:24;Leviticus 16:25-17:7;Leviticus 17:8-18:21;Leviticus 18:22-19:14;Leviticus 19:15-19:32;Leviticus 19:33-20:7;Leviticus 20:8-20:27
Leviticus|Emor|אמור|Leviticus 21:1-24:23|single|Leviticus 21:1-21:15;Leviticus 21:16-22:16;Leviticus 22:17-22:33;Leviticus 23:1-23:22;Leviticus 23:23-23:32;Leviticus 23:33-23:44;Leviticus 24:1-24:23
Leviticus|Behar|בהר|Leviticus 25:1-26:2|single|Leviticus 25:1-25:13;Leviticus 25:14-25:18;Leviticus 25:19-25:24;Leviticus 25:25-25:28;Leviticus 25:29-25:38;Leviticus 25:39-25:46;Leviticus 25:47-26:2
Leviticus|Bechukotai|בחוקתי|Leviticus 26:3-27:34|single|Leviticus 26:3-26:5;Leviticus 26:6-26:9;Leviticus 26:10-26:46;Leviticus 27:1-27:15;Leviticus 27:16-27:21;Leviticus 27:22-27:28;Leviticus 27:29-27:34
Leviticus|Behar-Bechukotai|בהר־בחוקתי|Leviticus 25:1-27:34|combined|Leviticus 25:1-25:18;Leviticus 25:19-25:28;Leviticus 25:29-25:38;Leviticus 25:39-26:9;Leviticus 26:10-26:46;Leviticus 27:1-27:15;Leviticus 27:16-27:34
Numbers|Bamidbar|במדבר|Numbers 1:1-4:20|single|Numbers 1:1-1:19;Numbers 1:20-1:54;Numbers 2:1-2:34;Numbers 3:1-3:13;Numbers 3:14-3:39;Numbers 3:40-3:51;Numbers 4:1-4:20
Numbers|Nasso|נשא|Numbers 4:21-7:89|single|Numbers 4:21-4:37;Numbers 4:38-4:49;Numbers 5:1-5:10;Numbers 5:11-6:27;Numbers 7:1-7:41;Numbers 7:42-7:71;Numbers 7:72-7:89
Numbers|Beha'alotcha|בהעלותך|Numbers 8:1-12:16|single|Numbers 8:1-8:14;Numbers 8:15-8:26;Numbers 9:1-9:14;Numbers 9:15-10:10;Numbers 10:11-10:34;Numbers 10:35-11:29;Numbers 11:30-12:16
Numbers|Sh'lach|שלח|Numbers 13:1-15:41|single|Numbers 13:1-13:20;Numbers 13:21-14:7;Numbers 14:8-14:25;Numbers 14:26-15:7;Numbers 15:8-15:16;Numbers 15:17-15:26;Numbers 15:27-15:41
Numbers|Korach|קרח|Numbers 16:1-18:32|single|Numbers 16:1-16:13;Numbers 16:14-16:19;Numbers 16:20-17:8;Numbers 17:9-17:15;Numbers 17:16-17:24;Numbers 17:25-18:20;Numbers 18:21-18:32
Numbers|Chukat|חקת|Numbers 19:1-22:1|single|Numbers 19:1-19:17;Numbers 19:18-20:6;Numbers 20:7-20:13;Numbers 20:14-20:21;Numbers 20:22-21:9;Numbers 21:10-21:20;Numbers 21:21-22:1
Numbers|Balak|בלק|Numbers 22:2-25:9|single|Numbers 22:2-22:12;Numbers 22:13-22:20;Numbers 22:21-22:38;Numbers 22:39-23:12;Numbers 23:13-23:26;Numbers 23:27-24:13;Numbers 24:14-25:9
Numbers|Chukat-Balak|חקת־בלק|Numbers 19:1-25:9|combined|Numbers 19:1-20:6;Numbers 20:7-20:21;Numbers 20:22-21:20;Numbers 21:21-22:12;Numbers 22:13-22:38;Numbers 22:39-23:26;Numbers 23:27-25:9
Numbers|Pinchas|פנחס|Numbers 25:10-30:1|single|Numbers 25:10-26:4;Numbers 26:5-26:51;Numbers 26:52-27:5;Numbers 27:6-27:23;Numbers 28:1-28:15;Numbers 28:16-29:11;Numbers 29:12-30:1
Numbers|Matot|מטות|Numbers 30:2-32:42|single|Numbers 30:2-30:17;Numbers 31:1-31:12;Numbers 31:13-31:24;Numbers 31:25-31:41;Numbers 31:42-31:54;Numbers 32:1-32:19;Numbers 32:20-32:42
Numbers|Masei|מסעי|Numbers 33:1-36:13|single|Numbers 33:1-33:10;Numbers 33:11-33:49;Numbers 33:50-34:15;Numbers 34:16-34:29;Numbers 35:1-35:8;Numbers 35:9-35:34;Numbers 36:1-36:13
Numbers|Matot-Masei|מטות־מסעי|Numbers 30:2-36:13|combined|Numbers 30:2-31:12;Numbers 31:13-31:54;Numbers 32:1-32:19;Numbers 32:20-33:49;Numbers 33:50-34:15;Numbers 34:16-35:8;Numbers 35:9-36:13
Deuteronomy|Devarim|דברים|Deuteronomy 1:1-3:22|single|Deuteronomy 1:1-1:10;Deuteronomy 1:11-1:21;Deuteronomy 1:22-1:38;Deuteronomy 1:39-2:1;Deuteronomy 2:2-2:30;Deuteronomy 2:31-3:14;Deuteronomy 3:15-3:22
Deuteronomy|Vaetchanan|ואתחנן|Deuteronomy 3:23-7:11|single|Deuteronomy 3:23-4:4;Deuteronomy 4:5-4:40;Deuteronomy 4:41-4:49;Deuteronomy 5:1-5:18;Deuteronomy 5:19-6:3;Deuteronomy 6:4-6:25;Deuteronomy 7:1-7:11
Deuteronomy|Eikev|עקב|Deuteronomy 7:12-11:25|single|Deuteronomy 7:12-8:10;Deuteronomy 8:11-9:3;Deuteronomy 9:4-9:29;Deuteronomy 10:1-10:11;Deuteronomy 10:12-11:9;Deuteronomy 11:10-11:21;Deuteronomy 11:22-11:25
Deuteronomy|Re'eh|ראה|Deuteronomy 11:26-16:17|single|Deuteronomy 11:26-12:10;Deuteronomy 12:11-12:28;Deuteronomy 12:29-13:19;Deuteronomy 14:1-14:21;Deuteronomy 14:22-14:29;Deuteronomy 15:1-15:18;Deuteronomy 15:19-16:17
Deuteronomy|Shoftim|שופטים|Deuteronomy 16:18-21:9|single|Deuteronomy 16:18-17:13;Deuteronomy 17:14-17:20;Deuteronomy 18:1-18:5;Deuteronomy 18:6-18:13;Deuteronomy 18:14-19:13;Deuteronomy 19:14-20:9;Deuteronomy 20:10-21:9
Deuteronomy|Ki Teitzei|כי תצא|Deuteronomy 21:10-25:19|single|Deuteronomy 21:10-21:21;Deuteronomy 21:22-22:7;Deuteronomy 22:8-23:7;Deuteronomy 23:8-23:24;Deuteronomy 23:25-24:4;Deuteronomy 24:5-24:13;Deuteronomy 24:14-25:19
Deuteronomy|Ki Tavo|כי תבוא|Deuteronomy 26:1-29:8|single|Deuteronomy 26:1-26:11;Deuteronomy 26:12-26:15;Deuteronomy 26:16-26:19;Deuteronomy 27:1-27:10;Deuteronomy 27:11-28:6;Deuteronomy 28:7-28:69;Deuteronomy 29:1-29:8
Deuteronomy|Nitzavim|נצבים|Deuteronomy 29:9-30:20|single|Deuteronomy 29:9-29:11;Deuteronomy 29:12-29:14;Deuteronomy 29:15-29:28;Deuteronomy 30:1-30:6;Deuteronomy 30:7-30:10;Deuteronomy 30:11-30:14;Deuteronomy 30:15-30:20
Deuteronomy|Vayeilech|וילך|Deuteronomy 31:1-30|single|Deuteronomy 31:1-31:3;Deuteronomy 31:4-31:6;Deuteronomy 31:7-31:9;Deuteronomy 31:10-31:13;Deuteronomy 31:14-31:19;Deuteronomy 31:20-31:24;Deuteronomy 31:25-31:30
Deuteronomy|Nitzavim-Vayeilech|נצבים־וילך|Deuteronomy 29:9-31:30|combined|Deuteronomy 29:9-29:28;Deuteronomy 30:1-30:6;Deuteronomy 30:7-30:14;Deuteronomy 30:15-31:6;Deuteronomy 31:7-31:13;Deuteronomy 31:14-31:19;Deuteronomy 31:20-31:30
Deuteronomy|Ha'Azinu|האזינו|Deuteronomy 32:1-52|single|Deuteronomy 32:1-32:6;Deuteronomy 32:7-32:12;Deuteronomy 32:13-32:18;Deuteronomy 32:19-32:28;Deuteronomy 32:29-32:39;Deuteronomy 32:40-32:43;Deuteronomy 32:44-32:52
Deuteronomy|V'Zot HaBerachah|וזאת הברכה|Deuteronomy 33:1-34:12|single|Deuteronomy 33:1-33:7;Deuteronomy 33:8-33:12;Deuteronomy 33:13-33:17;Deuteronomy 33:18-33:21;Deuteronomy 33:22-33:26;Deuteronomy 33:27-33:29;Deuteronomy 34:1-34:12
""";
}
