using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Contacts;
using DocumentInvite.Api.Features.Files;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Loans;
using DocumentInvite.Api.Features.Portal;
using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Files;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocumentInvite.Api.Tests.Portal;

/// <summary>Helpers for portal tests: staff create real invites through the API and the party uses the emailed token.</summary>
public abstract partial class PortalTestBase : StaffTestBase
{
    protected static readonly byte[] Pdf = "%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF"u8.ToArray();

    protected readonly FakeBlobStore Blobs = new();
    protected virtual int RateLimit => 1000;

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.RemoveAll<IBlobStore>();
        services.AddSingleton<IBlobStore>(Blobs);
        services.Configure<PortalSessionOptions>(o => o.RateLimitPerMinute = RateLimit);
    }

    protected sealed record Party(InviteResponse Invite, string Token, Guid ContactId)
    {
        public Guid FileItemId => Invite.Items.First(i => i.Kind == ItemKind.FileUpload).Id;
        public Guid NumberItemId => Invite.Items.First(i => i.FieldType == FieldType.Number).Id;
        public Guid YesNoItemId => Invite.Items.First(i => i.FieldType == FieldType.YesNo).Id;
        public Guid MultiItemId => Invite.Items.First(i => i.AllowMultiple).Id;
    }

    private int _counter;

    protected async Task<HttpClient> OfficerAsync() => await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

    /// <summary>Creates a loan with one invite (a single file, a multi file, a number and a yes/no item) and returns the emailed token.</summary>
    protected async Task<Party> SendInviteAsync(HttpClient officer, string? password = null, string? lastName = null)
    {
        var n = Interlocked.Increment(ref _counter);
        var contact = await officer.PostAsJsonAsync("/api/contacts",
            new ContactRequest($"Ann{n}", lastName ?? $"Lee{n}", $"ann{n}@example.com", null, PartyType.Borrower));
        contact.StatusCode.Should().Be(HttpStatusCode.Created);
        var contactId = (await contact.Content.ReadFromJsonAsync<ContactResponse>())!.Id;

        var loan = await officer.PostAsJsonAsync("/api/loans", new LoanRequest($"LN-{n:0000}", $"Loan {n}", 100m, "SBA 7(a)"));
        loan.StatusCode.Should().Be(HttpStatusCode.Created);
        var loanId = (await loan.Content.ReadFromJsonAsync<LoanResponse>())!.Id;

        var items = new List<InviteItemRequest>
        {
            new("Tax return", ItemKind.FileUpload, "Last two years", false, null, true),
            new("Bank statements", ItemKind.FileUpload, "Six months", true, null, false),
            new("Years in business", ItemKind.FormField, "", false, FieldType.Number, true),
            new("Owns property", ItemKind.FormField, "", false, FieldType.YesNo, false),
        };
        var response = await officer.PostAsJsonAsync($"/api/loans/{loanId}/invites",
            new SendInvitesRequest([new SendPartyRequest(
                contactId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)), null, items, password)]));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var invite = (await response.Content.ReadFromJsonAsync<List<InviteResponse>>())![0];
        return new Party(invite, await TokenForAsync(contactId), contactId);
    }

    protected async Task<string> TokenForAsync(Guid contactId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = await db.Contacts.AsNoTracking().Where(c => c.Id == contactId).Select(c => c.Email).SingleAsync();
        var mails = await db.OutboxEmails.AsNoTracking().Where(m => m.To == email).OrderByDescending(m => m.CreatedAt).ToListAsync();
        return LinkPattern().Match(mails[0].TextBody).Groups[1].Value;
    }

    [GeneratedRegex(@"/p/([A-Za-z0-9_-]+)")]
    private static partial Regex LinkPattern();

    protected sealed record SessionBody(string SessionToken, DateTimeOffset ExpiresAt);

    protected Task<HttpResponseMessage> ExchangeAsync(string token, string? password = null, HttpClient? client = null) =>
        (client ?? NewClient()).PostAsJsonAsync("/api/portal/session", new { token, password });

    /// <summary>Exchanges the token and returns a client that carries the portal session.</summary>
    protected async Task<HttpClient> SessionAsync(string token, string? password = null, bool acceptPrivacy = true)
    {
        var response = await ExchangeAsync(token, password);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = (await response.Content.ReadFromJsonAsync<SessionBody>())!;
        var client = NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.SessionToken);
        if (acceptPrivacy)
        {
            (await client.PostAsync("/api/portal/privacy", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        return client;
    }

    protected static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    protected async Task<Invite> InviteRowAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Invites.AsNoTracking().Include(i => i.Items).SingleAsync(i => i.Id == id);
    }

    protected async Task<Loan> LoanRowAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Loans.AsNoTracking().SingleAsync(l => l.Id == id);
    }

    protected async Task<List<StoredFile>> FilesAsync(Guid itemId)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().StoredFiles.AsNoTracking()
            .Where(f => f.RequestItemId == itemId).OrderBy(f => f.CreatedAt).ToListAsync();
    }

    /// <summary>Requests a slot, "uploads" the bytes to the fake store and completes the upload.</summary>
    protected async Task<HttpResponseMessage> UploadAsync(
        HttpClient session, Guid itemId, string name = "statement.pdf", byte[]? content = null)
    {
        content ??= Pdf;
        var slotResponse = await session.PostAsJsonAsync($"/api/portal/items/{itemId}/upload-slot",
            new { fileName = name, contentType = "application/pdf", sizeBytes = content.Length });
        slotResponse.StatusCode.Should().Be(HttpStatusCode.OK, await slotResponse.Content.ReadAsStringAsync());
        var slot = (await slotResponse.Content.ReadFromJsonAsync<UploadSlot>())!;

        var file = (await FilesAsync(itemId)).Single(f => f.Id == slot.FileId);
        Blobs.Put(file.BlobName, content, "application/pdf");
        return await session.PostAsJsonAsync($"/api/portal/items/{itemId}/complete", new { fileId = slot.FileId });
    }
}
