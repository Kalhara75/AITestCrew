using System.Text.Json;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Utilities;

namespace AiTestCrew.Agents.Environment;

/// <summary>
/// Walks each persisted step-definition type and substitutes every
/// <c>{{Token}}</c> literal using the caller's context dictionary,
/// returning a cloned instance so the persisted object is never mutated.
///
/// Built on top of <see cref="TokenSubstituter"/> (lenient mode) — unknown
/// tokens are left as <c>{{Literal}}</c> and (when a collector is supplied)
/// reported to the caller so the agent can log them as WARN.
///
/// Since Slice 1 of the generalized post-step work, each definition and test
/// case is also walked recursively through its <c>PostSteps</c> list so
/// sub-steps receive the same substitution. <c>AseXmlDelivery</c>'s legacy
/// <c>PostDeliveryVerifications</c> field stays substituted for back-compat.
/// </summary>
public static class StepParameterSubstituter
{
    private static readonly JsonSerializerOptions BodyOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── ApiTestDefinition ──────────────────────────────────────────

    public static ApiTestDefinition Apply(
        ApiTestDefinition source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        if (context.Count == 0) return source;

        return new ApiTestDefinition
        {
            Method = source.Method,
            Endpoint = Sub(source.Endpoint, context, unknownTokens) ?? "",
            Headers = SubDict(source.Headers, context, unknownTokens),
            QueryParams = SubDict(source.QueryParams, context, unknownTokens),
            Body = SubBody(source.Body, context, unknownTokens),
            ExpectedStatus = source.ExpectedStatus,
            ExpectedBodyContains = SubList(source.ExpectedBodyContains, context, unknownTokens),
            ExpectedBodyNotContains = SubList(source.ExpectedBodyNotContains, context, unknownTokens),
            IsFuzzTest = source.IsFuzzTest,
            PostSteps = ApplyPostSteps(source.PostSteps, context, unknownTokens)
        };
    }

    // ── WebUiTestDefinition ────────────────────────────────────────

    public static WebUiTestDefinition Apply(
        WebUiTestDefinition source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        if (context.Count == 0) return source;

        return new WebUiTestDefinition
        {
            Description = source.Description,
            StartUrl = Sub(source.StartUrl, context, unknownTokens) ?? "",
            Steps = source.Steps.Select(s => new WebUiStep
            {
                Action = s.Action,
                Selector = Sub(s.Selector, context, unknownTokens),
                Value = Sub(s.Value, context, unknownTokens),
                TimeoutMs = s.TimeoutMs,
                MatchFirst = s.MatchFirst
            }).ToList(),
            TakeScreenshotOnFailure = source.TakeScreenshotOnFailure,
            PostSteps = ApplyPostSteps(source.PostSteps, context, unknownTokens)
        };
    }

    // ── DesktopUiTestDefinition ────────────────────────────────────

    public static DesktopUiTestDefinition Apply(
        DesktopUiTestDefinition source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        if (context.Count == 0) return source;

        return new DesktopUiTestDefinition
        {
            Description = source.Description,
            Steps = source.Steps.Select(s => new DesktopUiStep
            {
                Action = s.Action,
                AutomationId = Sub(s.AutomationId, context, unknownTokens),
                Name = Sub(s.Name, context, unknownTokens),
                ClassName = Sub(s.ClassName, context, unknownTokens),
                ControlType = Sub(s.ControlType, context, unknownTokens),
                TreePath = Sub(s.TreePath, context, unknownTokens),
                Value = Sub(s.Value, context, unknownTokens),
                MenuPath = Sub(s.MenuPath, context, unknownTokens),
                WindowTitle = Sub(s.WindowTitle, context, unknownTokens),
                TimeoutMs = s.TimeoutMs
            }).ToList(),
            TakeScreenshotOnFailure = source.TakeScreenshotOnFailure,
            PostSteps = ApplyPostSteps(source.PostSteps, context, unknownTokens)
        };
    }

    // ── AseXmlTestDefinition ───────────────────────────────────────

    public static AseXmlTestDefinition Apply(
        AseXmlTestDefinition source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        if (context.Count == 0) return source;

        return new AseXmlTestDefinition
        {
            Description = source.Description,
            TemplateId = source.TemplateId,
            TransactionType = source.TransactionType,
            FieldValues = SubDict(source.FieldValues, context, unknownTokens),
            ValidateAgainstSchema = source.ValidateAgainstSchema,
            PostSteps = ApplyPostSteps(source.PostSteps, context, unknownTokens)
        };
    }

    // ── AseXmlDeliveryTestDefinition ───────────────────────────────

