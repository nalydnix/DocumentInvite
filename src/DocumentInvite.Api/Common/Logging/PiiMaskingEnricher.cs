using Serilog.Core;
using Serilog.Events;

namespace DocumentInvite.Api.Common.Logging;

/// <summary>Rewrites every property of a log event so that PII never reaches a sink.</summary>
public sealed class PiiMaskingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var property in logEvent.Properties.ToArray())
        {
            var masked = Mask(property.Key, property.Value);
            if (!ReferenceEquals(masked, property.Value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, masked));
            }
        }
    }

    internal static LogEventPropertyValue Mask(string? name, LogEventPropertyValue value)
    {
        if (name is not null && PiiMasker.IsSensitiveName(name) && value is not (StructureValue or SequenceValue or DictionaryValue))
        {
            return new ScalarValue(PiiMasker.Redacted);
        }

        switch (value)
        {
            case ScalarValue { Value: string text }:
                var maskedText = name == "RequestPath" ? PiiMasker.MaskPath(text) : PiiMasker.MaskText(text);
                return maskedText == text ? value : new ScalarValue(maskedText);
            case StructureValue structure:
                return new StructureValue(
                    structure.Properties.Select(p => new LogEventProperty(p.Name, Mask(p.Name, p.Value))),
                    structure.TypeTag);
            case SequenceValue sequence:
                return new SequenceValue(sequence.Elements.Select(e => Mask(name, e)));
            case DictionaryValue dictionary:
                return new DictionaryValue(dictionary.Elements.Select(kv => new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                    kv.Key,
                    Mask(kv.Key.Value as string, kv.Value))));
            default:
                return value;
        }
    }
}
