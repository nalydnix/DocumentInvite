using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DocumentInvite.Api.Domain;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Features.Outbox;

/// <summary>Thin client for the Resend HTTP API (<c>POST /emails</c>).</summary>
public sealed class ResendClient(HttpClient http)
{
    public async Task<string?> SendAsync(string from, EmailMessage message, CancellationToken cancellationToken)
    {
        var payload = new ResendRequest(from, [message.To], message.Subject, message.HtmlBody, message.TextBody);
        using var response = await http.PostAsJsonAsync("emails", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The response body is not read: it can echo addresses back.
            throw new ResendException((int)response.StatusCode);
        }

        // The email was accepted; an unreadable body only means there is no message id to keep.
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ResendResponse>(cancellationToken);
            return body?.Id;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record ResendRequest(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string[] To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html")] string Html,
        [property: JsonPropertyName("text")] string Text);

    private sealed record ResendResponse([property: JsonPropertyName("id")] string? Id);
}

public sealed class ResendException(int statusCode) : Exception($"Resend returned HTTP {statusCode}.")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Stores the email, then sends it through Resend. If Resend fails, the failure is logged and
/// recorded on the stored copy, which is kept.
/// </summary>
public sealed class ResendEmailSender(
    OutboxStore store,
    ResendClient client,
    IOptions<EmailOptions> options,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var email = await store.AddAsync(message, EmailProvider.Resend, cancellationToken);

        string error;
        try
        {
            email.ProviderMessageId = await client.SendAsync(options.Value.From, message, cancellationToken);
            await store.SaveAsync(cancellationToken);
            return new EmailSendResult(email.Id, Delivered: true);
        }
        catch (ResendException ex)
        {
            error = $"Resend rejected the email (HTTP {ex.StatusCode}).";
        }
        catch (HttpRequestException)
        {
            error = "Resend could not be reached.";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error = "Resend did not answer in time.";
        }

        // Exception details are left out on purpose: they can carry addresses.
        logger.LogError("Email {OutboxEmailId} was stored but not delivered: {DeliveryError}", email.Id, error);
        email.DeliveryError = error;
        await store.SaveAsync(cancellationToken);
        return new EmailSendResult(email.Id, Delivered: false, error);
    }
}
