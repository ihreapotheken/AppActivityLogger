// Tabbed chart switcher: shows one chart panel at a time behind a pill tablist (Analytics
// "Activity over time"). Progressive enhancement — without JS the [hidden] tablist stays hidden
// and every [data-chart-panel] renders stacked; this script reveals the tablist, collapses to the
// first panel, and wires click + arrow-key (WAI-ARIA tabs pattern) switching. Loaded globally from
// _Layout; a no-op on pages with no [data-chart-tabs] container. Supports multiple groups per page.
(function () {
    'use strict';

    var groups = document.querySelectorAll('[data-chart-tabs]');
    if (!groups.length) return;

    Array.prototype.forEach.call(groups, function (root) {
        var tablist = root.querySelector('[role="tablist"]');
        var tabs = Array.prototype.slice.call(root.querySelectorAll('[data-chart-tab]'));
        var panels = Array.prototype.slice.call(root.querySelectorAll('[data-chart-panel]'));
        if (!tabs.length || !panels.length) return;

        function activate(key, focusTab) {
            tabs.forEach(function (t) {
                var on = t.getAttribute('data-chart-tab') === key;
                t.classList.toggle('is-active', on);
                t.setAttribute('aria-selected', on ? 'true' : 'false');
                t.tabIndex = on ? 0 : -1;
                if (on && focusTab) t.focus();
            });
            panels.forEach(function (p) {
                if (p.getAttribute('data-chart-panel') === key) { p.removeAttribute('hidden'); }
                else { p.setAttribute('hidden', 'hidden'); }
            });
        }

        tabs.forEach(function (t) {
            t.addEventListener('click', function () { activate(t.getAttribute('data-chart-tab'), false); });
        });

        // Roving arrow-key navigation across the tablist (WAI-ARIA tabs pattern).
        if (tablist) {
            tablist.addEventListener('keydown', function (e) {
                if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
                var idx = tabs.map(function (t) { return t.getAttribute('aria-selected'); }).indexOf('true');
                if (idx < 0) return;
                var next = e.key === 'ArrowRight' ? (idx + 1) % tabs.length : (idx - 1 + tabs.length) % tabs.length;
                activate(tabs[next].getAttribute('data-chart-tab'), true);
                e.preventDefault();
            });
            tablist.removeAttribute('hidden');
        }

        // Initial state: first tab active, every other panel collapsed.
        activate(tabs[0].getAttribute('data-chart-tab'), false);
    });
})();
