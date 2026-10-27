using System.Text.Json.Serialization;

namespace DocumentInvite.Api.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PartyType
{
    Borrower,
    CoBorrower,
    Guarantor,
    Broker,
    Attorney,
    Accountant,
}

/// <summary>A person who can be added to a loan as a party.</summary>
public class Contact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public PartyType PartyType { get; set; }

    /// <summary>True once the personal data has been replaced by a pseudonym (GDPR erasure or retention purge).</summary>
    public bool IsPseudonymised { get; set; }

    public DateTimeOffset? ErasedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
