using System.Globalization;
using System.Text;

namespace DocumentInvite.Api.Features.Files;

/// <summary>Builds canonical file names: <c>{RequestRef}_{PartyLastName}_{ItemName}_{n}.{ext}</c>.</summary>
public static class FileNaming
{
    public const int MaxSegmentLength = 40;
    private const string EmptySegment = "Unknown";

    /// <summary>
    /// Keeps only <c>A-Z a-z 0-9 -</c>. Accents are folded to their base letter, whitespace and
    /// underscores become <c>-</c>, repeated dashes collapse, and the result is capped in length.
    /// </summary>
    public static string Sanitise(string? value, int maxLength = MaxSegmentLength)
    {
        var builder = new StringBuilder();
        foreach (var c in (value ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
            }
            else if (char.IsWhiteSpace(c) || c is '-' or '_')
            {
                if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }
        }

        var result = builder.ToString().Trim('-');
        if (result.Length > maxLength)
        {
            result = result[..maxLength].TrimEnd('-');
        }

        return result.Length == 0 ? EmptySegment : result;
    }

    public static string Canonical(string requestReference, string partyLastName, string itemName, int sequence, string extension)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        return $"{Sanitise(requestReference)}_{Sanitise(partyLastName)}_{Sanitise(itemName)}_{sequence}.{extension.TrimStart('.').ToLowerInvariant()}";
    }

    /// <summary>The next sequence number for an item, given the sequences already used.</summary>
    public static int NextSequence(IEnumerable<int> used) => used.DefaultIfEmpty(0).Max() + 1;
}