    public static AseXmlDeliveryTestDefinition Apply(
        AseXmlDeliveryTestDefinition source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        if (context.Count == 0) return source;

        return new AseXmlDeliveryTestDefinition
        {
            Description = source.Description,
            TemplateId = source.TemplateId,
            TransactionType = source.TransactionType,
            FieldValues = SubDict(source.FieldValues, context, unknownTokens),
            EndpointCode = Sub(source.EndpointCode, context, unknownTokens) ?? "",
            ValidateAgainstSchema = source.ValidateAgainstSchema,
            PostSteps = source.PostSteps
                .Select(v => Apply(v, context, unknownTokens))
                .ToList()
        };
    }

    // ── Runtime test-case overloads (used by agents in Reuse mode) ─

    public static ApiTestCase Apply(
        ApiTestCase source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        if (context.Count == 0) return source;
        return new ApiTestCase
        {
            Name = source.Name,
            Method = source.Method,
            Endpoint = Sub(source.Endpoint, context, unknownTokens) ?? "",
            Headers = SubDict(source.Headers, context, unknownTokens),
            QueryParams = SubDict(source.QueryParams, context, unknownTokens),
            Body = SubBody(source.Body, context, unknownTokens),
            ExpectedStatus = source.ExpectedStatus,
            ExpectedBodyContains = SubList(source.ExpectedBodyContains, context, unknownTokens),
            ExpectedBodyNotContains = SubList(source.ExpectedBodyNotContains, context, unknownTokens),
            IsFuzzTest = source.IsFuzzTest,
            PostSteps = ApplyPostSteps(source.PostSteps, context, unknownTokens)
        };
    }

    public static WebUiTestCase Apply(
        WebUiTestCase source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        if (context.Count == 0) return source;
        return new WebUiTestCase
        {
            Name = source.Name,
            Description = source.Description,
            StartUrl = Sub(source.StartUrl, context, unknownTokens) ?? "",
            Steps = source.Steps.Select(s => new WebUiStep
            {
                Action = s.Action,
                Selector = Sub(s.Selector, context, unknownTokens),
                Value = Sub(s.Value, context, unknownTokens),
                TimeoutMs = s.TimeoutMs,
                MatchFirst = s.MatchFirst
            }).ToList(),
            TakeScreenshotOnFailure = source.TakeScreenshotOnFailure,
            PostSteps = ApplyPostSteps(source.PostSteps, context, unknownTokens)
        };
    }

    public static DesktopUiTestCase Apply(
        DesktopUiTestCase source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        if (context.Count == 0) return source;
        return new DesktopUiTestCase
        {
            Name = source.Name,
            Description = source.Description,
            Steps = source.Steps.Select(s => new DesktopUiStep
            {
                Action = s.Action,
                AutomationId = Sub(s.AutomationId, context, unknownTokens),
                Name = Sub(s.Name, context, unknownTokens),
                ClassName = Sub(s.ClassName, context, unknownTokens),
                ControlType = Sub(s.ControlType, context, unknownTokens),
                TreePath = Sub(s.TreePath, context, unknownTokens),
                Value = Sub(s.Value, context, unknownTokens),
                MenuPath = Sub(s.MenuPath, context, unknownTokens),
                WindowTitle = Sub(s.WindowTitle, context, unknownTokens),
                TimeoutMs = s.TimeoutMs
            }).ToList(),
            TakeScreenshotOnFailure = source.TakeScreenshotOnFailure,
            PostSteps = ApplyPostSteps(source.PostSteps, context, unknownTokens)
        };
    }

    public static AseXmlTestCase Apply(
        AseXmlTestCase source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        if (context.Count == 0) return source;
        return new AseXmlTestCase
        {
            Name = source.Name,
            Description = source.Description,
            TemplateId = source.TemplateId,
            TransactionType = source.TransactionType,
            FieldValues = SubDict(source.FieldValues, context, unknownTokens),
            ValidateAgainstSchema = source.ValidateAgainstSchema,
            PostSteps = ApplyPostSteps(source.PostSteps, context, unknownTokens)
        };
    }

    public static AseXmlDeliveryTestCase Apply(
        AseXmlDeliveryTestCase source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        if (context.Count == 0) return source;
        return new AseXmlDeliveryTestCase
        {
            Name = source.Name,
            Description = source.Description,
            TemplateId = source.TemplateId,
            TransactionType = source.TransactionType,
            FieldValues = SubDict(source.FieldValues, context, unknownTokens),
            EndpointCode = Sub(source.EndpointCode, context, unknownTokens) ?? "",
            ValidateAgainstSchema = source.ValidateAgainstSchema,
            PostSteps = source.PostSteps
                .Select(v => Apply(v, context, unknownTokens))
                .ToList()
        };
    }

