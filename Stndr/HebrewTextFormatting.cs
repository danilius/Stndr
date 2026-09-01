using System.Text;

namespace Stndr;

internal static class HebrewTextFormatting
{
    internal static string ApplyMarksMode(string text, HebrewMarksMode mode)
    {
        if (mode == HebrewMarksMode.NikkudAndCantillation || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (mode == HebrewMarksMode.TextOnly && IsTextOnlySeparator(character))
            {
                if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                {
                    builder.Append(' ');
                }

                continue;
            }

            if (ShouldSuppress(character, mode))
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    internal static bool ShouldSuppress(char character, HebrewMarksMode mode)
    {
        var code = (int)character;
        var isCantillation =
            code is >= 0x0591 and <= 0x05AF or
            0x05BD or
            0x05C4 or
            0x05C5;
        var isNikkud =
            code is >= 0x05B0 and <= 0x05BC or
            0x05BF or
            0x05C1 or
            0x05C2 or
            0x05C7;

        return mode switch
        {
            HebrewMarksMode.TextOnly => isCantillation || isNikkud || IsTextOnlySeparator(character),
            HebrewMarksMode.Nikkud => isCantillation,
            _ => false
        };
    }

    private static bool IsTextOnlySeparator(char character) =>
        character is '\u05BE' or '\u05C0' or '\u2009';
}
