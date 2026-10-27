using System.Text.RegularExpressions;

namespace DocumentInvite.Api.Common.Logging;

/// <summary>Pure masking rules shared by the Serilog enricher and the request logger.</summary>
public static partial class PiiMasker
{
    public const string Redacted = "[redacted]";

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "firstname", "lastname", "fullname", "displayname", "contactname", "partyname", "username",
        "email", "emailaddress", "phone", "phonenumber", "token", "portaltoken", "password", "authorization",
        "secret", "apikey",
    };

    // 32 random bytes encoded as base64url are 43 characters long.
    [GeneratedRegex("(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\w-])\+?\d[\d\s().\-]{7,}\d(?![\w-])")]
    private static partial Regex PhonePattern();

    public static bool IsSensitiveName(string propertyName) => SensitiveNames.Contains(propertyName);

    /// <summary>Masks emails, phone numbers and portal tokens embedded in free text.</summary>
    public static string MaskText(string value)
    {
        if (value.Length == 0) return value;
        var masked = EmailPattern().Replace(value, "[email]");
        masked = TokenPattern().Replace(masked, "[token]");
        masked = PhonePattern().Replace(masked, m => m.Value.Count(char.IsAsciiDigit) >= 10 ? "[phone]" : m.Value);
        return masked;
    }

    /// <summary>Masks a request path, always hiding the segment after <c>/p/</c> (the portal token).</summary>
    public static string MaskPath(string path)
    {
        var segments = path.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i - 1].Equals("p", StringComparison.OrdinalIgnoreCase) && segments[i].Length > 0)
            {
                segments[i] = "[token]";
            }
        }

        return MaskText(string.Join('/', segments));
    }
}
