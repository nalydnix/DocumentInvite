using System.Text.Json.Serialization;

namespace DocumentInvite.Api.Domain;

/// <summary>The four collection states shared by invites and loans.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CollectionStatus
{
    Sent,
    InProgress,
    Submitted,
    Completed,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequestItemStatus
{
    Outstanding,
    Submitted,
    Approved,
    Rejected,
}

/// <summary>A loan for which documents are collected from one or more parties.</summary>
public class Loan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Unique, human-readable reference. Used in exported file names.</summary>
    public string Reference { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string LoanType { get; set; } = string.Empty;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Stamped when the loan becomes Completed. Starts the retention period.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Derived from the loan's invites by <c>StatusCalculator</c> and stored for filtering.</summary>
    public CollectionStatus Status { get; set; } = CollectionStatus.Sent;

    public byte[] RowVersion { get; set; } = [];
    public List<Invite> Invites { get; set; } = [];
}

/// <summary>One party's request on one loan, opened through a secure link.</summary>
public class Invite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LoanId { get; set; }
    public Guid ContactId { get; set; }
    public DateOnly DueDate { get; set; }
    public int ReminderIntervalDays { get; set; }
    public DateTimeOffset? LastReminderAt { get; set; }
    public DateTimeOffset? OverdueNoticeSentAt { get; set; }

    /// <summary>SHA-256 of the portal token. The token itself is never stored.</summary>
    public byte[] TokenHash { get; set; } = [];

    public DateTimeOffset TokenExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? PasswordHash { get; set; }
    public DateTimeOffset? PrivacyAcceptedAt { get; set; }

    /// <summary>Set the first time the party opens the portal.</summary>
    public DateTimeOffset? PortalOpenedAt { get; set; }

    public DateTimeOffset SentAt { get; set; }
    public CollectionStatus Status { get; set; } = CollectionStatus.Sent;
    public byte[] RowVersion { get; set; } = [];
    public List<RequestItem> Items { get; set; } = [];
}

/// <summary>An item requested from a party. Copied from a template item, then editable.</summary>
public class RequestItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InviteId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ItemKind Kind { get; set; }
    public string Instructions { get; set; } = string.Empty;
    public bool AllowMultiple { get; set; }
    public FieldType? FieldType { get; set; }
    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }
    public RequestItemStatus Status { get; set; } = RequestItemStatus.Outstanding;
    public string? RejectionReason { get; set; }
    public string? FieldValue { get; set; }
    public string? PartyNote { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
