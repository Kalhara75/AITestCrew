using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Configuration;

namespace AiTestCrew.Agents.WebUiBase;

/// <summary>
/// Records a web UI test case by watching the user interact with a live browser.
/// Steps are captured with real CSS selectors from the DOM — no LLM involved.
///
/// Usage:
///   var testCase = await PlaywrightRecorder.RecordAsync(baseUrl, caseName, config, logger, ct);
///
/// The browser opens non-headless. The user performs the scenario, optionally clicks
/// assertion buttons in the overlay panel, then clicks "Save &amp; Stop".
/// The resulting WebUiTestCase is ready for deterministic replay.
/// </summary>
public static class PlaywrightRecorder
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<WebUiTestCase> RecordAsync(
        string baseUrl,
        string caseName,
        TestEnvironmentConfig config,
        ILogger logger,
        CancellationToken ct = default)
    {
        baseUrl = baseUrl.TrimEnd('/');

        var steps = new List<WebUiStep>();
        var stopSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        logger.LogInformation("[Recorder] Starting recording session for '{Name}' at {Url}", caseName, baseUrl);

        using var playwright = await Playwright.CreateAsync();

        // Always non-headless — user must see the browser
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            SlowMo = 50   // Slight slow-down makes interactions easier to record accurately
        });

        try
        {
            var context = await browser.NewContextAsync(new BrowserNewContextOptions());
            var page    = await context.NewPageAsync();

            // ── Expose functions (survive page navigation) ──────────────────────────────
            // aitcRecordStep receives a JSON string from JS (object serialisation is
            // simpler than relying on Playwright's .NET generic overload variance).
            await page.ExposeFunctionAsync<string, bool>("aitcRecordStep", json =>
            {
                try
                {
                    var step = JsonSerializer.Deserialize<WebUiStep>(json, _jsonOpts);
                    if (step is not null)
                    {
                        // Deduplicate fills: update the value if the same selector already exists
                        if (step.Action == "fill")
                        {
                            var existing = steps.FindLastIndex(
                                s => s.Action == "fill" && s.Selector == step.Selector);
                            if (existing >= 0)
                            {
                                steps[existing] = step;
                                logger.LogDebug("[Recorder] Updated fill: {Sel}", step.Selector);
                                return true;
                            }
                        }
                        steps.Add(step);
                        logger.LogDebug("[Recorder] Captured: {Action} {Sel} = {Val}",
                            step.Action, step.Selector, step.Value);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning("[Recorder] Failed to parse step JSON: {Err}", ex.Message);
                }
                return true;
            });

            await page.ExposeFunctionAsync<bool>("aitcStopRecording", () =>
            {
                stopSignal.TrySetResult(true);
                return true;
            });

            // ── Init script (runs on every page load) ──────────────────────────────────
            await page.AddInitScriptAsync(BuildInitScript());

            // ── Navigate to start ──────────────────────────────────────────────────────
            await page.GotoAsync(baseUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });

            logger.LogInformation("[Recorder] Browser open. Perform the test scenario, then click 'Save & Stop' in the overlay.");
            Console.WriteLine();
            Console.WriteLine("  ┌─────────────────────────────────────────────────────┐");
            Console.WriteLine("  │  Browser is open. Perform your test scenario.        │");
            Console.WriteLine("  │  Use the overlay buttons to add assertions.          │");
            Console.WriteLine("  │  Click  Save & Stop  when done.                      │");
            Console.WriteLine("  └─────────────────────────────────────────────────────┘");
            Console.WriteLine();

            // ── Wait for stop signal or timeout ───────────────────────────────────────
            browser.Disconnected += (_, _) => stopSignal.TrySetResult(true);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(15));

            await Task.WhenAny(
                stopSignal.Task,
                Task.Delay(Timeout.Infinite, timeoutCts.Token).ContinueWith(_ => { }));

            logger.LogInformation("[Recorder] Recording stopped. {Count} steps captured.", steps.Count);
        }
        finally
        {
            if (!browser.IsConnected) { /* already closed */ }
            else await browser.CloseAsync();
        }

        return new WebUiTestCase
        {
            Name        = caseName,
            Description = $"Recorded test case: {caseName}",
            StartUrl    = "/",
            Steps       = steps,
            TakeScreenshotOnFailure = true
        };
    }

    // ── Recording JavaScript ───────────────────────────────────────────────────────────

    private static string BuildInitScript() => """
        (function () {
            if (window.__aitcRecorderActive) return;
            window.__aitcRecorderActive = true;

            // ── Selector builder (same logic as PlaywrightBrowserTools.cs) ──
            function bestSelector(el) {
                if (!el || !el.tagName) return null;
                const tag  = el.tagName.toLowerCase();
                const id   = el.id;
                const name = el.getAttribute('name');
                const type = (el.getAttribute('type') || '').toLowerCase();
                if (id)   return '#' + id;
                if (name) return tag + '[name="' + name + '"]';
                if (type === 'submit') return tag + '[type="submit"]';
                if (type === 'button') return tag + '[type="button"]';
                if (type && tag === 'input') return 'input[type="' + type + '"]';
                if (tag === 'a') {
                    const href = el.getAttribute('href');
                    if (href) {
                        try { return 'a[href="' + new URL(href, location.href).pathname + '"]'; }
                        catch {}
                    }
                }
                return tag;
            }

            function isOverlayEl(el) {
                return el && (el.closest('#__aitc_overlay') !== null);
            }

            function send(step) {
                if (typeof window.aitcRecordStep === 'function') {
                    window.aitcRecordStep(JSON.stringify(step));
                }
            }

            // ── Capture fills on change (fires when user leaves the field) ──
            document.addEventListener('change', function (e) {
                const el = e.target;
                if (!el || isOverlayEl(el)) return;
                const tag = el.tagName.toLowerCase();
                if (!['input', 'textarea', 'select'].includes(tag)) return;
                const sel = bestSelector(el);
                if (!sel) return;
                send({ action: 'fill', selector: sel, value: el.value || '', timeoutMs: 5000 });
            }, true);

            // ── Capture clicks on interactive elements ──
            document.addEventListener('click', function (e) {
                const el = e.target.closest(
                    'button, a, input[type="submit"], input[type="button"], [role="button"]'
                );
                if (!el || isOverlayEl(el)) return;
                const sel = bestSelector(el);
                if (!sel) return;
                // 15s timeout — clicks often trigger form submissions and page navigation
                send({ action: 'click', selector: sel, value: null, timeoutMs: 15000 });
            }, true);

            // ── Inject overlay panel (deferred until body is ready) ──
            function injectOverlay() {
                if (!document.body || document.getElementById('__aitc_overlay')) return;
                const panel = document.createElement('div');
                panel.id = '__aitc_overlay';
                panel.style.cssText =
                    'position:fixed;bottom:20px;right:20px;z-index:2147483647;' +
                    'background:#1e1e2e;color:#cdd6f4;padding:14px 16px;border-radius:10px;' +
                    'font-family:monospace;font-size:13px;line-height:1.5;' +
                    'box-shadow:0 6px 28px rgba(0,0,0,0.55);min-width:230px;user-select:none;';

                panel.innerHTML =
                    '<div style="color:#a6e3a1;font-weight:bold;margin-bottom:10px;">&#9679; AITestCrew Recording</div>' +

                    '<button onclick="(function(){' +
                        'const j=JSON.stringify({action:\'assert-url-contains\',selector:null,value:location.pathname,timeoutMs:5000});' +
                        'window.aitcRecordStep(j);' +
                        'this.textContent=\'+ Assert current URL (\'+location.pathname+\') ✓\';' +
                    '})(this)" style="display:block;width:100%;margin-bottom:4px;padding:5px 8px;' +
                    'background:#313244;color:#cdd6f4;border:1px solid #45475a;border-radius:5px;cursor:pointer;">' +
                    '+ Assert current URL (' + location.pathname + ')</button>' +

                    '<button onclick="(function(){' +
                        'const j=JSON.stringify({action:\'assert-title-contains\',selector:null,value:document.title,timeoutMs:5000});' +
                        'window.aitcRecordStep(j);' +
                        'this.textContent=\'+ Assert title: \'+document.title+\' ✓\';' +
                    '})(this)" style="display:block;width:100%;margin-bottom:12px;padding:5px 8px;' +
                    'background:#313244;color:#cdd6f4;border:1px solid #45475a;border-radius:5px;cursor:pointer;">' +
                    '+ Assert page title (' + document.title + ')</button>' +

                    '<button onclick="window.aitcStopRecording()" ' +
                    'style="display:block;width:100%;padding:7px;' +
                    'background:#a6e3a1;color:#1e1e2e;border:none;border-radius:5px;cursor:pointer;font-weight:bold;">' +
                    'Save &amp; Stop</button>';

                document.body.appendChild(panel);
            }

            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', injectOverlay);
            } else {
                injectOverlay();
            }
        })();
        """;
}
