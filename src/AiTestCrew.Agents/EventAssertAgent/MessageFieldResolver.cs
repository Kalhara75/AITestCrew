using System.Globalization;
using AiTestCrew.Agents.EventAssertAgent.Body;

namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// Single dispatch entry resolving any <c>EventCriterion.Field</c> /
/// <c>EventCapture.Field</c> path against a <see cref="ReceivedMessageView"/>
/// to a scalar value. Knows the path-prefix table from the requirement:
/// system properties (MessageId, CorrelationId, …),
/// <c>ApplicationProperties.&lt;name&gt;</c>, <c>Body.&lt;jsonpath&gt;</c>,
/// <c>BodyXml.&lt;xpath&gt;</c>, <c>BodyText</c>, <c>BodyLength</c>.
///
/// <para>
/// Used by both the agent's evaluation loop and the field-path picker on
/// the editor's "Peek messages" panel (Phase 8) so the runtime and authoring
/// surfaces agree on which fields exist on a message.
/// </para>
/// </summary>
public static class MessageFieldResolver
{
    /// <summary>
    /// Resolves <paramref name="fieldPath"/> against <paramref name="message"/>.
    /// <paramref name="effectiveBodyFormat"/> is the result of
    /// <c>BodyFormatDetector.Resolve</c>; pass it once per message rather than
    /// re-sniffing per criterion.
    ///
    /// <paramref name="bodyOverride"/> lets the caller supply an alternate
    /// body buffer for <c>Body.*</c> / <c>BodyXml.*</c> / <c>BodyText</c> /
    /// <c>BodyLength</c> resolution — typically the result of
    /// <c>BodyDecompressor.MaybeDecompress</c> on a Rebus / NServiceBus /
    /// MassTransit-wrapped message. When null, falls back to
    /// <see cref="ReceivedMessageView.Body"/> (back-compat with existing
    /// callers).
    /// </summary>
    public static ExtractResult Resolve(
        ReceivedMessageView message,
        string fieldPath,
        BodyFormat effectiveBodyFormat,
        byte[]? bodyOverride = null)
    {
        if (string.IsNullOrWhiteSpace(fieldPath))
            return ExtractResult.Failed("field path is empty");

        // Most body-extraction paths use the override when supplied. System
        // properties + ApplicationProperties never look at the body, so they
        // ignore it.
        var effectiveBody = bodyOverride ?? message.Body;

        // ── ApplicationProperties.<name> ──
        if (fieldPath.StartsWith("ApplicationProperties.", StringComparison.OrdinalIgnoreCase))
        {
            var key = fieldPath["ApplicationProperties.".Length..];
            if (string.IsNullOrEmpty(key))
                return ExtractResult.Failed("ApplicationProperties.* requires a property name");
            if (!message.ApplicationProperties.TryGetValue(key, out var value))
                return ExtractResult.Failed(
                    $"application property '{key}' not present on message");
            return value is null
                ? ExtractResult.FoundNullValue()
                : ExtractResult.FoundValue(Stringify(value));
        }

        // ── Body.<jsonpath> ──
        if (fieldPath.StartsWith("Body.", StringComparison.OrdinalIgnoreCase))
        {
            if (effectiveBodyFormat == BodyFormat.Binary)
                return ExtractResult.Failed(
                    "binary body — only system / application properties and BodyLength are matchable");
            if (effectiveBodyFormat != BodyFormat.Json && effectiveBodyFormat != BodyFormat.Auto)
                return ExtractResult.Failed(
                    $"Body.* requires JSON body format; resolved format is {effectiveBodyFormat}. Use BodyXml.* for XML or BodyText for raw text.");
            var path = fieldPath["Body.".Length..];
            return JsonBodyExtractor.Extract(effectiveBody, path);
        }

        // ── BodyXml.<xpath> ──
        if (fieldPath.StartsWith("BodyXml.", StringComparison.OrdinalIgnoreCase))
        {
            if (effectiveBodyFormat == BodyFormat.Binary)
                return ExtractResult.Failed(
                    "binary body — only system / application properties and BodyLength are matchable");
            if (effectiveBodyFormat != BodyFormat.Xml && effectiveBodyFormat != BodyFormat.Auto)
                return ExtractResult.Failed(
                    $"BodyXml.* requires XML body format; resolved format is {effectiveBodyFormat}. Use Body.* for JSON or BodyText for raw text.");
            var xpath = fieldPath["BodyXml.".Length..];
            return XmlBodyExtractor.Extract(effectiveBody, xpath);
        }

        // ── BodyText / BodyLength (always available, even on binary bodies for length) ──
        if (string.Equals(fieldPath, "BodyText", StringComparison.OrdinalIgnoreCase))
        {
            if (effectiveBodyFormat == BodyFormat.Binary)
                return ExtractResult.Failed(
                    "binary body — BodyText is unsafe; use BodyLength or system / application properties");
            if (effectiveBody.Length == 0)
                return ExtractResult.FoundValue("");
            try
            {
                return ExtractResult.FoundValue(
                    System.Text.Encoding.UTF8.GetString(effectiveBody));
            }
            catch (Exception ex)
            {
                return ExtractResult.Failed($"body is not valid UTF-8: {ex.Message}");
            }
        }

        if (string.Equals(fieldPath, "BodyLength", StringComparison.OrdinalIgnoreCase))
            return ExtractResult.FoundValue(
                effectiveBody.Length.ToString(CultureInfo.InvariantCulture));

        // ── System properties — exact (case-insensitive) match against the view's fields. ──
        return ResolveSystemProperty(message, fieldPath);
    }

    private static ExtractResult ResolveSystemProperty(ReceivedMessageView m, string field)
    {
        if (Match(field, "MessageId"))
            return Wrap(m.MessageId);
        if (Match(field, "CorrelationId"))
            return Wrap(m.CorrelationId);
        if (Match(field, "Subject"))
            return Wrap(m.Subject);
        if (Match(field, "ContentType"))
            return Wrap(m.ContentType);
        if (Match(field, "ReplyTo"))
            return Wrap(m.ReplyTo);
        if (Match(field, "To"))
            return Wrap(m.To);
        if (Match(field, "SessionId"))
            return Wrap(m.SessionId);
        if (Match(field, "PartitionKey"))
            return Wrap(m.PartitionKey);
        if (Match(field, "EnqueuedTimeUtc"))
            return ExtractResult.FoundValue(
                m.EnqueuedTimeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (Match(field, "DeliveryCount"))
            return ExtractResult.FoundValue(
                m.DeliveryCount.ToString(CultureInfo.InvariantCulture));

        return ExtractResult.Failed(
            $"unknown field path '{field}' — expected one of: MessageId, CorrelationId, Subject, ContentType, ReplyTo, To, SessionId, EnqueuedTimeUtc, DeliveryCount, PartitionKey, ApplicationProperties.<name>, Body.<jsonpath>, BodyXml.<xpath>, BodyText, BodyLength");
    }

    private static bool Match(string field, string canonical) =>
        string.Equals(field, canonical, StringComparison.OrdinalIgnoreCase);

    private static ExtractResult Wrap(string? value) =>
        value is null ? ExtractResult.FoundNullValue() : ExtractResult.FoundValue(value);

    private static string Stringify(object value)
    {
        return value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }
}
