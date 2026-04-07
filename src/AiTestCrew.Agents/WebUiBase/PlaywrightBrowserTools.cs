using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Microsoft.SemanticKernel;

namespace AiTestCrew.Agents.WebUiBase;

/// <summary>
/// Semantic Kernel plugin that gives the LLM live Playwright browser access during
/// test case generation. The LLM navigates, interacts, and observes the real UI
/// before generating test cases — so selectors, URLs, and post-action states are
/// based on direct observation, not guesswork.
///
/// Used only during generation; deterministic replay uses the stored WebUiTestCase JSON.
/// </summary>
public sealed class PlaywrightBrowserTools
{
    private readonly IPage _page;
    private readonly ILogger _logger;
    private readonly string _baseUrl;

    /// <summary>
    /// Every page state recorded by snapshot() — used by the generation phase to
    /// inject authoritative titles and URLs so the LLM cannot hallucinate them.
    /// </summary>
    public record PageObservation(string Url, string Title, string InteractiveElements);

    public List<PageObservation> Observations { get; } = [];

    public PlaywrightBrowserTools(IPage page, ILogger logger, string baseUrl)
    {
        _page    = page;
        _logger  = logger;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    [KernelFunction("snapshot")]
    [Description(
        "Get the current page state: URL, title, all interactive elements with their exact CSS selectors, " +
        "and visible text. Call this after every navigate, click, or fill to see what changed and get " +
        "accurate selectors for the next step.")]
    public async Task<string> SnapshotAsync()
    {
        _logger.LogDebug("[Browser] snapshot → {Url}", _page.Url);
        try
        {
            var title = await _page.TitleAsync();

            var bodyText = await _page.EvaluateAsync<string>("""
                () => {
                    const clone = document.body.cloneNode(true);
                    clone.querySelectorAll('script,style,noscript,svg').forEach(n => n.remove());
                    return (clone.innerText || clone.textContent || '')
                        .replace(/\s{2,}/g, ' ').trim().substring(0, 1500);
                }
                """);

            var elements = await _page.EvaluateAsync<string[]>("""
                () => {
                    function bestSelector(el) {
                        const tag  = el.tagName.toLowerCase();
                        const type = (el.type || '').toLowerCase();
                        const id   = el.id;
                        const name = el.getAttribute('name');
                        const title = el.getAttribute('title');
                        const dynamicIdPattern = /(_active|_selected|_current|_focused|_hover|_open|_collapsed|_expanded|_pb_|_dd_|_wnd_|\d{4,}|^[0-9a-f-]{36}$)/i;
                        if (id && !dynamicIdPattern.test(id)) return `#${id}`;
                        if (name) return `${tag}[name="${name}"]`;
                        if (type === 'submit') return `${tag}[type="submit"]`;
                        if (type === 'button') return `${tag}[type="button"]`;
                        if (type && tag === 'input') return `input[type="${type}"]`;
                        if (title) return `${tag}[title="${title}"]`;
                        const href = el.getAttribute('href');
                        if (href && tag === 'a' && href !== '#' && !href.endsWith('#') && !href.startsWith('javascript:')) {
                            try { return `a[href*="${new URL(href, location.href).pathname}"]`; } catch {}
                        }
                        const role = el.getAttribute('role');
                        if (role && role !== 'menuitem' && role !== 'menu') return `[role="${role}"]`;
                        return tag;
                    }

                    function getLabel(el) {
                        if (el.id) {
                            const lbl = document.querySelector(`label[for="${el.id}"]`);
                            if (lbl) return lbl.textContent.trim().substring(0, 60);
                        }
                        return (
                            el.getAttribute('aria-label') ||
                            el.getAttribute('placeholder') ||
                            el.value ||
                            el.textContent || ''
                        ).trim().substring(0, 60);
                    }

                    return Array.from(document.querySelectorAll(
                        'input,select,textarea,button,a[href],[role="button"],[role="link"],[role="menuitem"]'
                    ))
                    .filter(el => {
                        const r = el.getBoundingClientRect();
                        return r.width > 0 && r.height > 0;
                    })
                    .slice(0, 40)
                    .map(el => `  ${bestSelector(el)}  →  "${getLabel(el) || '(no label)'}"`)
                }
                """);

            var elementList = string.Join("\n", elements ?? []);

            // Record for phase 2 injection — gives the generation prompt authoritative values
            Observations.Add(new PageObservation(_page.Url, title, elementList));

            var sb = new StringBuilder();
            sb.AppendLine($"URL: {_page.Url}");
            sb.AppendLine($"Title: {title}");
            sb.AppendLine();
            sb.AppendLine("Interactive elements (use these EXACT strings as selectors in test steps):");
            sb.AppendLine(elementList);
            sb.AppendLine();
            sb.AppendLine("Visible text:");
            sb.AppendLine(bodyText);

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Snapshot failed: {ex.Message}";
        }
    }

    [KernelFunction("navigate")]
    [Description(
        "Navigate the browser to a URL and wait for the page to fully load. " +
        "Use relative paths (e.g. '/login') or absolute URLs. " +
        "Always call snapshot() afterwards to see the resulting page.")]
    public async Task<string> NavigateAsync(
        [Description("URL or relative path to navigate to (e.g. '/', '/Account/Login')")] string url)
    {
        var resolved = ResolveUrl(url);
        _logger.LogDebug("[Browser] navigate → {Url}", resolved);
        try
        {
            await _page.GotoAsync(resolved,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 20_000 });
            return $"Navigated. Current URL: {_page.Url}. Call snapshot() to see the page.";
        }
        catch (Exception ex)
        {
            return $"Navigation failed: {ex.Message}";
        }
    }

    [KernelFunction("fill")]
    [Description(
        "Type text into an input field or textarea. " +
        "The selector must come from the most recent snapshot() output. " +
        "Always call snapshot() first if you haven't seen the current page.")]
    public async Task<string> FillAsync(
        [Description("CSS selector of the input — copy exactly from snapshot output (e.g. '#UserName', 'input[name=\"Email\"]')")] string selector,
        [Description("Text to type into the field")] string value)
    {
        _logger.LogDebug("[Browser] fill → {Sel}", selector);
        try
        {
            await _page.FillAsync(selector, value, new PageFillOptions { Timeout = 10_000 });
            return $"Filled '{selector}'.";
        }
        catch (Exception ex)
        {
            return $"Fill failed on '{selector}': {ex.Message}. " +
                   "Make sure you copied the selector exactly from the snapshot.";
        }
    }

    [KernelFunction("click")]
    [Description(
        "Click an element. " +
        "The selector must come from the most recent snapshot() output. " +
        "After clicking a submit button or link, call snapshot() to see the resulting page.")]
    public async Task<string> ClickAsync(
        [Description("CSS selector of the element to click — copy exactly from snapshot output")] string selector)
    {
        _logger.LogDebug("[Browser] click → {Sel}", selector);
        try
        {
            await _page.ClickAsync(selector, new PageClickOptions { Timeout = 10_000 });
            // Give the page a moment to start any navigation
            try
            {
                await _page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                    new PageWaitForLoadStateOptions { Timeout = 10_000 });
            }
            catch { /* non-fatal — page may not navigate */ }

            return $"Clicked '{selector}'. Current URL: {_page.Url}. Call snapshot() to see the result.";
        }
        catch (Exception ex)
        {
            return $"Click failed on '{selector}': {ex.Message}. " +
                   "Make sure you copied the selector exactly from the snapshot.";
        }
    }

    private string ResolveUrl(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        return _baseUrl + "/" + url.TrimStart('/');
    }
}
