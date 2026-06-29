// Styled hover/focus tooltips for table column headers. Each header emitted by
// RSATableHeaderTagHelper carries its description in `data-th-tip`. We render ONE tooltip element on
// <body> (not inside the table) so the table's `overflow: hidden` can't clip it, and position it via
// the CSSOM — element.style.* setters are allowed under the admin CSP (style-src 'self'), unlike
// inline style attributes. Loaded globally from _Layout; a no-op on pages without tipped headers.
(function () {
    'use strict';

    var ATTR = 'data-th-tip';
    var GAP = 8;     // px between header and tooltip
    var MARGIN = 6;  // viewport edge padding
    var tip = null;
    var current = null;

    function ensureTip() {
        if (tip) return tip;
        tip = document.createElement('div');
        tip.className = 'th-tooltip';
        tip.id = 'th-tooltip';
        tip.setAttribute('role', 'tooltip');
        tip.hidden = true;
        document.body.appendChild(tip);
        return tip;
    }

    function place(el, t) {
        var r = el.getBoundingClientRect();
        var tw = t.offsetWidth;
        var thh = t.offsetHeight;
        var left = r.left + (r.width - tw) / 2;
        left = Math.max(MARGIN, Math.min(left, window.innerWidth - MARGIN - tw));
        var top = r.top - thh - GAP;
        var below = top < MARGIN;
        if (below) top = r.bottom + GAP;
        t.classList.toggle('th-tooltip--below', below);
        // CSSOM property setters — permitted under style-src 'self'.
        t.style.left = Math.round(left) + 'px';
        t.style.top = Math.round(top) + 'px';
    }

    function show(el) {
        var text = el.getAttribute(ATTR);
        if (!text) return;
        var t = ensureTip();
        t.textContent = text;
        t.hidden = false;
        el.setAttribute('aria-describedby', 'th-tooltip');
        current = el;
        place(el, t);
    }

    function hide(el) {
        if (tip) tip.hidden = true;
        if (el && el.removeAttribute) el.removeAttribute('aria-describedby');
        if (current === el) current = null;
    }

    function headerFrom(node) {
        return node && node.closest ? node.closest('[' + ATTR + ']') : null;
    }

    document.addEventListener('mouseover', function (e) {
        var el = headerFrom(e.target);
        if (el && el !== current) show(el);
    });
    document.addEventListener('mouseout', function (e) {
        var el = headerFrom(e.target);
        if (!el) return;
        // Ignore moves between the header and its own children (button, caret, label).
        if (e.relatedTarget && el.contains(e.relatedTarget)) return;
        hide(el);
    });
    document.addEventListener('focusin', function (e) {
        var el = headerFrom(e.target);
        if (el) show(el);
    });
    document.addEventListener('focusout', function (e) {
        var el = headerFrom(e.target);
        if (el) hide(el);
    });

    // A fixed-position tooltip would drift from its header on scroll/resize — just dismiss it.
    function dismiss() { if (current) hide(current); }
    window.addEventListener('scroll', dismiss, true);
    window.addEventListener('resize', dismiss);
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') dismiss(); });
})();
