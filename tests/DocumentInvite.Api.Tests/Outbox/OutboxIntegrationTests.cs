using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Outbox;
using DocumentInvite.Api.Infrastructure.Persistence;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentInvite.Api.Tests.Outbox;

public class OutboxIntegrationTests(SqlServerFixture sql) : StaffTestBase(sql)
{
    private static readonly EmailMessage Sample = EmailMessage.From(
        "pat@example.com",
        EmailTemplates.Invite(new("Pat", "Loan 42", "https://app.example.com/p/tok", new DateOnly(2027, 3, 5), 2)));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    private WebApplicationFactory<Program> ResendFactory(StubHandler handler, string? apiKey = "re_test_key") =>
        Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Email:Provider", "Resend");
            builder.UseSetting("Email:From", "Lender <lender@example.com>");
            if (apiKey is not null) builder.UseSetting("Resend:ApiKey", apiKey);
            builder.ConfigureServices(services =>
                services.AddHttpClient<ResendClient>().ConfigurePrimaryHttpMessageHandler(() => handler));
        });

    private static async Task<EmailSendResult> SendAsync(WebApplicationFactory<Program> factory, EmailMessage message)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(message);
    }

    private async Task<List<OutboxEmail>> StoredAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OutboxEmails.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync();
    }

    [Fact]
    public async Task The_demo_provider_is_the_default_and_stores_the_email()
    {
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IEmailSender>().Should().BeOfType<DemoOutboxEmailSender>();

        var result = await SendAsync(Factory, Sample);

        result.Delivered.Should().BeTrue();
        var stored = (await StoredAsync()).Should().ContainSingle().Subject;
        stored.Id.Should().Be(result.OutboxEmailId);
        stored.To.Should().Be("pat@example.com");
        stored.Subject.Should().Be(Sample.Subject);
        stored.HtmlBody.Should().Contain("https://app.example.com/p/tok");
        stored.TextBody.Should().Contain("https://app.example.com/p/tok");
        stored.Provider.Should().Be(EmailProvider.Demo);
        stored.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task The_resend_provider_is_selected_by_configuration_and_still_stores_the_email()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = "resend-msg-1" }),
        });
        using var factory = ResendFactory(handler);

        var result = await SendAsync(factory, Sample);

        result.Delivered.Should().BeTrue();
        var stored = (await StoredAsync()).Should().ContainSingle().Subject;
        stored.Provider.Should().Be(EmailProvider.Resend);
        stored.ProviderMessageId.Should().Be("resend-msg-1");
        stored.DeliveryError.Should().BeNull();

        var (request, body) = handler.Calls.Should().ContainSingle().Subject;
        request.RequestUri!.AbsoluteUri.Should().Be("https://api.resend.com/emails");
        request.Headers.Authorization!.ToString().Should().Be("Bearer re_test_key");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("from").GetString().Should().Be("Lender <lender@example.com>");
        json.RootElement.GetProperty("to")[0].GetString().Should().Be("pat@example.com");
        json.RootElement.GetProperty("html").GetString().Should().Contain("https://app.example.com/p/tok");
        json.RootElement.GetProperty("text").GetString().Should().Contain("https://app.example.com/p/tok");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    public async Task An_unreadable_success_body_still_counts_as_delivered(string body)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        using var factory = ResendFactory(handler);

        var result = await SendAsync(factory, Sample);

        result.Delivered.Should().BeTrue();
        var stored = (await StoredAsync()).Should().ContainSingle().Subject;
        stored.ProviderMessageId.Should().BeNull();
        stored.DeliveryError.Should().BeNull();
    }

    [Fact]
    public async Task A_resend_failure_is_returned_and_the_stored_copy_is_kept()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("{\"message\":\"bad address pat@example.com\"}"),
        });
        using var factory = ResendFactory(handler);

        var result = await SendAsync(factory, Sample);

        result.Delivered.Should().BeFalse();
        result.Error.Should().Contain("422").And.NotContain("pat@example.com");
        var stored = (await StoredAsync()).Should().ContainSingle().Subject;
        stored.Id.Should().Be(result.OutboxEmailId);
        stored.DeliveryError.Should().Be(result.Error);
        stored.ProviderMessageId.Should().BeNull();
    }

    [Fact]
    public async Task An_unreachable_resend_is_a_failure_not_an_exception()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused for pat@example.com"));
        using var factory = ResendFactory(handler);

        var result = await SendAsync(factory, Sample);

        result.Delivered.Should().BeFalse();
        result.Error.Should().NotContain("pat@example.com");
        (await StoredAsync()).Should().ContainSingle().Which.DeliveryError.Should().NotBeNull();
    }

    [Fact]
    public async Task Resend_without_an_api_key_fails_fast_instead_of_sending()
    {
        using var factory = ResendFactory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), apiKey: null);

        var act = () => SendAsync(factory, Sample);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Resend:ApiKey*");
    }

    [Fact]
    public async Task Staff_see_the_outbox_newest_first()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        await SendAsync(Factory, Sample with { Subject = "first" });
        await Task.Delay(20);
        await SendAsync(Factory, Sample with { Subject = "second" });

        var page = await client.GetFromJsonAsync<OutboxPage>("/api/outbox");

        page!.Total.Should().Be(2);
        page.Items.Select(i => i.Subject).Should().Equal("second", "first");

        var detail = await client.GetFromJsonAsync<OutboxEmailDetail>($"/api/outbox/{page.Items[0].Id}");
        detail!.HtmlBody.Should().Contain("https://app.example.com/p/tok");
        detail.TextBody.Should().Contain("https://app.example.com/p/tok");
    }

    [Fact]
    public async Task The_outbox_pages_results()
    {
        var client = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);
        for (var i = 0; i < 3; i++)
        {
            await SendAsync(Factory, Sample with { Subject = $"mail {i}" });
            await Task.Delay(20);
        }

        var page = await client.GetFromJsonAsync<OutboxPage>("/api/outbox?skip=1&take=1");

        page!.Total.Should().Be(3);
        page.Items.Should().ContainSingle().Which.Subject.Should().Be("mail 1");
    }

    [Fact]
    public async Task Unknown_emails_return_404()
    {
        var client = await ClientForAsync("admin@example.com", [StaffRoles.Admin]);

        (await client.GetAsync($"/api/outbox/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(StaffRoles.Viewer)]
    [InlineData(StaffRoles.Auditor)]
    public async Task Only_admins_and_loan_officers_can_read_the_outbox(string role)
    {
        var client = await ClientForAsync("other@example.com", [role]);
        var result = await SendAsync(Factory, Sample);

        (await client.GetAsync("/api/outbox")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/outbox/{result.OutboxEmailId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_callers_cannot_read_the_outbox()
    {
        (await NewClient().GetAsync("/api/outbox")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
