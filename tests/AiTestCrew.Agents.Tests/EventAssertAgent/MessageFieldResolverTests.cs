using System.Text;
using AiTestCrew.Agents.EventAssertAgent;
using AiTestCrew.Agents.EventAssertAgent.Body;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.EventAssertAgent;

public class MessageFieldResolverTests
{
    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    private static ReceivedMessageView Msg(
        string? messageId = null,
        string? correlationId = null,
        string? contentType = null,
        Dictionary<string, object?>? appProps = null,
        byte[]? body = null,
        string? subject = null,
        DateTimeOffset? enqueuedAt = null,
        int deliveryCount = 1)
    {
        return new ReceivedMessageView
        {
            MessageId = messageId,
            CorrelationId = correlationId,
            ContentType = contentType,
            Subject = subject,
            ApplicationProperties = appProps ?? new Dictionary<string, object?>(),
            Body = body ?? Array.Empty<byte>(),
            EnqueuedTimeUtc = enqueuedAt ?? DateTimeOffset.UtcNow,
            DeliveryCount = deliveryCount,
        };
    }

    // ── System properties ──────────────────────────────────────────────

    [Fact]
    public void Resolves_messageId()
    {
        var m = Msg(messageId: "abc-123");
        var r = MessageFieldResolver.Resolve(m, "MessageId", BodyFormat.Text);
        r.Status.Should().Be(ExtractStatus.Found);
        r.Value.Should().Be("abc-123");
    }

    [Fact]
    public void System_property_match_is_case_insensitive()
    {
        var m = Msg(correlationId: "X");
        MessageFieldResolver.Resolve(m, "correlationid", BodyFormat.Text)
            .Value.Should().Be("X");
    }

    [Fact]
    public void Null_system_property_returns_FoundNull()
    {
        var m = Msg(messageId: null);
        MessageFieldResolver.Resolve(m, "MessageId", BodyFormat.Text)
            .Status.Should().Be(ExtractStatus.FoundNull);
    }

    [Fact]
    public void Unknown_system_property_returns_failed()
    {
        var r = MessageFieldResolver.Resolve(Msg(), "DoesNotExist", BodyFormat.Text);
        r.Status.Should().Be(ExtractStatus.Failed);
        r.Error.Should().Contain("unknown field path");
    }

    [Fact]
    public void DeliveryCount_serialises_as_int()
    {
        MessageFieldResolver.Resolve(Msg(deliveryCount: 7), "DeliveryCount", BodyFormat.Text)
            .Value.Should().Be("7");
    }

    // ── ApplicationProperties ──────────────────────────────────────────

    [Fact]
    public void Resolves_application_property()
    {
        var m = Msg(appProps: new() { ["EventType"] = "MeterReadingCreated" });
        MessageFieldResolver.Resolve(m, "ApplicationProperties.EventType", BodyFormat.Text)
            .Value.Should().Be("MeterReadingCreated");
    }

    [Fact]
    public void Application_property_missing_returns_failed()
    {
        var r = MessageFieldResolver.Resolve(Msg(), "ApplicationProperties.Missing", BodyFormat.Text);
        r.Status.Should().Be(ExtractStatus.Failed);
        r.Error.Should().Contain("not present");
    }

    [Fact]
    public void Application_property_null_returns_FoundNull()
    {
        var m = Msg(appProps: new() { ["X"] = null });
        MessageFieldResolver.Resolve(m, "ApplicationProperties.X", BodyFormat.Text)
            .Status.Should().Be(ExtractStatus.FoundNull);
    }

    [Fact]
    public void Application_property_bool_stringifies_lowercase()
    {
        var m = Msg(appProps: new() { ["IsRetry"] = true });
        MessageFieldResolver.Resolve(m, "ApplicationProperties.IsRetry", BodyFormat.Text)
            .Value.Should().Be("true");
    }

    // ── Body.* (JSON) ──────────────────────────────────────────────────

    [Fact]
    public void Body_jsonpath_resolves_against_json_body()
    {
        var m = Msg(body: U("{\"OrderId\":\"12345\"}"), contentType: "application/json");
        MessageFieldResolver.Resolve(m, "Body.OrderId", BodyFormat.Json)
            .Value.Should().Be("12345");
    }

    [Fact]
    public void Body_jsonpath_missing_returns_failed()
    {
        var m = Msg(body: U("{\"OrderId\":\"12345\"}"), contentType: "application/json");
        var r = MessageFieldResolver.Resolve(m, "Body.Missing", BodyFormat.Json);
        r.Status.Should().Be(ExtractStatus.Failed);
        r.Error.Should().Contain("not found");
    }

