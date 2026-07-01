// Header tenant/app switcher.
//
// Progressive enhancement: the server renders a native <select> inside a POST form to /Scope (which
// sets the sticky scope cookie and redirects back to the current page). With no JS that select still
// works — change fires a submit, and there's a <noscript> "Go" button. With JS we HIDE the select and
// build a nicer popover dropdown from its own <optgroup>/<option> structure, so there is one source of
// truth and no duplicated markup. Kept external (not inline) to satisfy the strict CSP (script-src
// 'self').
//
// The option VALUE encodes the scope as "client|app": ""=all clients, "client|"=a whole client (all
// its apps), "client|app"=one app. The menu renders those three levels with a client header, an
// "All apps" row per client, and indented app rows.
(function () {
    'use strict';

    var form = document.querySelector('form[data-tenant-switcher-form]');
    var sel = form && form.querySelector('select[data-tenant-switcher]');
    if (!form || !sel) return;

    // No-JS fallback path also stays wired: a native change submits the form.
    sel.addEventListener('change', function () { form.submit(); });

    function stripSlug(label) {
        // optgroup labels are "Display Name (slug)" — drop the parenthetical for the compact trigger.
        return label.replace(/\s*\([^)]*\)\s*$/, '').trim() || label;
    }

    // Flatten the select into a model the menu renders from.
    function readModel() {
        var groups = [];       // { client: string|null, items: [{value,label,level}] }
        var current = { value: sel.value, trigger: '' };
        var loose = { client: null, items: [] };

        function pushOption(opt, group) {
            // A disabled <option> (e.g. "No registered apps" under a client with none) is a caption,
            // not a choice: render it as a note, don't make it selectable.
            if (opt.disabled) { group.items.push({ label: opt.textContent.trim(), note: true }); return; }
            var value = opt.value;
            var level = value === '' ? 'all' : (value.charAt(value.length - 1) === '|' ? 'client' : 'app');
            var label = level === 'client' ? 'All apps' : opt.textContent.trim();
            group.items.push({ value: value, label: label, level: level });
            if (opt.selected) {
                current.value = value;
                if (level === 'all') current.trigger = opt.textContent.trim();
                else if (level === 'client') current.trigger = (group.client || '') + ' — all apps';
                else current.trigger = (group.client ? group.client + ' › ' : '') + opt.textContent.trim();
            }
        }

        for (var i = 0; i < sel.children.length; i++) {
            var node = sel.children[i];
            if (node.tagName === 'OPTGROUP') {
                var g = { client: stripSlug(node.label), items: [] };
                for (var j = 0; j < node.children.length; j++) {
                    if (node.children[j].tagName === 'OPTION') pushOption(node.children[j], g);
                }
                groups.push(g);
            } else if (node.tagName === 'OPTION') {
                pushOption(node, loose);
            }
        }
        if (loose.items.length) groups.unshift(loose);
        if (!current.trigger) current.trigger = (sel.options[sel.selectedIndex] || {}).textContent || 'Select';
        return { groups: groups, current: current };
    }

    var model = readModel();

    // ---- Build the widget ------------------------------------------------------------------------
    var dd = document.createElement('div');
    dd.className = 'tenant-dd';

    var trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'tenant-dd-trigger';
    trigger.setAttribute('aria-haspopup', 'listbox');
    trigger.setAttribute('aria-expanded', 'false');
    trigger.innerHTML = '<span class="tenant-dd-value"></span><span class="tenant-dd-caret" aria-hidden="true"></span>';
    trigger.querySelector('.tenant-dd-value').textContent = model.current.trigger;

    var menu = document.createElement('div');
    menu.className = 'tenant-dd-menu';
    menu.setAttribute('role', 'listbox');
    menu.hidden = true;

    var filterWrap = document.createElement('div');
    filterWrap.className = 'tenant-dd-filterwrap';
    var filter = document.createElement('input');
    filter.type = 'search';
    filter.className = 'tenant-dd-filter';
    filter.placeholder = 'Filter clients & apps…';
    filter.setAttribute('aria-label', 'Filter clients and apps');
    filterWrap.appendChild(filter);

    var list = document.createElement('div');
    list.className = 'tenant-dd-list';

    var optionEls = []; // interactive option buttons (keyboard nav + selection)
    var rowEls = [];    // all filterable rows (options + "No registered apps" notes)

    function makeNote(item, groupLabel) {
        var d = document.createElement('div');
        d.className = 'tenant-dd-note';
        d.dataset.search = (item.label + ' ' + (groupLabel || '')).toLowerCase();
        d.textContent = item.label;
        rowEls.push(d);
        return d;
    }

    function makeOption(item, groupLabel) {
        var b = document.createElement('button');
        b.type = 'button';
        b.className = 'tenant-dd-opt tenant-dd-opt-' + item.level;
        b.setAttribute('role', 'option');
        b.dataset.value = item.value;
        // Searchable text: label + its client group, so filtering an app by client name works.
        b.dataset.search = (item.label + ' ' + (groupLabel || '')).toLowerCase();
        var selected = item.value === model.current.value;
        b.setAttribute('aria-selected', selected ? 'true' : 'false');
        if (selected) b.classList.add('is-selected');
        b.innerHTML = '<span class="tenant-dd-check" aria-hidden="true"></span><span class="tenant-dd-opt-label"></span>';
        b.querySelector('.tenant-dd-opt-label').textContent = item.label;
        b.addEventListener('click', function () { choose(item.value); });
        optionEls.push(b);
        rowEls.push(b);
        return b;
    }

    model.groups.forEach(function (g) {
        if (g.client) {
            var header = document.createElement('div');
            header.className = 'tenant-dd-group';
            header.textContent = g.client;
            list.appendChild(header);
        }
        g.items.forEach(function (item) {
            list.appendChild(item.note ? makeNote(item, g.client) : makeOption(item, g.client));
        });
    });

    menu.appendChild(filterWrap);
    menu.appendChild(list);
    dd.appendChild(trigger);
    dd.appendChild(menu);

    // Insert the widget before the select and hide the native control (keep it in the DOM as the
    // form's real field + no-JS fallback).
    sel.parentNode.insertBefore(dd, sel);
    sel.classList.add('tenant-switcher-select--enhanced');

    // ---- Behaviour -------------------------------------------------------------------------------
    var activeIdx = -1;

    function visibleOptions() {
        return optionEls.filter(function (el) { return !el.hidden; });
    }

    function setActive(idx) {
        var vis = visibleOptions();
        vis.forEach(function (el) { el.classList.remove('is-active'); });
        activeIdx = idx;
        if (idx >= 0 && idx < vis.length) {
            vis[idx].classList.add('is-active');
            vis[idx].scrollIntoView({ block: 'nearest' });
        }
    }

    function open() {
        menu.hidden = false;
        trigger.setAttribute('aria-expanded', 'true');
        dd.classList.add('is-open');
        filter.value = '';
        applyFilter('');
        // Start with the selected option active.
        var vis = visibleOptions();
        var selIdx = vis.findIndex(function (el) { return el.classList.contains('is-selected'); });
        setActive(selIdx >= 0 ? selIdx : (vis.length ? 0 : -1));
        setTimeout(function () { filter.focus(); }, 0);
    }

    function close() {
        menu.hidden = true;
        trigger.setAttribute('aria-expanded', 'false');
        dd.classList.remove('is-open');
    }

    function toggle() { menu.hidden ? open() : close(); }

    function choose(value) {
        // Drive the real field so the form posts the chosen scope, then submit.
        sel.value = value;
        close();
        form.submit();
    }

    function applyFilter(q) {
        q = q.trim().toLowerCase();
        // Show/hide rows (options + notes), then hide group headers with no visible row under them.
        rowEls.forEach(function (el) {
            el.hidden = q !== '' && el.dataset.search.indexOf(q) === -1;
        });
        var nodes = list.children, lastHeader = null, headerHasVisible = false;
        for (var i = 0; i < nodes.length; i++) {
            var n = nodes[i];
            if (n.classList.contains('tenant-dd-group')) {
                if (lastHeader) lastHeader.hidden = !headerHasVisible;
                lastHeader = n; headerHasVisible = false;
            } else if (!n.hidden) {
                headerHasVisible = true;
            }
        }
        if (lastHeader) lastHeader.hidden = !headerHasVisible;
        setActive(visibleOptions().length ? 0 : -1);
    }

    trigger.addEventListener('click', function (e) { e.preventDefault(); toggle(); });

    filter.addEventListener('input', function () { applyFilter(filter.value); });

    dd.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') { close(); trigger.focus(); return; }
        if (menu.hidden) return;
        var vis = visibleOptions();
        if (e.key === 'ArrowDown') { e.preventDefault(); setActive(Math.min(activeIdx + 1, vis.length - 1)); }
        else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(Math.max(activeIdx - 1, 0)); }
        else if (e.key === 'Enter') {
            e.preventDefault();
            if (activeIdx >= 0 && vis[activeIdx]) vis[activeIdx].click();
        }
    });

    document.addEventListener('click', function (e) {
        if (!dd.contains(e.target)) close();
    });
})();
