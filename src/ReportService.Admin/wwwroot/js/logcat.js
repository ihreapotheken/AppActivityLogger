// Client-side log filter for the Report detail page. Hides lines that don't match the level
// + search criteria. Search supports plain substring (case-insensitive) and /regex/ (slashes).
(function () {
    var body = document.getElementById('logcat-body');
    if (!body) return;
    var lines = Array.prototype.slice.call(body.querySelectorAll('.logcat-line'));
    var levelSel = document.getElementById('logcat-level');
    var searchEl = document.getElementById('logcat-search');
    var countEl = document.getElementById('logcat-count');
    var clearBtn = document.getElementById('logcat-clear');
    if (!levelSel || !searchEl || !countEl || !clearBtn) return;

    // Pre-cache lowercased text once so every keystroke runs in O(n) without re-reading DOM.
    lines.forEach(function (el) { el.dataset._lc = el.textContent.toLowerCase(); });

    function compileMatcher(raw) {
        if (!raw) return function () { return true; };
        var trimmed = raw.trim();
        if (trimmed.length >= 2 && trimmed.charAt(0) === '/' && trimmed.charAt(trimmed.length - 1) === '/') {
            try {
                var re = new RegExp(trimmed.slice(1, -1), 'i');
                return function (text) { return re.test(text); };
            } catch (e) { /* fall through to substring */ }
        }
        var needle = trimmed.toLowerCase();
        return function (text) { return text.indexOf(needle) !== -1; };
    }

    function apply() {
        var level = levelSel.value;
        var matcher = compileMatcher(searchEl.value);
        var shown = 0;
        for (var i = 0; i < lines.length; i++) {
            var el = lines[i];
            var okLevel = !level || el.dataset.level === level;
            var okSearch = matcher(el.dataset._lc);
            var visible = okLevel && okSearch;
            el.hidden = !visible;
            if (visible) shown++;
        }
        countEl.textContent = shown === lines.length
            ? lines.length + ' lines'
            : shown + ' of ' + lines.length + ' lines';
    }

    levelSel.addEventListener('change', apply);
    searchEl.addEventListener('input', apply);
    clearBtn.addEventListener('click', function () {
        levelSel.value = '';
        searchEl.value = '';
        apply();
    });
})();
