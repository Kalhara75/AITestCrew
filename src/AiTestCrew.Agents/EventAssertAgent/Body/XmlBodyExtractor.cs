using System.Text;
using System.Xml;
using System.Xml.XPath;

namespace AiTestCrew.Agents.EventAssertAgent.Body;

/// <summary>
/// Resolves <c>BodyXml.&lt;xpath&gt;</c> field paths on a Service Bus
/// message body using <see cref="System.Xml.XPath"/>. Returns the inner
/// text of the first matched node, or the attribute value when the XPath
/// ends in <c>/@attr</c>.
///
/// <para>
/// Default-namespace handling — v1 of REQ-004 expects users to wrap
/// prefixed paths in <c>local-name()</c> filters when documents declare
/// default namespaces (e.g. <c>//*[local-name()='Order']/@Id</c>). A
/// first-class namespace-registry extension is flagged in
/// <c>docs/architecture.md</c> and would slot in via an optional second
/// parameter; this v1 implementation keeps the surface minimal.
/// </para>
/// </summary>
public static class XmlBodyExtractor
{
    public static ExtractResult Extract(byte[] body, string xpath)
    {
        if (body.Length == 0)
            return ExtractResult.Failed($"body is empty — XPath '{xpath}' cannot be evaluated");

        string text;
        try
        {
            text = Encoding.UTF8.GetString(body);
        }
        catch (Exception ex)
        {
            return ExtractResult.Failed($"body is not valid UTF-8: {ex.Message}");
        }

        XPathDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            });
            doc = new XPathDocument(reader);
        }
        catch (XmlException ex)
        {
            return ExtractResult.Failed($"body is not XML: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ExtractResult.Failed($"body could not be parsed as XML: {ex.Message}");
        }

        var nav = doc.CreateNavigator();
        XPathNodeIterator iterator;
        try
        {
            iterator = nav.Select(xpath);
        }
        catch (XPathException ex)
        {
            return ExtractResult.Failed($"XPath '{xpath}' is invalid: {ex.Message}");
        }

        if (!iterator.MoveNext())
            return ExtractResult.Failed($"XPath '{xpath}' not found in body");

        var match = iterator.Current;
        if (match is null)
            return ExtractResult.Failed($"XPath '{xpath}' produced no node");

        // Attribute selector → use the attribute's typed value; element / text /
        // node → use the navigator's Value (text of the node + descendants).
        return ExtractResult.FoundValue(match.Value ?? "");
    }
}
