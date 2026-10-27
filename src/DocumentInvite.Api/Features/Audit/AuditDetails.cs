using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocumentInvite.Api.Features.Audit;

/// <summary>
/// Typed audit details. The only values it accepts are ids, enum values and counts, so names,
/// emails, file names and field values cannot be recorded. Keys are sorted so the serialised
/// form is canonical.
/// </summary>
public sealed partial class AuditDetails
{
    private readonly SortedDictionary<string, object> _values = new(StringComparer.Ordinal);

    public static AuditDetails Empty => new();

    public AuditDetails WithId(string key, Guid id) => Add(key, id.ToString("D"));

    public AuditDetails WithEnum<TEnum>(string key, TEnum value) where TEnum : struct, Enum =>
        Add(key, value.ToString());

    public AuditDetails WithCount(string key, long count) => Add(key, count);

    public string ToJson() => _values.Count == 0 ? "{}" : JsonSerializer.Serialize(_values);

    private AuditDetails Add(string key, object value)
    {
        if (key is null || !KeyPattern().IsMatch(key))
        {
            throw new ArgumentException(
                "Audit detail keys must be camelCase identifiers of at most 50 characters.", nameof(key));
        }

        if (!_values.TryAdd(key, value))
        {
            throw new ArgumentException($"Audit detail key '{key}' was already added.", nameof(key));
        }

        return this;
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,49}$")]
    private static partial Regex KeyPattern();
}
