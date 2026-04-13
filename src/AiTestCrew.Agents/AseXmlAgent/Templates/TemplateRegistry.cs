using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AiTestCrew.Agents.AseXmlAgent.Templates;

/// <summary>
/// Discovers and caches aseXML templates at startup. Scans the templates
/// root for "{templateId}.xml" + "{templateId}.manifest.json" pairs (organised
/// by subfolder per transaction type).
///
/// The cache is immutable after <see cref="LoadFrom(string, ILogger{TemplateRegistry})"/> —
/// templates are loaded once at process start (no hot-reload in Phase 1).
/// </summary>
public class TemplateRegistry
{
    private readonly Dictionary<string, LoadedTemplate> _byId;
    private readonly string _templatesPath;

    public string TemplatesPath => _templatesPath;

    private TemplateRegistry(string templatesPath, Dictionary<string, LoadedTemplate> byId)
    {
        _templatesPath = templatesPath;
        _byId = byId;
    }

    /// <summary>Load all template pairs under the given root.</summary>
    public static TemplateRegistry LoadFrom(string templatesPath, ILogger<TemplateRegistry> logger)
    {
        var absolute = Path.IsPathRooted(templatesPath)
            ? templatesPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, templatesPath));

        var byId = new Dictionary<string, LoadedTemplate>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(absolute))
        {
            logger.LogInformation("aseXML templates directory does not exist yet ({Path}) — registry is empty.", absolute);
            return new TemplateRegistry(absolute, byId);
        }

        var manifestFiles = Directory.GetFiles(absolute, "*.manifest.json", SearchOption.AllDirectories);
        foreach (var manifestFile in manifestFiles)
        {
            try
            {
                var json = File.ReadAllText(manifestFile);
                var manifest = JsonSerializer.Deserialize<TemplateManifest>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.TemplateId))
                {
                    logger.LogWarning("aseXML manifest at {Path} could not be parsed or has no templateId — skipping.", manifestFile);
                    continue;
                }

                var bodyPath = Path.Combine(
                    Path.GetDirectoryName(manifestFile)!,
                    manifest.TemplateId + ".xml");

                if (!File.Exists(bodyPath))
                {
                    logger.LogWarning("aseXML template body not found for '{Id}' (expected {Path}) — skipping.",
                        manifest.TemplateId, bodyPath);
                    continue;
                }

                manifest.BodyPath = bodyPath;
                var body = File.ReadAllText(bodyPath);
                byId[manifest.TemplateId] = new LoadedTemplate(manifest, body);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load aseXML manifest at {Path}", manifestFile);
            }
        }

        logger.LogInformation("aseXML template registry loaded {Count} template(s) from {Path}",
            byId.Count, absolute);
        return new TemplateRegistry(absolute, byId);
    }

    /// <summary>Return all loaded templates.</summary>
    public IReadOnlyCollection<LoadedTemplate> All() => _byId.Values;

    /// <summary>Look up by template id (case-insensitive). Returns null if not found.</summary>
    public LoadedTemplate? Get(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return null;
        return _byId.TryGetValue(templateId, out var t) ? t : null;
    }

    /// <summary>Find all templates for a given transaction type.</summary>
    public IEnumerable<LoadedTemplate> Find(string transactionType) =>
        _byId.Values.Where(t =>
            string.Equals(t.Manifest.TransactionType, transactionType, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A manifest plus the raw template body, loaded once into memory.</summary>
public record LoadedTemplate(TemplateManifest Manifest, string Body);
