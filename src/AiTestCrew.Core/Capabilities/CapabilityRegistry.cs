namespace AiTestCrew.Core.Capabilities;

/// <summary>
/// Single source of truth for what AITestCrew can test today.
/// Used as LLM system-prompt context in the Xray importer and surfaced via GET /api/capabilities.
/// </summary>
public static class CapabilityRegistry
{
    private const string MarkdownText =
        """
        # AITestCrew Capability Registry

        This is the LLM ground truth when mapping Xray test steps to AITestCrew constructs.
        If no entry below covers the step, mark it UNSUPPORTED and suggest a gap REQ.

        ## Supported Top-Level Step Types

        ### ApiStep (API_REST or API_GraphQL)
        HTTP GET/POST/PUT/DELETE/PATCH. JSON body, headers, auth injected per stack/env.
        Response assertions: status code, JSON body (JSONPath), response header, raw body.
        Captures: store response fields as {{tokens}} for downstream steps.

        ### WebUiStep UI_Web_MVC (Bravo Web)
        Playwright on Kendo UI / ASP.NET MVC. Click, type, select from Kendo dropdowns, navigate,
        submit forms. Text-present and element-visible assertions. Recording via --record flag.

        ### WebUiStep UI_Web_Blazor (Brave Cloud)
        Playwright on MudBlazor / Blazor Server. Azure AD SSO with optional TOTP MFA.
        MudNavLink, MudTextField, MudSelect, MudButton selectors. Recording via --record flag.

        ### DesktopUiStep UI_Desktop_WinForms
        FlaUI / UI Automation on WinForms. Click, type, menu navigation. Assertions:
        assert-text (UIA + OCR fallback), assert-text-ocr (force OCR), assert-count (descendant count).

        ### AseXmlStep (Asexml_Generate)
        AEMO B2B XML template rendering. Token substitution + auto-generated fields (GUIDs, timestamps, counters).

        ### AseXmlDeliveryStep (Asexml_Deliver)
        SFTP/FTP/AS2 delivery of rendered aseXML + post-delivery UI verifications with deferred scheduling.

        ## Supported Post-Step Types

        ### DB Assert (Db_SqlServer)
        SELECT query against SQL Server + column assertions + captures.
        Operators: Equals, NotEquals, Contains, NotContains, GreaterThan, LessThan, IsNull, IsNotNull, Matches (regex).
        Captures store column values as {{tokens}}. SELECT-only enforced by guardrails.

        ### Event Assert (Event_AzureServiceBus)
        Receive messages from Azure Service Bus queue or subscription.
        Match modes: Any, All, ExactlyOne, MinCount, MaxCount.
        Body formats: JSON, XML, PlainText (auto-detected). Captures matched fields.

        ### API post-step (API_REST)
        Chained HTTP request + assertions + captures after a parent step.

        ### aseXML post-delivery verification
        Recorded UI step (any target) run after delivery with {{token}} substitution and optional deferred scheduling.

        ## Assertion Operators
        Equals, NotEquals, Contains, NotContains, GreaterThan, LessThan, IsNull, IsNotNull, Matches (regex)

        ## Event Match Modes
        Any, All, ExactlyOne, MinCount, MaxCount

        ## NOT Supported Today (generate a gap REQ for these)
        - PDF content inspection (no primitive; would need a PdfContentAssert post-step)
        - Excel / CSV file diff (no file-comparison primitive)
        - Image comparison or visual regression (no visual agent)
        - Negative UI assertions: element should NOT be visible / button should be ABSENT
          Note: no first-class absent-element assertion exists for Web or Desktop steps.
        - SAP GUI or non-Playwright/FlaUI desktop frameworks
        - Avro or Protobuf message body formats (Service Bus supports JSON, XML, PlainText only)
        - Two-way Xray write-back (execution results not pushed back to Jira)
        - Bulk Xray import by JQL or test plan (only individual test cases supported)
        """;

    /// <summary>Returns the full capability registry as LLM-readable markdown.</summary>
    public static string GetMarkdown() => MarkdownText;

    /// <summary>Returns a structured DTO for the GET /api/capabilities endpoint.</summary>
    public static CapabilityRegistryDto GetDto() => new()
    {
        StepTypes =
        [
            "ApiStep (API_REST / API_GraphQL) - HTTP GET/POST/PUT/DELETE/PATCH with JSON body, auth, assertions, captures",
            "WebUiStep UI_Web_MVC (Bravo Web) - Playwright + Kendo UI, recording-based",
            "WebUiStep UI_Web_Blazor (Brave Cloud) - Playwright + MudBlazor, recording-based",
            "DesktopUiStep UI_Desktop_WinForms - FlaUI Windows UI Automation, recording-based",
            "AseXmlStep (Asexml_Generate) - AEMO B2B XML template rendering",
            "AseXmlDeliveryStep (Asexml_Deliver) - SFTP/FTP delivery + post-delivery verifications",
        ],
        PostStepTypes =
        [
            "DB Assert (Db_SqlServer) - SELECT query + column assertions + captures",
            "Event Assert (Event_AzureServiceBus) - receive + criteria match + captures",
            "API post-step (API_REST) - chained HTTP request + assertions + captures",
            "aseXML post-delivery verification - recorded UI step run after delivery",
        ],
        AssertionPrimitives =
        [
            "Equals, NotEquals, Contains, NotContains, GreaterThan, LessThan, IsNull, IsNotNull, Matches (regex)",
            "Match modes: Any, All, ExactlyOne, MinCount, MaxCount",
            "Desktop: assert-text, assert-text-ocr, assert-count",
            "Status code assertion on API steps",
            "JSON body path assertion on API and DB steps",
        ],
        UnsupportedExamples =
        [
            "PDF content inspection",
            "Excel / CSV file diff",
            "Image / visual regression comparison",
            "Negative UI assertions (element absent / button not visible)",
            "SAP GUI or non-standard desktop frameworks",
            "Avro or Protobuf message formats",
        ],
    };
}
