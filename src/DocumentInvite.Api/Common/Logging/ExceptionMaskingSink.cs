using Serilog.Core;
using Serilog.Events;

namespace DocumentInvite.Api.Common.Logging;

/// <summary>
/// Decorates a sink so that exceptions are written with PII masked. Exception messages can embed
/// emails, phone numbers or tokens (for example a unique-key violation), so the raw exception
/// must never reach the inner sink.
/// </summary>
public sealed class ExceptionMaskingSink(ILogEventSink inner) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Exception is null)
        {
            inner.Emit(logEvent);
            return;
        }

        inner.Emit(new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            MaskedException.From(logEvent.Exception),
            logEvent.MessageTemplate,
            logEvent.Properties.Select(p => new LogEventProperty(p.Key, p.Value)),
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default));
    }

    /// <summary>An exception whose message and text are already masked.</summary>
    public sealed class MaskedException(string typeName, string message, string text) : Exception(message)
    {
        public string OriginalType { get; } = typeName;

        public override string ToString() => text;

        public static MaskedException From(Exception exception) => new(
            exception.GetType().FullName ?? exception.GetType().Name,
            PiiMasker.MaskText(exception.Message),
            PiiMasker.MaskText(exception.ToString()));
    }
}
