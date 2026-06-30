using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Stndr;

internal static class SearchTextMatcher
{
    public static bool Matches(string? query, params string?[] values)
    {
        var normalizedQuery = Normalize(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return false;
        }

        var normalizedQueryNoVowels = RemoveVowels(normalizedQuery);

        foreach (var candidate in BuildCandidateTerms(values))
        {
            var normalizedCandidate = Normalize(candidate);
            if (string.IsNullOrWhiteSpace(normalizedCandidate))
            {
                continue;
            }

            if (normalizedCandidate.Contains(normalizedQuery, StringComparison.Ordinal))
            {
                return true;
            }

            if (RemoveVowels(normalizedCandidate).Contains(normalizedQueryNoVowels, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<string> BuildCandidateTerms(params string?[] values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (seen.Add(value))
            {
                yield return value;
            }

            var transliterated = TransliterateHebrewToLatinApprox(value);
            if (!string.IsNullOrWhiteSpace(transliterated) && seen.Add(transliterated))
            {
                yield return transliterated;
            }

            foreach (var alias in GetKnownTransliterationAliases(value))
            {
                if (seen.Add(alias))
                {
                    yield return alias;
                }
            }
        }
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string RemoveVowels(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if ("aeiou".IndexOf(character) >= 0)
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string TransliterateHebrewToLatinApprox(string value)
    {
        if (!ContainsHebrewLetters(value))
        {
            return value;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length * 2);

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            switch (character)
            {
                case 'א':
                case 'ע':
                    break;
                case 'ב':
                    builder.Append('b');
                    break;
                case 'ג':
                    builder.Append('g');
                    break;
                case 'ד':
                    builder.Append('d');
                    break;
                case 'ה':
                    builder.Append('h');
                    break;
                case 'ו':
                    builder.Append('o');
                    break;
                case 'ז':
                    builder.Append('z');
                    break;
                case 'ח':
                    builder.Append("ch");
                    break;
                case 'ט':
                    builder.Append('t');
                    break;
                case 'י':
                    builder.Append('i');
                    break;
                case 'כ':
                case 'ך':
                    builder.Append("kh");
                    break;
                case 'ל':
                    builder.Append('l');
                    break;
                case 'מ':
                case 'ם':
                    builder.Append('m');
                    break;
                case 'נ':
                case 'ן':
                    builder.Append('n');
                    break;
                case 'ס':
                    builder.Append('s');
                    break;
                case 'פ':
                case 'ף':
                    builder.Append('p');
                    break;
                case 'צ':
                case 'ץ':
                    builder.Append("tz");
                    break;
                case 'ק':
                    builder.Append('k');
                    break;
                case 'ר':
                    builder.Append('r');
                    break;
                case 'ש':
                    builder.Append("sh");
                    break;
                case 'ת':
                    builder.Append('t');
                    break;
                default:
                    if (char.IsLetterOrDigit(character))
                    {
                        builder.Append(char.ToLowerInvariant(character));
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> GetKnownTransliterationAliases(string value)
    {
        var normalized = Normalize(value);
        return normalized switch
        {
            "בראשית" => new[] { "bereishit", "bereshit", "genesis" },
            "שמות" => new[] { "shemot", "exodus" },
            "ויקרא" => new[] { "vayikra", "vaikra", "leviticus" },
            "במדבר" => new[] { "bamidbar", "numbers" },
            "דברים" => new[] { "devarim", "deuteronomy" },
            "תורה" => new[] { "torah", "pentateuch" },
            "תנך" => new[] { "tanakh", "tanach", "bible", "hebrew bible" },
            "נביאים" => new[] { "neviim", "prophets" },
            "כתובים" => new[] { "ketuvim", "writings" },
            "משנה" => new[] { "mishnah", "mishna" },
            "תלמוד" => new[] { "talmud" },
            "בבלי" => new[] { "bavli" },
            "ירושלמי" => new[] { "yerushalmi" },
            "מישנהתורה" => new[] { "mishneh torah", "mishne torah" },
            "שולחןערוך" => new[] { "shulchan aruch", "shulchan arukh" },
            "שירהשירים" => new[] { "shir hashirim", "song of songs" },
            "קהלת" => new[] { "kohelet", "ecclesiastes" },
            "איכה" => new[] { "eicha", "eikhah", "lamentations" },
            "אסתר" => new[] { "esther" },
            "דניאל" => new[] { "daniel" },
            "שמואל" => new[] { "shmuel", "samuel" },
            "מלכים" => new[] { "melakhim", "melachim", "kings" },
            "דבריהימים" => new[] { "divrei hayamim", "chronicles" },
            _ => Array.Empty<string>()
        };
    }

    private static bool ContainsHebrewLetters(string value)
    {
        foreach (var character in value)
        {
            if (character >= '\u0590' && character <= '\u05FF')
            {
                return true;
            }
        }

        return false;
    }
}
