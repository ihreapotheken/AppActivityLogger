// Client-side column sorting for admin tables. Progressive enhancement: any table with
// `<th data-sort="text|num|date">` headers (emitted by the RSATableHeaderTagHelper for `sort="..."`)
// becomes click/keyboard sortable. Loaded globally from _Layout; a no-op on pages without such headers.
//
// Only applied to tables whose full dataset is in the DOM — server-paginated listings deliberately
// omit `sort` so we never imply a one-page reorder is a full-dataset sort.
//
// Sort key resolution per cell:
//   - `data-sort-value` attribute wins (required for bytes/dates/percentages whose display text
//     isn't directly comparable, e.g. "12.3 KiB", "55.0% (12)").
//   - otherwise the visible text, parsed by the column's declared type.
(function () {
    'use strict';

    function parseNumeric(text) {
        var n = parseFloat(String(text).replace(/[^0-9eE.+-]/g, ''));
        return isNaN(n) ? 0 : n;
    }

    function cellValue(row, index, type) {
        var cell = row.cells ? row.cells[index] : null;
        if (!cell) return type === 'text' ? '' : 0;
        var raw = cell.getAttribute('data-sort-value');
        if (type === 'text') {
            return (raw !== null ? raw : (cell.textContent || '')).trim().toLowerCase();
        }
        if (type === 'date') {
            var parsed = Date.parse(raw !== null ? raw : (cell.textContent || '').trim());
            return isNaN(parsed) ? 0 : parsed;
        }
        // num / bytes
        return raw !== null ? (parseFloat(raw) || 0) : parseNumeric(cell.textContent);
    }

    function columnIndex(th) {
        return Array.prototype.indexOf.call(th.parentNode.children, th);
    }

    function sortTable(table, th) {
        var tbody = table.tBodies[0];
        if (!tbody) return;
        var index = columnIndex(th);
        if (index < 0) return;
        var type = th.getAttribute('data-sort') || 'text';
        var direction = th.getAttribute('aria-sort') === 'ascending' ? 'descending' : 'ascending';

        // Reflect the active column; clear the rest so only one caret is "armed".
        var headers = th.parentNode.querySelectorAll('th[data-sort]');
        Array.prototype.forEach.call(headers, function (h) {
            h.setAttribute('aria-sort', h === th ? direction : 'none');
        });

        var sign = direction === 'ascending' ? 1 : -1;
        var rows = Array.prototype.slice.call(tbody.rows);
        rows
            .map(function (row, i) { return { row: row, i: i, value: cellValue(row, index, type) }; })
            .sort(function (a, b) {
                if (a.value < b.value) return -1 * sign;
                if (a.value > b.value) return 1 * sign;
                return a.i - b.i; // keep original order for ties (stable sort)
            })
            .forEach(function (entry) { tbody.appendChild(entry.row); });
    }

    function init() {
        var tables = document.querySelectorAll('table');
        Array.prototype.forEach.call(tables, function (table) {
            var headers = table.querySelectorAll('thead th[data-sort]');
            if (!headers.length) return;
            Array.prototype.forEach.call(headers, function (th) {
                var trigger = th.querySelector('.th-btn') || th;
                trigger.addEventListener('click', function (e) {
                    e.preventDefault();
                    sortTable(table, th);
                });
            });
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
