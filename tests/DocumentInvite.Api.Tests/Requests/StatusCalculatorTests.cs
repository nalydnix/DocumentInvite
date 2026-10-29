using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Invites;
using FluentAssertions;
using static DocumentInvite.Api.Domain.CollectionStatus;
using static DocumentInvite.Api.Domain.RequestItemStatus;

namespace DocumentInvite.Api.Tests.Requests;

public class StatusCalculatorTests
{
    private static ItemFacts Required(RequestItemStatus status) => new(status, true);

    private static ItemFacts Optional(RequestItemStatus status) => new(status, false);

    // ---- invite transitions ----

    [Fact]
    public void An_invite_with_nothing_done_is_Sent()
    {
        StatusCalculator.ForInvite([Required(Outstanding), Required(Outstanding)], portalOpened: false)
            .Should().Be(Sent);
    }

    [Fact]
    public void An_invite_without_items_is_Sent_until_the_portal_is_opened()
    {
        StatusCalculator.ForInvite([], portalOpened: false).Should().Be(Sent);
        StatusCalculator.ForInvite([], portalOpened: true).Should().Be(InProgress);
    }

    [Fact]
    public void Opening_the_portal_moves_Sent_to_InProgress()
    {
        StatusCalculator.ForInvite([Required(Outstanding)], portalOpened: true).Should().Be(InProgress);
    }

    [Fact]
    public void Submitting_one_of_two_required_items_moves_Sent_to_InProgress()
    {
        StatusCalculator.ForInvite([Required(RequestItemStatus.Submitted), Required(Outstanding)], portalOpened: false)
            .Should().Be(InProgress);
    }

    [Fact]
    public void Submitting_every_required_item_moves_InProgress_to_Submitted()
    {
        StatusCalculator.ForInvite([Required(RequestItemStatus.Submitted), Required(RequestItemStatus.Submitted)], portalOpened: true)
            .Should().Be(CollectionStatus.Submitted);
    }

    [Fact]
    public void Outstanding_optional_items_do_not_hold_back_Submitted()
    {
        StatusCalculator.ForInvite([Required(RequestItemStatus.Submitted), Optional(Outstanding)], portalOpened: true)
            .Should().Be(CollectionStatus.Submitted);
    }

    [Fact]
    public void A_mix_of_Submitted_and_Approved_required_items_is_Submitted()
    {
        StatusCalculator.ForInvite([Required(Approved), Required(RequestItemStatus.Submitted)], portalOpened: true)
            .Should().Be(CollectionStatus.Submitted);
    }

    [Fact]
    public void Approving_every_item_moves_Submitted_to_Completed()
    {
        StatusCalculator.ForInvite([Required(Approved), Required(Approved)], portalOpened: true)
            .Should().Be(Completed);
    }

    [Fact]
    public void An_outstanding_optional_item_blocks_Completed()
    {
        StatusCalculator.ForInvite([Required(Approved), Optional(Outstanding)], portalOpened: true)
            .Should().Be(CollectionStatus.Submitted);
    }

    [Fact]
    public void Approved_optional_items_alone_complete_the_invite()
    {
        StatusCalculator.ForInvite([Optional(Approved)], portalOpened: true).Should().Be(Completed);
    }

    [Fact]
    public void Optional_items_alone_never_make_an_invite_Submitted()
    {
        StatusCalculator.ForInvite([Optional(RequestItemStatus.Submitted)], portalOpened: true).Should().Be(InProgress);
    }

    [Fact]
    public void A_rejection_returns_a_Submitted_invite_to_InProgress()
    {
        StatusCalculator.ForInvite([Required(Rejected), Required(RequestItemStatus.Submitted)], portalOpened: true)
            .Should().Be(InProgress);
    }

    [Fact]
    public void A_rejection_returns_a_Completed_invite_to_InProgress()
    {
        StatusCalculator.ForInvite([Required(Rejected), Required(Approved)], portalOpened: true)
            .Should().Be(InProgress);
    }

    [Fact]
    public void A_rejected_optional_item_also_returns_the_invite_to_InProgress()
    {
        StatusCalculator.ForInvite([Required(RequestItemStatus.Submitted), Optional(Rejected)], portalOpened: true)
            .Should().Be(InProgress);
    }

