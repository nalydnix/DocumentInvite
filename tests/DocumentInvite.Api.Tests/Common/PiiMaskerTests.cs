using DocumentInvite.Api.Common.Logging;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace DocumentInvite.Api.Tests.Common;

public class PiiMaskerTests
{
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE"; // 43 chars

    [Fact]
    public void Token_fixture_is_43_characters() => Token.Should().HaveLength(43);

    [Theory]
    [InlineData("Contact jane.doe+loan@example.co.uk failed", "Contact [email] failed")]
    [InlineData("Call +1 (555) 123-4567 now", "Call [phone] now")]
    [InlineData("Call 555.123.4567 now", "Call [phone] now")]
    public void MaskText_masks_emails_and_phone_numbers(string input, string expected) =>
        PiiMasker.MaskText(input).Should().Be(expected);

    [Fact]
    public void MaskText_masks_portal_tokens() =>
        PiiMasker.MaskText($"opened {Token} ok").Should().Be("opened [token] ok");

    [Theory]
    [InlineData("Invite created for loan 3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("Due 2026-10-27 amount 250000")]
    [InlineData("")]
    public void MaskText_leaves_ordinary_text_alone(string input) =>
        PiiMasker.MaskText(input).Should().Be(input);

    [Theory]
    [InlineData("/p/" + Token, "/p/[token]")]
    [InlineData("/p/shortvalue/upload", "/p/[token]/upload")]
    [InlineData("/api/portal/" + Token + "/items", "/api/portal/[token]/items")]
    [InlineData("/api/loans", "/api/loans")]
    public void MaskPath_hides_token_bearing_segments(string path, string expected) =>
        PiiMasker.MaskPath(path).Should().Be(expected);

    [Theory]
    [InlineData("Email")]
    [InlineData("firstName")]
    [InlineData("LastName")]
    [InlineData("DisplayName")]
    [InlineData("Phone")]
    [InlineData("Token")]
    [InlineData("Password")]
    public void Sensitive_property_names_are_recognised(string name) =>
        PiiMasker.IsSensitiveName(name).Should().BeTrue();

    [Fact]
    public void Enricher_masks_sensitive_named_properties_and_embedded_values()
    {
        var sink = new CollectingSink();
        var log = new LoggerConfiguration().Enrich.With<PiiMaskingEnricher>().WriteTo.Sink(sink).CreateLogger();

        log.Information(
            "Sent {Email} {Detail} {RequestPath} {@Contact} {Count}",
            "jane@example.com",
            "phone 555-123-4567",
            "/p/" + Token,
            new { FirstName = "Jane", City = "Austin", Note = "mail bob@example.com" },
            3);

        var rendered = sink.Events.Single().RenderMessage();
        rendered.Should().NotContain("jane@example.com").And.NotContain("555-123-4567")
            .And.NotContain(Token).And.NotContain("Jane").And.NotContain("bob@example.com");
        rendered.Should().Contain("Austin").And.Contain("3").And.Contain("/p/[token]");
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}

public class ExceptionMaskingSinkTests
{
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE";

    [Fact]
    public void Exception_text_is_masked_before_reaching_the_inner_sink()
    {
        var captured = new List<LogEvent>();
        var inner = new LoggerConfiguration().WriteTo.Sink(new Collector(captured)).CreateLogger();
        var log = new LoggerConfiguration().WriteTo.Sink(new ExceptionMaskingSink(inner)).CreateLogger();
        var exception = new InvalidOperationException($"Duplicate key jane@example.com for /p/{Token}");

        log.Error(exception, "Failed");

        var emitted = captured.Single().Exception!;
        var text = emitted.ToString() + emitted.Message;
        text.Should().NotContain("jane@example.com").And.NotContain(Token);
        text.Should().Contain("[email]").And.Contain("[token]").And.Contain("InvalidOperationException");
    }

    [Fact]
    public void Events_without_exceptions_pass_through_unchanged()
    {
        var captured = new List<LogEvent>();
        var inner = new LoggerConfiguration().WriteTo.Sink(new Collector(captured)).CreateLogger();
        var log = new LoggerConfiguration().WriteTo.Sink(new ExceptionMaskingSink(inner)).CreateLogger();

        log.Information("Hello {Count}", 1);

        captured.Single().Exception.Should().BeNull();
    }

    private sealed class Collector(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }
}