    [Fact]
    public void Body_jsonpath_against_xml_body_fails_with_typed_reason()
    {
        var m = Msg(body: U("<Order Id=\"12345\"/>"), contentType: "application/xml");
        var r = MessageFieldResolver.Resolve(m, "Body.OrderId", BodyFormat.Xml);
        r.Status.Should().Be(ExtractStatus.Failed);
        r.Error.Should().Contain("requires JSON body format");
    }

    // ── BodyXml.* ──────────────────────────────────────────────────────

    [Fact]
    public void BodyXml_attribute_path_resolves()
    {
        var m = Msg(body: U("<Order Id=\"12345\"/>"), contentType: "application/xml");
        MessageFieldResolver.Resolve(m, "BodyXml.//Order/@Id", BodyFormat.Xml)
            .Value.Should().Be("12345");
    }

    [Fact]
    public void BodyXml_element_text_resolves()
    {
        var m = Msg(body: U("<Order><Id>42</Id></Order>"), contentType: "application/xml");
        MessageFieldResolver.Resolve(m, "BodyXml.//Order/Id", BodyFormat.Xml)
            .Value.Should().Be("42");
    }

    [Fact]
    public void BodyXml_missing_xpath_returns_failed()
    {
        var m = Msg(body: U("<Order Id=\"x\"/>"), contentType: "application/xml");
        var r = MessageFieldResolver.Resolve(m, "BodyXml.//Missing", BodyFormat.Xml);
        r.Status.Should().Be(ExtractStatus.Failed);
        r.Error.Should().Contain("not found");
    }

    // ── BodyText / BodyLength ──────────────────────────────────────────

    [Fact]
    public void BodyText_returns_raw_utf8()
    {
        var m = Msg(body: U("hello world"));
        MessageFieldResolver.Resolve(m, "BodyText", BodyFormat.Text)
            .Value.Should().Be("hello world");
    }

    [Fact]
    public void BodyLength_returns_byte_count()
    {
        var m = Msg(body: U("12345"));
        MessageFieldResolver.Resolve(m, "BodyLength", BodyFormat.Text)
            .Value.Should().Be("5");
    }

    // ── Binary body — Body.* / BodyXml.* / BodyText fail; BodyLength works ──

    [Fact]
    public void Binary_body_blocks_Body_jsonpath()
    {
        var m = Msg(body: new byte[] { 0x01, 0x02 }, contentType: "application/octet-stream");
        var r = MessageFieldResolver.Resolve(m, "Body.X", BodyFormat.Binary);
        r.Status.Should().Be(ExtractStatus.Failed);
        r.Error.Should().Contain("binary body");
    }

    [Fact]
    public void Binary_body_blocks_BodyXml()
    {
        var m = Msg(body: new byte[] { 0x01, 0x02 }, contentType: "application/octet-stream");
        var r = MessageFieldResolver.Resolve(m, "BodyXml.//X", BodyFormat.Binary);
        r.Status.Should().Be(ExtractStatus.Failed);
        r.Error.Should().Contain("binary body");
    }

    [Fact]
    public void Binary_body_blocks_BodyText()
    {
        var m = Msg(body: new byte[] { 0x01, 0x02 }, contentType: "application/octet-stream");
        var r = MessageFieldResolver.Resolve(m, "BodyText", BodyFormat.Binary);
        r.Status.Should().Be(ExtractStatus.Failed);
        r.Error.Should().Contain("binary body");
    }

    [Fact]
    public void Binary_body_allows_BodyLength()
    {
        var m = Msg(body: new byte[] { 0x01, 0x02, 0x03 }, contentType: "application/octet-stream");
        MessageFieldResolver.Resolve(m, "BodyLength", BodyFormat.Binary)
            .Value.Should().Be("3");
    }

    [Fact]
    public void Empty_field_path_fails()
    {
        var r = MessageFieldResolver.Resolve(Msg(), "", BodyFormat.Text);
        r.Status.Should().Be(ExtractStatus.Failed);
    }

    [Fact]
    public void ApplicationProperties_with_no_name_fails()
    {
        var r = MessageFieldResolver.Resolve(Msg(), "ApplicationProperties.", BodyFormat.Text);
        r.Status.Should().Be(ExtractStatus.Failed);
    }
}