    [Fact]
    public void Resubmitting_a_rejected_item_moves_the_invite_back_to_Submitted()
    {
        StatusCalculator.ForInvite([Required(RequestItemStatus.Submitted), Required(Approved)], portalOpened: true)
            .Should().Be(CollectionStatus.Submitted);
    }

    // ---- request transitions ----

    [Fact]
    public void A_request_without_active_invites_is_Sent()
    {
        StatusCalculator.ForRequest([]).Should().Be(Sent);
    }

    [Theory]
    [InlineData(Sent, Sent, Sent)]
    [InlineData(Sent, InProgress, InProgress)]
    [InlineData(Sent, CollectionStatus.Submitted, InProgress)]
    [InlineData(Sent, Completed, InProgress)]
    [InlineData(InProgress, InProgress, InProgress)]
    [InlineData(InProgress, CollectionStatus.Submitted, InProgress)]
    [InlineData(InProgress, Completed, InProgress)]
    [InlineData(CollectionStatus.Submitted, CollectionStatus.Submitted, CollectionStatus.Submitted)]
    [InlineData(CollectionStatus.Submitted, Completed, CollectionStatus.Submitted)]
    [InlineData(Completed, Completed, ReadyToComplete)]
    public void A_request_is_derived_from_its_invites(
        CollectionStatus first, CollectionStatus second, CollectionStatus expected)
    {
        StatusCalculator.ForRequest([first, second]).Should().Be(expected);
        StatusCalculator.ForRequest([second, first]).Should().Be(expected);
    }

    // ---- applying the result to entities ----

    private static Invite InviteWith(bool opened, params RequestItemStatus[] statuses) => new()
    {
        PortalOpenedAt = opened ? DateTimeOffset.UnixEpoch : null,
        Items = statuses.Select(s => new RequestItem { Status = s, IsRequired = true }).ToList(),
    };

    [Fact]
    public void Approving_every_invite_makes_the_request_ReadyToComplete_without_stamping_CompletedAt()
    {
        var request = new Request { Invites = [InviteWith(true, Approved), InviteWith(true, Approved)] };

        StatusRecalculator.Apply(request);

        request.Status.Should().Be(ReadyToComplete);
        request.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void A_completed_request_stays_Completed_and_keeps_CompletedAt()
    {
        var stamped = DateTimeOffset.UnixEpoch;
        var request = new Request { CompletedAt = stamped, Invites = [InviteWith(true, Approved)] };
        request.Invites[0].Items[0].Status = Rejected;

        StatusRecalculator.Apply(request);

        request.Status.Should().Be(Completed);
        request.CompletedAt.Should().Be(stamped);
    }

    [Fact]
    public void A_ReadyToComplete_request_returns_to_InProgress_when_an_item_is_rejected()
    {
        var request = new Request { Invites = [InviteWith(true, Approved)] };
        StatusRecalculator.Apply(request);
        request.Status.Should().Be(ReadyToComplete);

        request.Invites[0].Items[0].Status = Rejected;
        StatusRecalculator.Apply(request);

        request.Invites[0].Status.Should().Be(InProgress);
        request.Status.Should().Be(InProgress);
    }

    [Fact]
    public void Revoked_invites_do_not_count_towards_the_request_status()
    {
        var revoked = InviteWith(false, Outstanding);
        revoked.RevokedAt = DateTimeOffset.UnixEpoch;
        var request = new Request { Invites = [revoked, InviteWith(true, Approved)] };

        StatusRecalculator.Apply(request);

        request.Status.Should().Be(ReadyToComplete);
    }

    // ---- portal tokens ----

    [Fact]
    public void Tokens_are_32_random_bytes_in_base64url()
    {
        var token = PortalTokens.Generate();

        token.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(token).Should().HaveCount(32);
        PortalTokens.Generate().Should().NotBe(token);
    }

    [Fact]
    public void The_stored_hash_is_the_sha256_of_the_token()
    {
        var hash = PortalTokens.Hash("abc");

        Convert.ToHexString(hash).Should().Be("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
    }
}
