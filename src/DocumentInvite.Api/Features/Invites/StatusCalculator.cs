using DocumentInvite.Api.Domain;

namespace DocumentInvite.Api.Features.Invites;

/// <summary>What the calculator needs to know about one item.</summary>
public readonly record struct ItemFacts(RequestItemStatus Status, bool IsRequired);

/// <summary>
/// The single place where invite and loan status are decided. Pure: no clock, no database.
/// </summary>
public static class StatusCalculator
{
    /// <summary>
    /// Sent: nothing has happened yet. In progress: the portal was opened, or an item was submitted,
    /// reviewed or rejected. Submitted: at least one required item exists, none is rejected, and
    /// every required item is Submitted or Approved. Completed: every item is Approved. A rejection
    /// therefore always returns the invite to In progress.
    /// </summary>
    public static CollectionStatus ForInvite(IReadOnlyCollection<ItemFacts> items, bool portalOpened)
    {
        if (items.Count > 0 && items.All(i => i.Status == RequestItemStatus.Approved))
        {
            return CollectionStatus.Completed;
        }

        var anyRejected = items.Any(i => i.Status == RequestItemStatus.Rejected);
        var required = items.Where(i => i.IsRequired).ToList();
        if (!anyRejected && required.Count > 0 &&
            required.All(i => i.Status is RequestItemStatus.Submitted or RequestItemStatus.Approved))
        {
            return CollectionStatus.Submitted;
        }

        var anyActivity = items.Any(i => i.Status != RequestItemStatus.Outstanding);
        return portalOpened || anyActivity ? CollectionStatus.InProgress : CollectionStatus.Sent;
    }

    /// <summary>
    /// Derived from the statuses of the loan's active (not revoked) invites: Ready to complete when all
    /// are Completed (the loan only becomes Completed through the explicit complete action), Submitted when all are Submitted or Completed, Sent when all are Sent (or there
    /// are none), otherwise In progress.
    /// </summary>
    public static CollectionStatus ForLoan(IReadOnlyCollection<CollectionStatus> activeInvites)
    {
        if (activeInvites.Count == 0 || activeInvites.All(s => s == CollectionStatus.Sent))
        {
            return CollectionStatus.Sent;
        }

        if (activeInvites.All(s => s == CollectionStatus.Completed))
        {
            return CollectionStatus.ReadyToComplete;
        }

        if (activeInvites.All(s => s is CollectionStatus.Submitted or CollectionStatus.Completed))
        {
            return CollectionStatus.Submitted;
        }

        return CollectionStatus.InProgress;
    }
}

/// <summary>Applies <see cref="StatusCalculator"/> to loaded entities. <c>CompletedAt</c> is set only by the complete action.</summary>
public static class StatusRecalculator
{
    /// <summary>Recalculates every invite of the loan (items must be loaded) and then the loan itself.</summary>
    public static void Apply(Loan loan)
    {
        foreach (var invite in loan.Invites)
        {
            invite.Status = StatusCalculator.ForInvite(
                invite.Items.Select(i => new ItemFacts(i.Status, i.IsRequired)).ToList(),
                invite.PortalOpenedAt is not null);
        }

        // A completed loan is locked: it stays Completed until it is explicitly reopened.
        loan.Status = loan.CompletedAt is not null
            ? CollectionStatus.Completed
            : StatusCalculator.ForLoan(loan.Invites.Where(i => i.RevokedAt is null).Select(i => i.Status).ToList());
    }
}
