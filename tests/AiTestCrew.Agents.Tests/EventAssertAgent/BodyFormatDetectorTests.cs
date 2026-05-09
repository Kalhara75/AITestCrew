using System.Text;
using AiTestCrew.Agents.EventAssertAgent;
using AiTestCrew.Agents.EventAssertAgent.Body;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.EventAssertAgent;

public class BodyFormatDetectorTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Configured_value_other_than_auto_is_honoured()
    {
        BodyFormatDetector.Resolve(BodyFormat.Json, "application/xml", B("<x/>"))
            .Should().Be(BodyFormat.Json);
        BodyFormatDetector.Resolve(BodyFormat.Xml, "application/json", B("{}"))
            .Should().Be(BodyFormat.Xml);
        BodyFormatDetector.Resolve(BodyFormat.Text, null, B("anything"))
            .Should().Be(BodyFormat.Text);
        BodyFormatDetector.Resolve(BodyFormat.Binary, null, B(""))
            .Should().Be(BodyFormat.Binary);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/Json")]                       // mixed-case
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/vnd.api+json")]
    public void Auto_resolves_json_for_json_content_types(string ct)
    {
        BodyFormatDetector.Resolve(BodyFormat.Auto, ct, B("{}")).Should().Be(BodyFormat.Json);
    }

    [Theory]
    [InlineData("application/xml")]
    [InlineData("text/xml")]
    [InlineData("application/Xml")]
    public void Auto_resolves_xml_for_xml_content_types(string ct)
    {
        BodyFormatDetector.Resolve(BodyFormat.Auto, ct, B("<x/>")).Should().Be(BodyFormat.Xml);
    }

    [Fact]
    public void Auto_resolves_binary_for_octet_stream()
    {
        BodyFormatDetector.Resolve(BodyFormat.Auto, "application/octet-stream", new byte[] { 0x00, 0x01 })
            .Should().Be(BodyFormat.Binary);
    }

    [Fact]
    public void Auto_sniffs_json_from_leading_brace()
    {
        BodyFormatDetector.Resolve(BodyFormat.Auto, null, B("  \n{\"x\":1}"))
            .Should().Be(BodyFormat.Json);
    }

    [Fact]
    public void Auto_sniffs_json_from_leading_bracket()
    {
        BodyFormatDetector.Resolve(BodyFormat.Auto, null, B("[1,2]")).Should().Be(BodyFormat.Json);
    }

    [Fact]
    public void Auto_sniffs_xml_from_leading_angle()
    {
        BodyFormatDetector.Resolve(BodyFormat.Auto, null, B("<root/>")).Should().Be(BodyFormat.Xml);
    }

    [Fact]
    public void Auto_falls_back_to_text_for_plain_string()
    {
        BodyFormatDetector.Resolve(BodyFormat.Auto, "text/plain", B("hello world"))
            .Should().Be(BodyFormat.Text);
    }

    [Fact]
    public void Auto_falls_back_to_text_when_no_content_type_and_unknown_byte()
    {
        BodyFormatDetector.Resolve(BodyFormat.Auto, null, B("hello"))
            .Should().Be(BodyFormat.Text);
    }

    [Fact]
    public void Auto_returns_text_for_empty_body_when_no_content_type()
    {
        BodyFormatDetector.Resolve(BodyFormat.Auto, null, Array.Empty<byte>())
            .Should().Be(BodyFormat.Text);
    }
}
