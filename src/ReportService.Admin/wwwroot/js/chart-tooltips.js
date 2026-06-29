// Cursor-following tooltips for SVG charts. Any chart element (bar column, line dot, donut slice)
// that carries `data-chart-tip` opts in; we render ONE tooltip on <body> (so the chart's overflow
// can't clip it) and position it via the CSSOM — element.style.* setters are permitted under the
// strict admin CSP (style-src 'self'), unlike inline style attributes. Loaded globally from
// _Layout; a no-op on pages with no charts. Mirrors header-tooltips.js, but follows the pointer
// because chart targets (a whole bar column) are large.
(function () {
    'use strict';

    var ATTR = 'data-chart-tip';
    var GAP = 14;    // px between cursor and tooltip
    var MARGIN = 6;  // viewport edge padding
    var tip = null;
    var current = null;

    function ensureTip() {
        if (tip) return tip;
        tip = document.createElement('div');
        tip.className = 'chart-tooltip';
        tip.id = 'chart-tooltip';
        tip.setAttribute('role', 'tooltip');
        tip.hidden = true;
        document.body.appendChild(tip);
        return tip;
    }

    function targetFrom(node) {
        return node && node.closest ? node.closest('[' + ATTR + ']') : null;
    }

    // Place near the cursor when we have one (bar columns are wide); otherwise centre over the
    // element (keyboard focus has no pointer coordinates).
    function placeAtPoint(t, x, y) {
        var tw = t.offsetWidth;
        var th = t.offsetHeight;
        var left = x - tw / 2;
        left = Math.max(MARGIN, Math.min(left, window.innerWidth - MARGIN - tw));
        var top = y - th - GAP;
        if (top < MARGIN) top = y + GAP;        // flip below the cursor near the top edge
        t.style.left = Math.round(left) + 'px';
        t.style.top = Math.round(top) + 'px';
    }

    function placeAtElement(el, t) {
        var r = el.getBoundingClientRect();
        placeAtPoint(t, r.left + r.width / 2, r.top);
    }

    function show(el, text) {
        var t = ensureTip();
        t.textContent = text;
        t.hidden = false;
        current = el;
        return t;
    }

    function hide() {
        if (tip) tip.hidden = true;
        current = null;
    }

    document.addEventListener('mouseover', function (e) {
        var el = targetFrom(e.target);
        if (!el) return;
        var text = el.getAttribute(ATTR);
        if (!text) return;
        var t = show(el, text);
        placeAtPoint(t, e.clientX, e.clientY);
    });
    document.addEventListener('mousemove', function (e) {
        if (!current || !tip || tip.hidden) return;
        // Still over the same target? (cheap: ask the element under the cursor)
        if (targetFrom(e.target) === current) placeAtPoint(tip, e.clientX, e.clientY);
    });
    document.addEventListener('mouseout', function (e) {
        var el = targetFrom(e.target);
        if (!el || el !== current) return;
        if (e.relatedTarget && el.contains(e.relatedTarget)) return; // moving within the same target
        hide();
    });

    // Keyboard accessibility: dots/slices can be focusable (tabindex set in the markup).
    document.addEventListener('focusin', function (e) {
        var el = targetFrom(e.target);
        if (!el) return;
        var text = el.getAttribute(ATTR);
        if (text) placeAtElement(el, show(el, text));
    });
    document.addEventListener('focusout', function (e) {
        if (targetFrom(e.target) === current) hide();
    });

    // A fixed tooltip would drift from its anchor on scroll/resize — just dismiss it.
    window.addEventListener('scroll', hide, true);
    window.addEventListener('resize', hide);
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') hide(); });
})();
