using DocumentInvite.Api.Features.Outbox;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Tests.Outbox;

public class EmailTemplatesTests
{
    private const string Link = "https://app.example.com/p/abc123_-token";
    private static readonly DateOnly Due = new(2027, 3, 5);

    public static TheoryData<string, EmailContent> AllTemplates => new()
    {
        { "invite", EmailTemplates.Invite(new("Pat", "Request 42", Link, Due, 3)) },
        { "reminder", EmailTemplates.Reminder(new("Pat", "Request 42", Link, Due, 2)) },
        { "overdue", EmailTemplates.Overdue(new("Pat", "Request 42", Link, Due, 2)) },
        { "rejected", EmailTemplates.ItemRejected(new("Pat", "Request 42", "Bank statement", "Page 2 is missing", Link)) },
        { "message", EmailTemplates.NewMessage(new("Pat", "Request 42", Link)) },
    };

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void Every_email_has_html_and_text_bodies_with_the_portal_link(string name, EmailContent content)
    {
        content.Subject.Should().NotBeNullOrWhiteSpace(name);
        content.Subject.Should().Contain("Request 42");
        content.Html.Should().Contain($"href=\"{Link}\"").And.Contain("Hello Pat,");
        content.Text.Should().Contain(Link).And.Contain("Hello Pat,");
        content.Html.Should().NotContain("<script");
    }

    [Fact]
    public void Invite_mentions_the_item_count_and_due_date()
    {
        var content = EmailTemplates.Invite(new("Pat", "Request 42", Link, Due, 3));

        content.Text.Should().Contain("3 items").And.Contain("5 March 2027");
    }

    [Fact]
    public void Reminder_and_overdue_use_the_outstanding_count()
    {
        EmailTemplates.Reminder(new("Pat", "Request 42", Link, Due, 1)).Text.Should().Contain("1 item is still outstanding");
        EmailTemplates.Overdue(new("Pat", "Request 42", Link, Due, 4)).Text
            .Should().Contain("were due on 5 March 2027").And.Contain("4 items");
        EmailTemplates.Overdue(new("Pat", "Request 42", Link, Due, 4)).Subject.Should().StartWith("Overdue");
    }

    [Fact]
    public void Rejected_email_names_the_item_and_the_reason()
    {
        var content = EmailTemplates.ItemRejected(new("Pat", "Request 42", "Bank statement", "Page 2 is missing", Link));

        content.Subject.Should().Contain("Bank statement");
        content.Html.Should().Contain("Page 2 is missing");
        content.Text.Should().Contain("Reason: Page 2 is missing");
    }

    [Fact]
    public void New_message_email_does_not_carry_message_content()
    {
        var content = EmailTemplates.NewMessage(new("Pat", "Request 42", Link));

        content.Text.Should().Contain("only in your secure portal");
    }

    [Fact]
    public void Values_are_html_encoded_and_subjects_have_no_line_breaks()
    {
        var content = EmailTemplates.ItemRejected(new(
            "<b>Pat</b>", "Request\r\nBcc: evil@example.com", "A & B", "<img src=x onerror=alert(1)>", Link));

        content.Html.Should().NotContain("<b>Pat</b>").And.NotContain("<img").And.Contain("&lt;img");
        content.Html.Should().Contain("A &amp; B");
        content.Subject.Should().NotContain("\r").And.NotContain("\n");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/p/relative")]
    [InlineData("")]
    public void Links_must_be_absolute_http_urls(string link)
    {
        var act = () => EmailTemplates.NewMessage(new("Pat", "Request 42", link));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Portal_links_are_built_from_the_base_url_and_escape_the_token()
    {
        var builder = new PortalLinkBuilder(Options.Create(new PortalOptions { BaseUrl = "https://app.example.com/" }));

        builder.For("a-b_c").Should().Be("https://app.example.com/p/a-b_c");
        builder.For("a/b").Should().Be("https://app.example.com/p/a%2Fb");
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:5173")]
    public void Portal_links_require_an_absolute_base_url(string baseUrl)
    {
        var builder = new PortalLinkBuilder(Options.Create(new PortalOptions { BaseUrl = baseUrl }));

        var act = () => builder.For("token");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Portal:BaseUrl*");
    }

    [Fact]
    public void A_party_message_email_for_staff_encodes_names_and_links_to_the_request_without_the_message_text()
    {
        var requestId = Guid.NewGuid();
        var builder = new PortalLinkBuilder(Options.Create(new PortalOptions { BaseUrl = "https://app.example.com/" }));
        var content = EmailTemplates.NewPartyMessage(new(
            "Sam", "Request 42", "Pat <b>O'Neil</b>", builder.ForRequest(requestId)));

        content.Html.Should().Contain($"href=\"https://app.example.com/requests/{requestId}\"").And.NotContain("<b>");
        content.Text.Should().Contain($"https://app.example.com/requests/{requestId}").And.Contain("You are receiving this");
        content.Subject.Should().Contain("Request 42");
    }
}