    // ── Convenience: extract EnvironmentParameters from a TestTask ─

    /// <summary>
    /// Extracts the <c>EnvironmentParameters</c> entry (if any) from
    /// <see cref="TestTask.Parameters"/>. Returns an empty dictionary when the
    /// entry is missing or the wrong shape — callers can substitute unconditionally.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadEnvironmentParameters(
        IReadOnlyDictionary<string, object> taskParameters)
    {
        if (taskParameters.TryGetValue("EnvironmentParameters", out var raw))
        {
            if (raw is Dictionary<string, string> d) return d;
            if (raw is IDictionary<string, string> id)
                return new Dictionary<string, string>(id);
        }
        return EmptyParameters;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>();

    // ── VerificationStep / PostStep ────────────────────────────────

    public static VerificationStep Apply(
        VerificationStep source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        return new VerificationStep
        {
            Description = source.Description,
            Target = source.Target,
            WaitBeforeSeconds = source.WaitBeforeSeconds,
            Role = source.Role,
            WebUi = source.WebUi is null ? null : Apply(source.WebUi, context, unknownTokens),
            DesktopUi = source.DesktopUi is null ? null : Apply(source.DesktopUi, context, unknownTokens),
            Api = source.Api is null ? null : Apply(source.Api, context, unknownTokens),
            AseXml = source.AseXml is null ? null : Apply(source.AseXml, context, unknownTokens),
            AseXmlDeliver = source.AseXmlDeliver is null ? null : Apply(source.AseXmlDeliver, context, unknownTokens),
            DbCheck = source.DbCheck is null ? null : Apply(source.DbCheck, context, unknownTokens)
        };
    }

    // ── DbCheckStepDefinition ──────────────────────────────────────

    public static DbCheckStepDefinition Apply(
        DbCheckStepDefinition source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens = null)
    {
        return new DbCheckStepDefinition
        {
            Name = source.Name,
            ConnectionKey = source.ConnectionKey,
            Sql = Sub(source.Sql, context, unknownTokens) ?? "",
            ExpectedRowCount = source.ExpectedRowCount,
            ExpectedColumnValues = SubDict(source.ExpectedColumnValues, context, unknownTokens),
            TimeoutSeconds = source.TimeoutSeconds
        };
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static List<VerificationStep> ApplyPostSteps(
        List<VerificationStep> source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens)
    {
        if (source.Count == 0) return source;
        return source.Select(v => Apply(v, context, unknownTokens)).ToList();
    }

    private static string? Sub(
        string? input,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens)
        => TokenSubstituter.Substitute(input, context, throwOnMissing: false, unknownTokens);

    private static Dictionary<string, string> SubDict(
        Dictionary<string, string> source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens)
    {
        var result = new Dictionary<string, string>(source.Count, source.Comparer);
        foreach (var (k, v) in source)
        {
            var newKey = Sub(k, context, unknownTokens) ?? k;
            var newVal = Sub(v, context, unknownTokens) ?? v;
            result[newKey] = newVal;
        }
        return result;
    }

    private static List<string> SubList(
        List<string> source,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens)
    {
        var result = new List<string>(source.Count);
        foreach (var item in source)
            result.Add(Sub(item, context, unknownTokens) ?? "");
        return result;
    }

    /// <summary>
    /// Substitutes tokens inside a request body. If the body is a plain string,
    /// substitution happens in-place. For JSON-shaped payloads (anonymous objects,
    /// <see cref="JsonElement"/>, etc.), the body is round-tripped through
    /// <see cref="JsonSerializer"/> so <c>{{Token}}</c> literals embedded in any
    /// string field can be replaced. Non-string payloads with no tokens return unchanged.
    /// </summary>
    private static object? SubBody(
        object? body,
        IReadOnlyDictionary<string, string> context,
        ICollection<string>? unknownTokens)
    {
        if (body is null) return null;
        if (body is string s) return Sub(s, context, unknownTokens);

        // Serialise → substitute on the JSON text → deserialise back as JsonElement.
        // Avoids the complexity of walking arbitrary object graphs.
        try
        {
            var json = JsonSerializer.Serialize(body, BodyOpts);
            if (!json.Contains("{{", StringComparison.Ordinal)) return body;

            var substituted = Sub(json, context, unknownTokens) ?? json;
            using var doc = JsonDocument.Parse(substituted);
            return doc.RootElement.Clone();
        }
        catch
        {
            // If we can't round-trip, return the body untouched — better than crashing.
            return body;
        }
    }
}
