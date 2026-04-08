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
/// The browser opens non-headless and maximized. The user performs the scenario,
/// optionally clicks assertion buttons in the overlay panel, then clicks "Save &amp; Stop".
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

        // Always non-headless and maximized — user must see the browser at full size
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            SlowMo = 50,
            Args = ["--start-maximized"]
        });

        try
        {
            // NoViewport lets the page use the actual maximized window size
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = ViewportSize.NoViewport
            });
            var page = await context.NewPageAsync();

            // ── Expose functions (survive page navigation) ──────────────────────────────
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

            // ── Element assertion pick mode state ────────────────────────────────
            var __pickMode = false;
            var __pickedEl = null;
            var __highlightDiv = null;
            var __pickMenuDiv = null;
            // Expose toggle on window so inline onclick can reach it
            window.__aitcTogglePick = function() {}; // placeholder, set after functions defined

            // ── Selector builder ──────────────────────────────────────────────────
            // Builds the most specific, replay-stable CSS/Playwright selector.
            // Tailored for Kendo UI (PanelBar, Window, Grid) + Bootstrap stack.
            function bestSelector(el) {
                if (!el || !el.tagName) return null;
                const tag  = el.tagName.toLowerCase();
                const id   = el.id;
                const name = el.getAttribute('name');
                const type = (el.getAttribute('type') || '').toLowerCase();
                const title = el.getAttribute('title');

                // 1. Stable IDs (skip dynamic/stateful Kendo IDs like _pb_active, _dd_123)
                const dynamicIdPattern = /(_active|_selected|_current|_focused|_hover|_open|_collapsed|_expanded|_pb_|_dd_|_wnd_|\d{4,}|^[0-9a-f-]{36}$)/i;
                if (id && !dynamicIdPattern.test(id)) return '#' + id;

                // 2. name attribute (form fields)
                if (name) return tag + '[name="' + name + '"]';

                // 3. type-based (submit/button inputs)
                if (type === 'submit') return tag + '[type="submit"]';
                if (type === 'button') return tag + '[type="button"]';
                if (type && tag === 'input') return 'input[type="' + type + '"]';

                // 4. title attribute — Kendo PanelBar child items have title="NMI Search" etc.
                //    This is highly stable and unique within a menu.
                if (title) return tag + '[title="' + title + '"]';

                // 5. Links — use pathname + query string as a *contains* selector
                //    so grid links (where only the query string differs) get a
                //    unique selector while still matching absolute hrefs.
                //    Skip links inside Kendo Grids — their href is set dynamically
                //    by a mousedown handler and won't match during replay.
                //    Fall through to text-based selection for those.
                if (tag === 'a' && !el.closest('.k-grid')) {
                    const href = el.getAttribute('href');
                    if (href && href !== '#' && !href.startsWith('javascript:') && !href.startsWith('#')) {
                        try {
                            const url = new URL(href, location.href);
                            const pathWithQuery = url.pathname + url.search;
                            return 'a[href*="' + pathWithQuery + '"]';
                        } catch {}
                    }
                }

                // 6. data-* attributes (common in UI frameworks)
                for (const attr of ['data-id', 'data-name', 'data-value', 'data-key', 'data-feature', 'data-role']) {
                    const val = el.getAttribute(attr);
                    if (val) return tag + '[' + attr + '="' + val + '"]';
                }

                // 7. ARIA role (skip generic "menuitem" — too many in Kendo PanelBar)
                const role = el.getAttribute('role');
                if (role && role !== 'menuitem' && role !== 'menu' && role !== 'group') {
                    return tag + '[role="' + role + '"]';
                }

                // 8. Distinctive class names (skip utility/state/Kendo-state classes)
                const stateClassPattern = /^(active|selected|open|closed|hover|focus|show|hide|in|out|k-state-|k-first|k-last|k-item|k-link|k-header|k-group|k-panel|ng-|is-|has-|js-)/;
                const classes = Array.from(el.classList || []).filter(c => !stateClassPattern.test(c));
                if (classes.length > 0) {
                    const cls = CSS.escape(classes[0]);
                    if (document.querySelectorAll(tag + '.' + cls).length === 1) {
                        return tag + '.' + cls;
                    }
                }

                // 9. Text-based selector (own text nodes only — avoids grabbing all child text)
                const ownText = Array.from(el.childNodes)
                    .filter(n => n.nodeType === 3) // TEXT_NODE
                    .map(n => n.textContent.trim())
                    .join(' ')
                    .trim();
                if (ownText && ownText.length > 1 && ownText.length <= 50) {
                    return 'text="' + ownText + '"';
                }

                // 10. innerText fallback (only if short — not a tree group with children)
                const inner = (el.innerText || '').trim();
                if (inner && inner.length > 1 && inner.length <= 50 && !inner.includes('\n')) {
                    return 'text="' + inner + '"';
                }

                return tag;
            }

            // For Kendo PanelBar group headers: find the <span> that holds
            // just the group name text (e.g. "Standing Data"), not the parent
            // <li> whose innerText includes all children.
            function bestSelectorForPanelBarHeader(target) {
                // In Kendo PanelBar, group headers are:
                //   <li role="menuitem">
                //     <span class="k-link k-header">
                //       <img ...>
                //       <span>Standing Data</span>   ← we want THIS
                //       <span class="k-icon ..."></span>
                //     </span>
                //     <ul class="k-group">...</ul>
                //   </li>
                //
                // The click target may be the inner <span>, the <img>, the k-icon,
                // or the k-link itself. We want the <span> with the text.

                // Walk up to the k-link header
                const kLink = target.closest('.k-link.k-header') || target.closest('.k-link');
                if (!kLink) return null;

                // Find the text-bearing <span> child (not the k-icon)
                const textSpan = kLink.querySelector('span:not(.k-icon):not(.k-image)');
                if (textSpan) {
                    const text = textSpan.textContent.trim();
                    if (text && text.length <= 50) {
                        return 'text="' + text + '"';
                    }
                }

                return null;
            }

            function isOverlayEl(el) {
                return el && (
                    el.closest('#__aitc_overlay') !== null ||
                    el.closest('#__aitc_pick_highlight') !== null ||
                    el.closest('#__aitc_pick_menu') !== null
                );
            }

            function isFormField(el) {
                if (!el) return false;
                const tag = el.tagName.toLowerCase();
                return tag === 'input' || tag === 'textarea' || tag === 'select';
            }

            function send(step) {
                if (typeof window.aitcRecordStep === 'function') {
                    window.aitcRecordStep(JSON.stringify(step));
                }
            }

            // ── Element assertion pick mode ──────────────────────────────────────

            function enterPickMode() {
                __pickMode = true;
                document.body.style.cursor = 'crosshair';
                var btn = document.getElementById('__aitc_pick_btn');
                if (btn) {
                    btn.textContent = 'Cancel pick mode';
                    btn.style.background = '#45475a';
                }
                document.addEventListener('mousemove', onPickMouseMove, true);
            }

            function exitPickMode() {
                __pickMode = false;
                __pickedEl = null;
                document.body.style.cursor = '';
                if (__highlightDiv) __highlightDiv.style.display = 'none';
                if (__pickMenuDiv) __pickMenuDiv.style.display = 'none';
                var btn = document.getElementById('__aitc_pick_btn');
                if (btn) {
                    btn.textContent = '+ Assert element\u2026';
                    btn.style.background = '#313244';
                }
                document.removeEventListener('mousemove', onPickMouseMove, true);
            }

            function onPickMouseMove(e) {
                if (!__highlightDiv) return;
                var el = document.elementFromPoint(e.clientX, e.clientY);
                if (!el || isOverlayEl(el)) {
                    __highlightDiv.style.display = 'none';
                    return;
                }
                var rect = el.getBoundingClientRect();
                __highlightDiv.style.left   = rect.left + 'px';
                __highlightDiv.style.top    = rect.top + 'px';
                __highlightDiv.style.width  = rect.width + 'px';
                __highlightDiv.style.height = rect.height + 'px';
                __highlightDiv.style.display = 'block';
            }

            function showPickMenu(el) {
                if (!__pickMenuDiv) return;
                __pickedEl = el;
                var sel = bestSelector(el);
                var isField = isFormField(el);
                var textVal = (el.innerText || '').trim();
                if (textVal.length > 100) textVal = textVal.substring(0, 100) + '\u2026';

                var weakSelector = !sel || sel === 'div' || sel === 'span' || sel === 'li' || sel === 'img' || sel === 'ul';

                var html = '<div style="padding:6px 12px 4px;color:#a6e3a1;font-size:11px;border-bottom:1px solid #45475a;margin-bottom:4px;">' +
                    (weakSelector ? '<span style="color:#f9e2af;">&#9888; Selector may not be unique</span><br>' : '') +
                    (sel || 'unknown') + '</div>';

                var menuBtnStyle = 'display:block;width:100%;text-align:left;padding:6px 12px;' +
                    'background:transparent;color:#cdd6f4;border:none;cursor:pointer;font-family:monospace;font-size:12px;';

                html += '<button onmouseover="this.style.background=\'#313244\'" onmouseout="this.style.background=\'transparent\'" ' +
                    'style="' + menuBtnStyle + '" data-assert="assert-text">Assert text contains</button>';

                if (isField) {
                    html += '<button onmouseover="this.style.background=\'#313244\'" onmouseout="this.style.background=\'transparent\'" ' +
                        'style="' + menuBtnStyle + '" data-assert="assert-value">Assert value equals</button>';
                }

                html += '<button onmouseover="this.style.background=\'#313244\'" onmouseout="this.style.background=\'transparent\'" ' +
                    'style="' + menuBtnStyle + '" data-assert="assert-visible">Assert is visible</button>';

                html += '<button onmouseover="this.style.background=\'#313244\'" onmouseout="this.style.background=\'transparent\'" ' +
                    'style="' + menuBtnStyle + '" data-assert="assert-hidden">Assert is hidden</button>';

                html += '<div style="border-top:1px solid #45475a;margin-top:4px;padding-top:4px;">' +
                    '<button onmouseover="this.style.background=\'#313244\'" onmouseout="this.style.background=\'transparent\'" ' +
                    'style="' + menuBtnStyle + 'color:#f38ba8;" data-assert="cancel">Cancel</button></div>';

                __pickMenuDiv.innerHTML = html;

                // Position near the picked element
                var rect = el.getBoundingClientRect();
                var menuX = Math.min(rect.right + 8, window.innerWidth - 260);
                var menuY = Math.min(rect.top, window.innerHeight - 200);
                if (menuX < 0) menuX = 8;
                if (menuY < 0) menuY = 8;
                __pickMenuDiv.style.left = menuX + 'px';
                __pickMenuDiv.style.top  = menuY + 'px';
                __pickMenuDiv.style.display = 'block';

                // Attach click handlers to menu buttons
                __pickMenuDiv.querySelectorAll('button[data-assert]').forEach(function(btn) {
                    btn.addEventListener('click', function(ev) {
                        ev.stopPropagation();
                        var assertType = btn.getAttribute('data-assert');
                        if (assertType === 'cancel') { exitPickMode(); return; }
                        if (assertType === 'assert-value') {
                            recordPickAssertion('assert-text', sel, el.value || '');
                        } else if (assertType === 'assert-text') {
                            recordPickAssertion('assert-text', sel, textVal);
                        } else {
                            recordPickAssertion(assertType, sel, null);
                        }
                    });
                });
            }

            function recordPickAssertion(action, selector, value) {
                send({ action: action, selector: selector || null, value: value, timeoutMs: 5000 });
                // Show confirmation in overlay
                var toast = document.getElementById('__aitc_pick_toast');
                if (toast) {
                    var label = action + (selector ? ' ' + selector : '');
                    if (label.length > 50) label = label.substring(0, 50) + '\u2026';
                    toast.textContent = '\u2713 ' + label;
                    toast.style.display = 'block';
                    setTimeout(function() { toast.style.display = 'none'; }, 2000);
                }
                exitPickMode();
            }

            window.__aitcTogglePick = function() {
                if (__pickMode) exitPickMode(); else enterPickMode();
            };

            // ── Capture fills via input event (fires on every keystroke) ──
            // Using 'input' instead of 'change' ensures fill values are recorded AS the
            // user types, not on blur. This guarantees fills appear before any subsequent
            // click (e.g. submit) — the change/blur event ordering with click is unreliable
            // across browsers. Deduplication on the .NET side keeps only the final value.
            document.addEventListener('input', function (e) {
                if (__pickMode) return;
                const el = e.target;
                if (!el || isOverlayEl(el)) return;
                if (!isFormField(el)) return;
                const sel = bestSelector(el);
                if (!sel) return;
                send({ action: 'fill', selector: sel, value: el.value || '', timeoutMs: 5000 });
            }, true);

            // ── Capture select changes (input event doesn't cover <select>) ──
            document.addEventListener('change', function (e) {
                if (__pickMode) return;
                const el = e.target;
                if (!el || isOverlayEl(el)) return;
                if (el.tagName.toLowerCase() !== 'select') return;
                const sel = bestSelector(el);
                if (!sel) return;
                send({ action: 'fill', selector: sel, value: el.value || '', timeoutMs: 5000 });
            }, true);

            // ── Capture clicks ──
            // Uses click (not mousedown) to preserve correct ordering with change events:
            // when a user fills a field then clicks a button, change fires on blur BEFORE
            // click fires, so fill is recorded before the click. mousedown would reverse this.
            document.addEventListener('click', function (e) {
                const target = e.target;
                if (!target || isOverlayEl(target)) return;

                // ── Pick mode: intercept click to select element for assertion ──
                if (__pickMode) {
                    e.preventDefault();
                    e.stopImmediatePropagation();
                    if (__highlightDiv) __highlightDiv.style.display = 'none';
                    showPickMenu(target);
                    return;
                }

                // Skip focus-clicks on form fields — fill events handle these
                if (isFormField(target)) return;

                // ── Special case: Kendo PanelBar group header (expand/collapse) ──
                // These are <span class="k-link k-header"> containing <span>GroupName</span>.
                // They don't have <a> or <button> — just spans inside an <li>.
                const panelSel = bestSelectorForPanelBarHeader(target);
                if (panelSel) {
                    send({ action: 'click', selector: panelSel, value: null, timeoutMs: 15000 });
                    return;
                }

                // ── Special case: Kendo Window / modal close button ──
                const kWindowAction = target.closest('.k-window-action, .k-dialog-close');
                if (kWindowAction) {
                    const kWindow = kWindowAction.closest('.k-window');
                    const titleEl = kWindow && kWindow.querySelector('.k-window-title');
                    const title = titleEl ? titleEl.textContent.trim() : '';
                    // Record as a press Escape — simpler and more reliable for replay
                    send({ action: 'press', selector: null, value: 'Escape', timeoutMs: 5000 });
                    return;
                }

                // ── General click handling ──
                // Walk up from target to find the nearest meaningful interactive element.
                // Skip <a href="#"> / <a href="...#"> — Kendo uses these as widget wrappers.
                let el = null;
                let cursor = target;
                while (cursor && cursor !== document.body) {
                    const ctag = cursor.tagName.toLowerCase();
                    if (ctag === 'button' || ctag === 'input') {
                        el = cursor;
                        break;
                    }
                    if (ctag === 'a') {
                        const href = cursor.getAttribute('href');
                        // Only use <a> if it has a real navigation href (not # or ?m=Y#)
                        if (href && !href.endsWith('#') && !href.startsWith('javascript:')) {
                            el = cursor;
                            break;
                        }
                        // Skip this <a> — it's a Kendo widget wrapper, not a real link
                    }
                    cursor = cursor.parentElement;
                }

                // Fallback to the actual clicked element if no interactive ancestor found
                if (!el) el = target;

                if (isOverlayEl(el)) return;
                const sel = bestSelector(el);
                if (!sel || sel === 'div' || sel === 'span' || sel === 'li' || sel === 'img' || sel === 'ul') return;
                send({ action: 'click', selector: sel, value: null, timeoutMs: 15000 });
            }, true);

            // ── Capture Escape key (dismisses Kendo Windows / modals) ──
            document.addEventListener('keydown', function (e) {
                if (e.key === 'Escape') {
                    if (__pickMode) { exitPickMode(); return; }
                    send({ action: 'press', selector: null, value: 'Escape', timeoutMs: 5000 });
                }
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

                    '<button onclick="(function(btn){' +
                        'const j=JSON.stringify({action:\'assert-url-contains\',selector:null,value:location.pathname,timeoutMs:5000});' +
                        'window.aitcRecordStep(j);' +
                        'btn.textContent=\'+ Assert current URL (\'+location.pathname+\') \u2713\';' +
                    '})(this)" style="display:block;width:100%;margin-bottom:4px;padding:5px 8px;' +
                    'background:#313244;color:#cdd6f4;border:1px solid #45475a;border-radius:5px;cursor:pointer;">' +
                    '+ Assert current URL (' + location.pathname + ')</button>' +

                    '<button onclick="(function(btn){' +
                        'const j=JSON.stringify({action:\'assert-title-contains\',selector:null,value:document.title,timeoutMs:5000});' +
                        'window.aitcRecordStep(j);' +
                        'btn.textContent=\'+ Assert title: \'+document.title+\' \u2713\';' +
                    '})(this)" style="display:block;width:100%;margin-bottom:4px;padding:5px 8px;' +
                    'background:#313244;color:#cdd6f4;border:1px solid #45475a;border-radius:5px;cursor:pointer;">' +
                    '+ Assert page title (' + document.title + ')</button>' +

                    '<button id="__aitc_pick_btn" onclick="window.__aitcTogglePick()" style="display:block;width:100%;margin-bottom:12px;padding:5px 8px;' +
                    'background:#313244;color:#cdd6f4;border:1px solid #45475a;border-left:3px solid #a6e3a1;border-radius:5px;cursor:pointer;">' +
                    '+ Assert element\u2026</button>' +

                    '<div id="__aitc_pick_toast" style="display:none;margin-bottom:8px;padding:4px 8px;' +
                    'background:#313244;color:#a6e3a1;border-radius:4px;font-size:11px;"></div>' +

                    '<button onclick="window.aitcStopRecording()" ' +
                    'style="display:block;width:100%;padding:7px;' +
                    'background:#a6e3a1;color:#1e1e2e;border:none;border-radius:5px;cursor:pointer;font-weight:bold;">' +
                    'Save &amp; Stop</button>';

                document.body.appendChild(panel);

                // ── Highlight overlay (follows cursor during pick mode) ──
                __highlightDiv = document.createElement('div');
                __highlightDiv.id = '__aitc_pick_highlight';
                __highlightDiv.style.cssText =
                    'position:fixed;pointer-events:none;z-index:2147483645;' +
                    'background:rgba(166,227,161,0.18);border:2px solid #a6e3a1;border-radius:3px;' +
                    'transition:all 0.07s ease;display:none;';
                document.body.appendChild(__highlightDiv);

                // ── Assertion type context menu ──
                __pickMenuDiv = document.createElement('div');
                __pickMenuDiv.id = '__aitc_pick_menu';
                __pickMenuDiv.style.cssText =
                    'position:fixed;z-index:2147483646;background:#1e1e2e;color:#cdd6f4;' +
                    'border:1px solid #45475a;border-radius:8px;box-shadow:0 4px 20px rgba(0,0,0,0.5);' +
                    'padding:6px 0;font-family:monospace;font-size:13px;display:none;min-width:220px;';
                document.body.appendChild(__pickMenuDiv);
            }

            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', injectOverlay);
            } else {
                injectOverlay();
            }
        })();
        """;
}
