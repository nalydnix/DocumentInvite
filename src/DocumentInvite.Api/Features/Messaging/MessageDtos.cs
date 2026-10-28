using System.Text.Json.Serialization;
using DocumentInvite.Api.Domain;
using FluentValidation;

namespace DocumentInvite.Api.Features.Messaging;

public static class MessageLimits
{
    /// <summary>Matches the <c>Messages.Body</c> column.</summary>
    public const int MaxBodyLength = 4000;
}

/// <summary>What kind of entry was posted. Recorded in audit details instead of any text.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageKind
{
    Team,
    Party,
    Internal,
}

public sealed record PostMessageRequest(string? Body);

/// <summary>An entry as staff see it, with the author's display name for team entries.</summary>
public sealed record StaffMessageResponse(
    Guid Id,
    MessageAuthorType AuthorType,
    string? AuthorName,
    string Body,
    bool IsInternal,
    DateTimeOffset CreatedAt);

/// <summary>
/// An entry as the party sees it. It has no internal flag and no staff identity: internal notes are
/// never selected for the portal, and a team author is shown only as the lender.
/// </summary>
public sealed record PortalMessageResponse(Guid Id, MessageAuthorType AuthorType, string Body, DateTimeOffset CreatedAt);

public sealed class PostMessageRequestValidator : AbstractValidator<PostMessageRequest>
{
    public PostMessageRequestValidator()
    {
        RuleFor(x => x.Body).NotEmpty().WithMessage("Write a message first.")
            .MaximumLength(MessageLimits.MaxBodyLength);
    }
}
