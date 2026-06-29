// Docs page: tabbed chapter navigation + "on this page" table-of-contents scrollspy.
// Progressive enhancement — the page renders fully without JS (first chapter visible); this wires
// up tab switching, deep links (#chapter-slug or #chapter-slug--heading), arrow-key nav, and TOC
// highlighting. Served from _Layout's Scripts section; a no-op on pages without a `.docs` root.
(function () {
    'use strict';

    var root = document.querySelector('.docs');
    if (!root) return;
    var tabs = Array.prototype.slice.call(root.querySelectorAll('[data-doc-tab]'));
    if (!tabs.length) return;
    var panels = root.querySelectorAll('[data-doc-panel]');
    var tocs = root.querySelectorAll('[data-doc-toc]');
    var spy = null;

    function toggle(el, on) {
        el.classList.toggle('is-active', on);
        if (on) { el.removeAttribute('hidden'); } else { el.setAttribute('hidden', 'hidden'); }
    }

    // Highlight the TOC entry for whichever heading is near the top of the viewport.
    function startScrollspy(slug) {
        if (spy) { spy.disconnect(); spy = null; }
        if (!('IntersectionObserver' in window)) return;
        var panel = root.querySelector('[data-doc-panel="' + slug + '"]');
        var toc = root.querySelector('[data-doc-toc="' + slug + '"]');
        if (!panel || !toc) return;

        var links = {};
        Array.prototype.forEach.call(toc.querySelectorAll('a[data-toc-link]'), function (a) {
            links[a.getAttribute('data-toc-link')] = a;
        });
        var headings = panel.querySelectorAll('h2[id], h3[id]');
        if (!headings.length) return;

        spy = new IntersectionObserver(function (entries) {
            entries.forEach(function (entry) {
                if (!entry.isIntersecting) return;
                var current = links[entry.target.id];
                if (!current) return;
                Object.keys(links).forEach(function (k) { links[k].classList.remove('is-current'); });
                current.classList.add('is-current');
            });
        }, { rootMargin: '0px 0px -75% 0px', threshold: 0 });
        Array.prototype.forEach.call(headings, function (h) { spy.observe(h); });
    }

    function activate(slug, updateHash) {
        var tab = tabs.filter(function (t) { return t.getAttribute('data-doc-tab') === slug; })[0];
        if (!tab) return;
        tabs.forEach(function (t) {
            var on = t === tab;
            t.classList.toggle('is-active', on);
            t.setAttribute('aria-selected', on ? 'true' : 'false');
            t.tabIndex = on ? 0 : -1;
        });
        Array.prototype.forEach.call(panels, function (p) { toggle(p, p.getAttribute('data-doc-panel') === slug); });
        Array.prototype.forEach.call(tocs, function (n) { toggle(n, n.getAttribute('data-doc-toc') === slug); });
        startScrollspy(slug);
        if (updateHash && window.history && history.replaceState) {
            history.replaceState(null, '', '#' + slug);
        }
        window.scrollTo({ top: 0 });
    }

    tabs.forEach(function (t) {
        t.addEventListener('click', function () { activate(t.getAttribute('data-doc-tab'), true); });
    });

    // Roving arrow-key navigation across the tablist (WAI-ARIA tabs pattern).
    var tablist = root.querySelector('.docs-tabs');
    if (tablist) {
        tablist.addEventListener('keydown', function (e) {
            if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
            var idx = tabs.map(function (t) { return t.getAttribute('aria-selected'); }).indexOf('true');
            if (idx < 0) return;
            var next = e.key === 'ArrowRight' ? (idx + 1) % tabs.length : (idx - 1 + tabs.length) % tabs.length;
            activate(tabs[next].getAttribute('data-doc-tab'), true);
            tabs[next].focus();
            e.preventDefault();
        });
    }

    // Deep links: #chapter-slug opens that tab; #chapter-slug--heading opens the tab then scrolls.
    function openFromHash() {
        var hash = (location.hash || '').replace(/^#/, '');
        if (!hash) return false;
        if (tabs.some(function (t) { return t.getAttribute('data-doc-tab') === hash; })) {
            activate(hash, false);
            return true;
        }
        var sep = hash.indexOf('--');
        if (sep > 0) {
            var slug = hash.slice(0, sep);
            if (tabs.some(function (t) { return t.getAttribute('data-doc-tab') === slug; })) {
                activate(slug, false);
                var target = document.getElementById(hash);
                if (target) target.scrollIntoView();
                return true;
            }
        }
        return false;
    }

    if (!openFromHash()) {
        activate(tabs[0].getAttribute('data-doc-tab'), false);
    }
    window.addEventListener('hashchange', openFromHash);
})();
