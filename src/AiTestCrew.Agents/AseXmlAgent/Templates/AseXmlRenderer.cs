using System.Text;
using System.Xml.Linq;
using AiTestCrew.Core.Utilities;

namespace AiTestCrew.Agents.AseXmlAgent.Templates;

/// <summary>
/// Deterministic aseXML render — pure function of (manifest, template body, user values).
/// Applies generators for auto fields, enforces required user fields, substitutes
/// hardwired const values, then token-replaces "{{FieldName}}" in the template.
///
/// The token grammar is shared with <see cref="TokenSubstituter"/> via its
/// <see cref="TokenSubstituter.TokenRx"/> — the renderer keeps its own Replace
/// loop because it must XML-escape substituted values (the general-purpose
/// <see cref="TokenSubstituter.Substitute"/> is XML-agnostic).
/// </summary>
public static class AseXmlRenderer
{
    public record RenderResult(string Xml, Dictionary<string, string> ResolvedFields);

    /// <summary>
    /// Merge user values with manifest field specs and substitute tokens.
    /// Throws <see cref="AseXmlRenderException"/> when required fields are missing,
    /// unknown tokens appear in the body, or the output isn't well-formed XML.
    /// </summary>
    public static RenderResult Render(
        TemplateManifest manifest,
        string templateBody,
        IReadOnlyDictionary<string, string> userValues)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var (name, spec) in manifest.Fields)
        {
            switch (spec.Source?.ToLowerInvariant())
            {
                case "auto":
                    var generated = FieldGenerators.Generate(spec);
                    if (generated is null)
                        throw new AseXmlRenderException(
                            $"Template '{manifest.TemplateId}' field '{name}': unknown generator '{spec.Generator}'.");
                    resolved[name] = generated;
                    break;

                case "const":
                    resolved[name] = spec.Value ?? "";
                    break;

                case "user":
                default:
                    if (userValues.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v))
                    {
                        resolved[name] = v;
                    }
                    else if (spec.Required)
                    {
                        missing.Add(name);
                    }
                    else
                    {
                        // Optional, not supplied → empty string (caller can still see it in resolved).
                        resolved[name] = "";
                    }
                    break;
            }
        }

        if (missing.Count > 0)
        {
            throw new AseXmlRenderException(
                $"Template '{manifest.TemplateId}' is missing required user field(s): {string.Join(", ", missing)}.");
        }

        // Substitute {{tokens}}. Any token without a resolved value is treated as an error —
        // prevents silent malformed output caused by a typo between template and manifest.
        // Uses the shared TokenSubstituter regex but keeps the XML-escape in the Replace
        // callback because substituted values become XML text content.
        var unknownTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rendered = TokenSubstituter.TokenRx.Replace(templateBody, m =>
        {
            var key = m.Groups[1].Value;
            if (resolved.TryGetValue(key, out var value)) return XmlEscape(value);
            unknownTokens.Add(key);
            return m.Value;
        });

        if (unknownTokens.Count > 0)
        {
            throw new AseXmlRenderException(
                $"Template '{manifest.TemplateId}' references undeclared token(s) not in the manifest: " +
                string.Join(", ", unknownTokens) + ".");
        }

        // Well-formedness sanity — parse and throw on failure with a helpful message.
        try
        {
            _ = XDocument.Parse(rendered);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new AseXmlRenderException(
                $"Template '{manifest.TemplateId}' produced malformed XML after substitution: {ex.Message}");
        }

        return new RenderResult(rendered, resolved);
    }

    // Single-pass XML escape — avoids double-escaping ampersands that the chained
    // Replace pattern would produce. User-supplied field values are treated as text
    // content (or attribute values), never as markup, so all five predefined entities
    // are replaced.
    private static string XmlEscape(string v)
    {
        if (string.IsNullOrEmpty(v)) return v;
        var sb = new StringBuilder(v.Length + 8);
        foreach (var c in v)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Raised when a template cannot be rendered (missing required user field,
/// unknown generator, unknown token, or malformed output).
/// </summary>
public class AseXmlRenderException : Exception
{
    public AseXmlRenderException(string message) : base(message) { }
}
