using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Audit;

public sealed class AuditVerifier(AppDbContext db) : IAuditVerifier
{
    public async Task<AuditVerificationResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var expectedPrev = AuditHasher.GenesisHash;
        long checkedCount = 0;

        await foreach (var e in db.AuditEvents.AsNoTracking().OrderBy(x => x.Id).AsAsyncEnumerable()
                           .WithCancellation(cancellationToken))
        {
            if (!e.PrevHash.AsSpan().SequenceEqual(expectedPrev) ||
                !e.Hash.AsSpan().SequenceEqual(AuditHasher.ComputeHash(e.PrevHash, e)))
            {
                return new AuditVerificationResult(false, e.Id, checkedCount + 1);
            }

            expectedPrev = e.Hash;
            checkedCount++;
        }

        return new AuditVerificationResult(true, null, checkedCount);
    }
}
