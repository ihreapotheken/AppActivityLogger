// In-dashboard API console — a small Hoppscotch/Postman-style request runner.
//
// The server ships the bundled Postman v2.1 collection + environment as <script type="application/json">
// data blocks (CSP-safe — never executed). Everything else happens here: we flatten the folder tree,
// resolve {{variables}} (incl. Postman dynamic vars), and run each request with fetch() against THIS
// origin. baseUrl defaults to window.location.origin, so requests stay same-origin and the admin session
// cookie (credentials: 'include') + dev API key header carry over with no CORS hop.
//
// CSP note: no inline scripts/styles. Element.style.* via the CSSOM, toggling [hidden]/classes, and
// assigning innerHTML built from HTML-escaped text are all permitted under script-src/style-src 'self'.
(function () {
    'use strict';

    var root = document.getElementById('api-console');
    if (!root) return; // empty-state build with no collection

    var ORIGIN = root.getAttribute('data-origin') || window.location.origin;
    var STORE_VARS = 'apic.vars.v1';
    var STORE_LAST = 'apic.lastRequest.v1';

    function parseDataBlock(id) {
        var el = document.getElementById(id);
        if (!el) return null;
        try { return JSON.parse(el.textContent); } catch (e) { return null; }
    }
    function store(key, val) { try { localStorage.setItem(key, val); } catch (e) { /* private mode */ } }
    function load(key) { try { return localStorage.getItem(key); } catch (e) { return null; } }

    var collection = parseDataBlock('api-console-collection');
    var environment = parseDataBlock('api-console-environment');
    if (!collection) return;

    // ---- Variables ---------------------------------------------------------------------------
    // Seeded from the Postman environment (enabled values) + collection-level variables, overlaid with
    // whatever the operator last edited (persisted per-browser), then baseUrl is pinned to this
    // dashboard's origin so "Send" runs same-origin.
    var vars = {};
    function seedVars() {
        vars = {};
        (collection.variable || []).forEach(function (v) {
            if (v && v.key) vars[v.key] = v.value != null ? String(v.value) : '';
        });
        if (environment && Array.isArray(environment.values)) {
            environment.values.forEach(function (v) {
                if (v && v.key && v.enabled !== false) vars[v.key] = v.value != null ? String(v.value) : '';
            });
        }
        // clientId/appId are tenant-attribution vars the operator can use to target a specific
        // client/app — including when the global switcher is on "All clients" (no pinned scope).
        // Register them as known (empty) vars BEFORE the saved overlay so they always show in the
        // Variables tab, resolve to '' rather than a literal {{clientId}}, and let a typed/saved value
        // persist. A pinned scope (below) still wins.
        if (!('clientId' in vars)) vars.clientId = '';
        if (!('appId' in vars)) vars.appId = '';
        var saved = load(STORE_VARS);
        if (saved) {
            try {
                var obj = JSON.parse(saved);
                Object.keys(obj).forEach(function (k) { vars[k] = String(obj[k]); });
            } catch (e) { /* ignore corrupt store */ }
        }
        vars.baseUrl = ORIGIN; // override whatever the env/store said — keep it same-origin
        // Pin the API key to the instance's configured ingestion key (server-injected) so requests
        // authenticate without the operator pasting one — the bundled fixture key is only a placeholder
        // for desktop Postman. Overrides the fixture/saved value (and heals a stale saved key), like
        // baseUrl; to use a different key, add an explicit `apiKey` header row on the request.
        var auth = parseDataBlock('api-console-auth');
        if (auth && auth.apiKey) vars.apiKey = auth.apiKey;
        // Pin clientId/appId to the top-left switcher's selection (server-injected) so requests
        // default to the scoped app. Overrides env/saved values; absent block / empty value = "All
        // clients", in which case the operator's typed value (if any) stands.
        var scope = parseDataBlock('api-console-scope');
        if (scope) {
            if (scope.clientId) vars.clientId = scope.clientId;
            if (scope.appId) vars.appId = scope.appId;
        }
    }
    function persistVars() {
        var copy = {};
        Object.keys(vars).forEach(function (k) { if (k !== 'baseUrl') copy[k] = vars[k]; });
        store(STORE_VARS, JSON.stringify(copy));
    }

    // Small faker pools for the $random* dynamic variables (a subset of Postman's faker set, so the
    // same {{$randomEmail}} etc. also resolve when the collection is imported into real Postman).
    var FAKE = {
        first: ['Anna', 'Lukas', 'Maria', 'Felix', 'Sophie', 'Jonas', 'Lena', 'Paul', 'Mia', 'Emil', 'Clara', 'Noah'],
        last: ['Müller', 'Schmidt', 'Weber', 'Becker', 'Wagner', 'Hoffmann', 'Schäfer', 'Koch', 'Fischer', 'Braun'],
        city: ['Berlin', 'Hamburg', 'München', 'Köln', 'Frankfurt', 'Stuttgart', 'Dresden', 'Leipzig', 'Bremen'],
        word: ['cart', 'checkout', 'scanner', 'profile', 'search', 'pharmacy', 'order', 'reminder', 'coupon', 'sync'],
        product: ['Ibuprofen 400', 'Vitamin D3', 'Aspirin', 'Paracetamol', 'Magnesium', 'Throat Spray', 'Plasters'],
        device: ['Pixel 8', 'Galaxy S24', 'iPhone 15', 'iPhone 14 Pro', 'Xperia 1 VI', 'OnePlus 12']
    };
    function pick(a) { return a[Math.floor(Math.random() * a.length)]; }
    function slug(s) { return String(s).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, ''); }

    // Per-Send cache for "sticky" dynamic vars (e.g. $sessionId): a sticky token resolves ONCE per
    // Send and the same value is reused for every occurrence in that request, so a batch's events
    // share one session/anonymous id. Non-sticky tokens ($guid, faker) stay fresh per occurrence so
    // eventIds remain unique. Null outside a Send, so the live URL preview always shows fresh values.
    var sendCache = null;
    function sticky(name, make) {
        if (!sendCache) return make();
        if (!(name in sendCache)) sendCache[name] = make();
        return sendCache[name];
    }

    function dynamic(name) {
        switch (name) {
            case '$isoTimestamp': return new Date().toISOString();
            case '$timestamp': return String(Math.floor(Date.now() / 1000));
            case '$guid':
            case '$randomUUID': return uuid();
            case '$randomInt': return String(Math.floor(Math.random() * 1000));
            case '$randomBoolean': return Math.random() < 0.5 ? 'true' : 'false';
            case '$randomFirstName': return pick(FAKE.first);
            case '$randomLastName': return pick(FAKE.last);
            case '$randomFullName': return pick(FAKE.first) + ' ' + pick(FAKE.last);
            case '$randomUserName': return slug(pick(FAKE.first)) + Math.floor(Math.random() * 1000);
            case '$randomEmail': return slug(pick(FAKE.first)) + '.' + slug(pick(FAKE.last)) + Math.floor(Math.random() * 100) + '@example.org';
            case '$randomCity': return pick(FAKE.city);
            case '$randomWord': return pick(FAKE.word);
            case '$randomWords': return pick(FAKE.word) + ' ' + pick(FAKE.word) + ' ' + pick(FAKE.word);
            case '$randomProductName': return pick(FAKE.product);
            case '$randomDeviceModel': return pick(FAKE.device);
            case '$randomPhoneNumber': return '+49 1' + (50 + Math.floor(Math.random() * 49)) + ' ' + (1000000 + Math.floor(Math.random() * 8999999));
            case '$randomIP': return [10, 0, 0, 0].map(function () { return Math.floor(Math.random() * 255); }).join('.');
            // Sticky (one value per Send) — group a batch's events under one session/user.
            case '$sessionId': return sticky(name, function () { return 'sess-' + uuid(); });
            case '$anonymousId': return sticky(name, function () { return 'anon-' + uuid(); });
            case '$userId': return sticky(name, function () { return uuid(); });
            default: return null;
        }
    }
    function uuid() {
        if (window.crypto && window.crypto.randomUUID) return window.crypto.randomUUID();
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            var r = (Math.random() * 16) | 0, v = c === 'x' ? r : (r & 0x3) | 0x8;
            return v.toString(16);
        });
    }

    // Resolve {{tokens}} against vars + dynamic vars. Iterates so a var whose value references another
    // var still resolves; bounded to avoid a cycle spinning forever.
    function subst(str) {
        if (str == null) return '';
        var out = String(str);
        for (var pass = 0; pass < 5 && out.indexOf('{{') !== -1; pass++) {
            out = out.replace(/\{\{([^}]+)\}\}/g, function (whole, name) {
                name = name.trim();
                if (name.charAt(0) === '$') {
                    var d = dynamic(name);
                    return d != null ? d : whole;
                }
                return Object.prototype.hasOwnProperty.call(vars, name) ? vars[name] : whole;
            });
        }
        return out;
    }
    // Names of {{tokens}} in a string that won't resolve (not a known var, not a dynamic var).
    function unresolvedTokens(str) {
        var miss = [], m, re = /\{\{([^}]+)\}\}/g;
        while ((m = re.exec(String(str))) !== null) {
            var name = m[1].trim();
            if (name.charAt(0) === '$') { if (dynamic(name) == null) miss.push(m[1].trim()); }
            else if (!Object.prototype.hasOwnProperty.call(vars, name)) miss.push(name);
        }
        return miss;
    }

    // ---- Collection flattening --------------------------------------------------------------
    // A Postman item is a folder when it has .item[], a request when it has .request. We keep the
    // collection-level auth as the inherited default; each request may override it (e.g. noauth).
    var collectionAuth = collection.auth || null;
    var requests = []; // flat list, used by the filter: {btn, name, folders}

    function buildTree(items, parentEl, folderPath) {
        (items || []).forEach(function (item) {
            if (item.item) {
                var group = document.createElement('div');
                group.className = 'apic-folder';
                var head = document.createElement('button');
                head.type = 'button';
                head.className = 'apic-folder-head';
                head.setAttribute('aria-expanded', 'true');
                head.textContent = item.name || 'Folder';
                var body = document.createElement('div');
                body.className = 'apic-folder-body';
                head.addEventListener('click', function () {
                    var open = head.getAttribute('aria-expanded') === 'true';
                    head.setAttribute('aria-expanded', String(!open));
                    body.hidden = open;
                });
                group.appendChild(head);
                group.appendChild(body);
                parentEl.appendChild(group);
                buildTree(item.item, body, folderPath.concat(item.name || ''));
            } else if (item.request) {
                var req = normalizeRequest(item);
                var btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'apic-req';
                btn.dataset.key = req.key;
                var m = document.createElement('span');
                m.className = 'apic-req-method m-' + req.method.toLowerCase();
                m.textContent = req.method;
                var n = document.createElement('span');
                n.className = 'apic-req-name';
                n.textContent = req.name;
                btn.appendChild(m);
                btn.appendChild(n);
                btn.addEventListener('click', function () { selectRequest(req, btn); });
                parentEl.appendChild(btn);
                requests.push({ btn: btn, text: (folderPath.join(' ') + ' ' + req.name).toLowerCase() });
            }
        });
    }

    function normalizeRequest(item) {
        var r = item.request || {};
        var url = r.url || {};
        var raw = typeof url === 'string' ? url : (url.raw || '');
        var headers = (r.header || []).filter(function (h) { return h && !h.disabled; })
            .map(function (h) { return { key: h.key || '', value: h.value || '' }; });
        var body = r.body || null;
        var tests = [];
        (item.event || []).forEach(function (ev) {
            if (ev.listen === 'test' && ev.script && ev.script.exec) {
                tests = tests.concat(ev.script.exec);
            }
        });
        var method = (r.method || 'GET').toUpperCase();
        var name = item.name || (method + ' ' + raw);
        var desc = item.description || r.description || '';
        if (desc && typeof desc === 'object') desc = desc.content || '';
        return {
            key: method + ' ' + name,
            name: name,
            method: method,
            url: raw,
            headers: headers,
            body: body,
            auth: r.auth !== undefined ? r.auth : collectionAuth,
            tests: tests,
            description: desc
        };
    }

    // ---- Request editor state ---------------------------------------------------------------
    var current = null;          // the selected normalized request
    var formdataState = null;    // array of {key,value,contentType,enabled} when body mode is formdata
    var lastResponseText = '';   // raw text of the last response, for Copy

    function $(id) { return document.getElementById(id); }
    var els = {
        method: $('apic-method'), url: $('apic-url'), resolved: $('apic-resolved'), warn: $('apic-warn'),
        desc: $('apic-desc'), body: $('apic-body'), bodyKind: $('apic-body-kind'),
        headers: $('apic-headers'), vars: $('apic-vars'), tests: $('apic-tests'),
        hdrCount: $('apic-hdr-count'), varCount: $('apic-var-count'),
        response: $('apic-response'), status: $('apic-status'), time: $('apic-time'), size: $('apic-size'),
        resbody: $('apic-resbody'), resheaders: $('apic-resheaders'),
        send: $('apic-send'), filter: $('apic-filter'), treeEmpty: $('apic-tree-empty')
    };

    function selectRequest(req, btn) {
        current = req;
        document.querySelectorAll('.apic-req.is-active').forEach(function (b) { b.classList.remove('is-active'); });
        if (btn) { btn.classList.add('is-active'); btn.scrollIntoView({ block: 'nearest' }); }
        store(STORE_LAST, req.key);

        els.method.value = req.method;
        setMethodColor();
        els.url.value = req.url;
        renderHeaders(req.headers);
        renderTests(req.tests);
        renderBody(req);
        renderDescription(req.description);
        updateResolved();
        els.response.hidden = true;
    }

    function setMethodColor() { els.method.dataset.method = els.method.value.toLowerCase(); }

    function renderDescription(desc) {
        if (desc && desc.trim()) {
            // Show the first paragraph only — the library is the place for full prose.
            els.desc.textContent = desc.split('\n')[0];
            els.desc.hidden = false;
        } else {
            els.desc.hidden = true;
        }
    }

    function renderBody(req) {
        var body = req.body;
        if (body && body.mode === 'formdata') {
            formdataState = (body.formdata || []).map(function (f) {
                return { key: f.key || '', value: f.value || '', contentType: f.contentType || '', enabled: !f.disabled, type: f.type || 'text' };
            });
            els.bodyKind.textContent = 'form-data';
            els.body.hidden = true;
            renderFormdata();
        } else {
            formdataState = null;
            removeFormdataTable();
            els.bodyKind.textContent = 'raw';
            els.body.hidden = false;
            els.body.value = body && body.raw ? body.raw : '';
        }
    }

    // Key/value table rendering shared by headers + variables (and a sibling for form-data).
    function renderKv(table, rows, onChange) {
        table.innerHTML = '';
        rows.forEach(function (row, i) {
            var tr = document.createElement('tr');
            var k = document.createElement('input');
            k.type = 'text'; k.className = 'input apic-kv-key'; k.value = row.key; k.placeholder = 'key';
            var v = document.createElement('input');
            v.type = 'text'; v.className = 'input apic-kv-val'; v.value = row.value; v.placeholder = 'value';
            var del = document.createElement('button');
            del.type = 'button'; del.className = 'apic-kv-del'; del.setAttribute('aria-label', 'Remove row'); del.textContent = '×';
            k.addEventListener('input', function () { row.key = k.value; onChange && onChange(); });
            v.addEventListener('input', function () { row.value = v.value; onChange && onChange(); });
            del.addEventListener('click', function () { rows.splice(i, 1); renderKv(table, rows, onChange); onChange && onChange(); });
            [k, v, del].forEach(function (cell) { var td = document.createElement('td'); td.appendChild(cell); tr.appendChild(td); });
            table.appendChild(tr);
        });
    }

    function badge(el, n) {
        if (n > 0) { el.textContent = n; el.hidden = false; } else { el.hidden = true; }
    }

    function renderHeaders(headers) {
        renderKv(els.headers, headers, function () { badge(els.hdrCount, countFilled(headers)); });
        badge(els.hdrCount, countFilled(headers));
    }
    function countFilled(rows) { return rows.filter(function (r) { return r.key && r.key.trim(); }).length; }

    function renderVars() {
        var rows = Object.keys(vars).sort().map(function (key) { return { key: key, value: vars[key] }; });
        badge(els.varCount, rows.length);
        // Re-derive the vars object from the editable rows on every change so renames/edits stick.
        renderKv(els.vars, rows, function () {
            var next = {};
            rows.forEach(function (r) { if (r.key) next[r.key] = r.value; });
            vars = next;
            vars.baseUrl = vars.baseUrl || ORIGIN;
            badge(els.varCount, Object.keys(vars).length);
            persistVars();
            updateResolved();
        });
    }

    function renderTests(tests) {
        els.tests.textContent = tests && tests.length ? tests.join('\n') : '// no test script on this request';
    }

    // form-data editor lives in a table appended after the (hidden) raw textarea.
    function removeFormdataTable() {
        var t = $('apic-formdata');
        if (t) t.parentNode.removeChild(t);
    }
    function renderFormdata() {
        removeFormdataTable();
        var table = document.createElement('table');
        table.id = 'apic-formdata';
        table.className = 'apic-kv';
        els.body.parentNode.insertBefore(table, els.body.nextSibling);
        renderKv(table, formdataState, null);
    }

    // ---- Variable substitution preview ------------------------------------------------------
    function updateResolved() {
        if (!current) { els.resolved.textContent = ''; els.warn.hidden = true; return; }
        var resolved = subst(els.url.value);
        var absUrl = toAbsolute(resolved);
        // Echo any tenant-attribution headers the console will add (clientId/appId → X-Analytics-*/
        // X-Report-App) so targeting a specific client/app is visible before sending, not silent.
        var injected = tenantHeaders(absUrl, effectiveHeaders().map(function (h) { return h.key; }));
        var injectedNote = Object.keys(injected).map(function (k) { return k + ': ' + injected[k]; }).join(', ');
        els.resolved.textContent = '→ ' + absUrl + (injectedNote ? '   (+ ' + injectedNote + ')' : '');

        // Flag any tokens that won't resolve — across URL + body — so a stray {{var}} is obvious
        // before it gets posted as a literal.
        var miss = unresolvedTokens(els.url.value);
        if (!els.body.hidden) miss = miss.concat(unresolvedTokens(els.body.value));
        miss = miss.filter(function (v, i, a) { return a.indexOf(v) === i; });
        if (miss.length) {
            els.warn.textContent = 'Unresolved: ' + miss.map(function (m) { return '{{' + m + '}}'; }).join(', ') +
                ' — set ' + (miss.length > 1 ? 'them' : 'it') + ' in Variables.';
            els.warn.hidden = false;
        } else {
            els.warn.hidden = true;
        }
    }

    // A resolved URL may still be relative (collection used {{baseUrl}} which is our origin) or absolute.
    function toAbsolute(u) {
        try { return new URL(u, ORIGIN).href; } catch (e) { return u; }
    }

    els.url.addEventListener('input', updateResolved);
    els.body.addEventListener('input', updateResolved);

    // ---- Send --------------------------------------------------------------------------------
    function effectiveHeaders() {
        // Start from the editable header rows, then layer the resolved auth header if the request
        // (or the inherited collection auth) is apikey-in-header and the user hasn't set it manually.
        var rows = [];
        els.headers.querySelectorAll('tr').forEach(function (tr) {
            var k = tr.querySelector('.apic-kv-key'), v = tr.querySelector('.apic-kv-val');
            if (k && k.value.trim()) rows.push({ key: k.value.trim(), value: v ? v.value : '' });
        });
        var auth = current && current.auth;
        if (auth && auth.type === 'apikey') {
            var cfg = {};
            (auth.apikey || []).forEach(function (p) { cfg[p.key] = p.value; });
            var inHeader = (cfg.in || 'header') === 'header';
            var headerName = cfg.key || 'apiKey';
            if (inHeader && !rows.some(function (r) { return r.key.toLowerCase() === headerName.toLowerCase(); })) {
                rows.push({ key: headerName, value: cfg.value || '' });
            }
        }
        return rows;
    }

    // Tenant attribution. When clientId/appId are set — pinned by the switcher OR typed in the
    // Variables tab while the global scope is "All clients" — carry them on outgoing ingestion
    // requests so the console can target a specific client/app. The server reads
    // X-Analytics-Client/X-Analytics-App for analytics and X-Report-App for problem/crash reports
    // (a key-bound API key still wins for the client; an unbound key honours the header). A blank
    // value injects nothing, so the server falls back to its configured default. Never overrides a
    // header the request already sets.
    function tenantHeaders(absUrl, existingKeys) {
        var add = {};
        var cid = (vars.clientId || '').trim();
        var aid = (vars.appId || '').trim();
        if (!cid && !aid) return add;
        var path;
        try { path = new URL(absUrl, ORIGIN).pathname; } catch (e) { path = String(absUrl || ''); }
        function has(name) {
            return existingKeys.some(function (k) { return k.toLowerCase() === name.toLowerCase(); });
        }
        if (/\/api\/v2\/analytics\//i.test(path)) {
            if (cid && !has('X-Analytics-Client')) add['X-Analytics-Client'] = cid;
            if (aid && !has('X-Analytics-App')) add['X-Analytics-App'] = aid;
        } else if (/\/(api\/v1\/reports|partners|api\/problem-reports|api\/v1\/forced-reports)/i.test(path)) {
            if (aid && !has('X-Report-App')) add['X-Report-App'] = aid;
        }
        return add;
    }

    // Assemble {method, url, headers{}, body} with all variables resolved, inside one sticky scope.
    function buildRequest() {
        sendCache = {};
        try {
            var method = els.method.value;
            var url = toAbsolute(subst(els.url.value));
            var headers = {};
            effectiveHeaders().forEach(function (h) { headers[h.key] = subst(h.value); });
            var extra = tenantHeaders(url, Object.keys(headers));
            Object.keys(extra).forEach(function (k) { headers[k] = extra[k]; });
            var bodyAllowed = method !== 'GET' && method !== 'HEAD';
            var out = { method: method, url: url, headers: headers, body: null, formdata: null };
            if (bodyAllowed) {
                if (formdataState) {
                    out.formdata = formdataState.filter(function (f) { return f.enabled && f.key; })
                        .map(function (f) { return { key: f.key, value: subst(f.value), contentType: f.contentType }; });
                } else if (els.body.value.trim()) {
                    out.body = subst(els.body.value);
                }
            }
            return out;
        } finally {
            sendCache = null;
        }
    }

    function send() {
        if (!current) return;
        var r = buildRequest();
        var init = { method: r.method, headers: r.headers, credentials: 'include' };
        if (r.formdata) {
            var fd = new FormData();
            r.formdata.forEach(function (f) {
                if (f.contentType) fd.append(f.key, new Blob([f.value], { type: f.contentType }));
                else fd.append(f.key, f.value);
            });
            init.body = fd; // browser sets the multipart Content-Type + boundary
            delete init.headers['Content-Type'];
        } else if (r.body != null) {
            init.body = r.body;
        }

        els.send.disabled = true;
        els.send.classList.add('is-loading');
        var t0 = performance.now();
        fetch(r.url, init).then(function (res) {
            var ms = Math.round(performance.now() - t0);
            return res.text().then(function (text) { renderResponse(res, text, ms); });
        }).catch(function (err) {
            renderError(err);
        }).finally(function () {
            els.send.disabled = false;
            els.send.classList.remove('is-loading');
        });
    }

    function renderResponse(res, text, ms) {
        lastResponseText = text;
        els.response.hidden = false;
        els.status.textContent = res.status + ' ' + res.statusText;
        els.status.className = 'apic-status ' + statusClass(res.status);
        els.time.textContent = ms + ' ms';
        els.size.textContent = formatBytes(new Blob([text]).size);

        var ct = res.headers.get('content-type') || '';
        if (ct.indexOf('json') !== -1) {
            els.resbody.innerHTML = highlightJson(prettyJson(text));
        } else {
            els.resbody.textContent = text;
        }

        var hlines = [];
        res.headers.forEach(function (v, k) { hlines.push(k + ': ' + v); });
        els.resheaders.textContent = hlines.join('\n');
        els.response.scrollIntoView({ block: 'nearest' });
    }

    function renderError(err) {
        lastResponseText = '';
        els.response.hidden = false;
        els.status.textContent = 'Request failed';
        els.status.className = 'apic-status s-err';
        els.time.textContent = '';
        els.size.textContent = '';
        els.resbody.textContent = String(err && err.message ? err.message : err) +
            '\n\nA network-level failure (CORS, mixed content, or the server is down). Same-origin requests to this dashboard should succeed; cross-origin hosts need CORS enabled.';
        els.resheaders.textContent = '';
    }

    function statusClass(code) {
        if (code < 300) return 's-ok';
        if (code < 400) return 's-redir';
        if (code < 500) return 's-warn';
        return 's-err';
    }
    function prettyJson(text) {
        try { return JSON.stringify(JSON.parse(text), null, 2); } catch (e) { return text; }
    }
    function formatBytes(n) {
        if (n < 1024) return n + ' B';
        if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
        return (n / 1024 / 1024).toFixed(1) + ' MB';
    }
    function escapeHtml(s) {
        return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }
    // Lightweight JSON syntax highlighter: HTML-escape first (so response data can't inject markup),
    // then wrap each token in a class span. innerHTML of escaped text + our own spans is CSP-safe.
    function highlightJson(json) {
        return escapeHtml(json).replace(
            /("(?:\\.|[^"\\])*"(\s*:)?)|\b(true|false|null)\b|(-?\d+(?:\.\d+)?(?:[eE][+\-]?\d+)?)/g,
            function (m, str, isKey, lit, num) {
                var cls = 'j-num';
                if (str) cls = isKey ? 'j-key' : 'j-str';
                else if (lit) cls = (lit === 'null') ? 'j-null' : 'j-bool';
                return '<span class="' + cls + '">' + m + '</span>';
            });
    }

    // ---- Copy / cURL -------------------------------------------------------------------------
    function copyText(text, btn) {
        var done = function () { flash(btn, 'Copied'); };
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(done, function () { legacyCopy(text); done(); });
        } else { legacyCopy(text); done(); }
    }
    function legacyCopy(text) {
        var ta = document.createElement('textarea');
        ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
        document.body.appendChild(ta); ta.select();
        try { document.execCommand('copy'); } catch (e) { /* ignore */ }
        document.body.removeChild(ta);
    }
    function flash(btn, msg) {
        if (!btn) return;
        var prev = btn.textContent;
        btn.textContent = msg; btn.classList.add('is-done');
        setTimeout(function () { btn.textContent = prev; btn.classList.remove('is-done'); }, 1200);
    }
    // Shell-quote a single argument for the cURL snippet.
    function sh(s) { return "'" + String(s).replace(/'/g, "'\\''") + "'"; }
    function toCurl() {
        var r = buildRequest();
        var parts = ['curl -X ' + r.method + ' ' + sh(r.url)];
        Object.keys(r.headers).forEach(function (k) { parts.push("-H " + sh(k + ': ' + r.headers[k])); });
        if (r.formdata) {
            r.formdata.forEach(function (f) {
                parts.push('-F ' + sh(f.key + '=' + f.value + (f.contentType ? ';type=' + f.contentType : '')));
            });
        } else if (r.body != null) {
            parts.push('--data ' + sh(r.body));
        }
        // credentials: 'include' → send cookies; mirror it so the copied command behaves the same.
        parts.push('--cookie ' + sh(document.cookie || ''));
        return parts.join(' \\\n  ');
    }

    // ---- Tabs + controls --------------------------------------------------------------------
    function wireTabs(tabSelector, panelAttr, tabAttr) {
        document.querySelectorAll(tabSelector).forEach(function (tab) {
            tab.addEventListener('click', function () {
                var groupEl = tab.parentNode;
                groupEl.querySelectorAll(tabSelector).forEach(function (t) {
                    t.classList.remove('is-active'); t.setAttribute('aria-selected', 'false');
                });
                tab.classList.add('is-active'); tab.setAttribute('aria-selected', 'true');
                var key = tab.getAttribute(tabAttr);
                document.querySelectorAll('[' + panelAttr + ']').forEach(function (p) {
                    p.hidden = p.getAttribute(panelAttr) !== key;
                });
            });
        });
    }
    wireTabs('[data-apic-tab]', 'data-apic-panel', 'data-apic-tab');
    wireTabs('[data-apic-restab]', 'data-apic-respanel', 'data-apic-restab');

    els.send.addEventListener('click', send);
    els.method.addEventListener('change', function () { setMethodColor(); updateResolved(); });
    // ⌘/Ctrl + Enter sends from anywhere on the page (incl. the body textarea); Enter in the URL too.
    document.addEventListener('keydown', function (e) {
        if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') { e.preventDefault(); send(); }
    });
    els.url.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); send(); } });

    $('apic-format').addEventListener('click', function () {
        if (!els.body.hidden) { els.body.value = prettyJson(els.body.value); updateResolved(); }
    });
    $('apic-curl').addEventListener('click', function () { copyText(toCurl(), $('apic-curl')); });
    $('apic-copy').addEventListener('click', function () {
        copyText(lastResponseText || els.resbody.textContent, $('apic-copy'));
    });
    $('apic-wrap').addEventListener('click', function () {
        var on = els.resbody.classList.toggle('is-wrap');
        els.resheaders.classList.toggle('is-wrap', on);
        this.setAttribute('aria-pressed', String(on));
    });
    $('apic-add-header').addEventListener('click', function () {
        if (!current) return;
        current.headers.push({ key: '', value: '' });
        renderHeaders(current.headers);
    });
    $('apic-add-var').addEventListener('click', function () {
        var name = 'newVar' + (Object.keys(vars).length);
        vars[name] = '';
        persistVars();
        renderVars();
    });
    $('apic-reset-vars').addEventListener('click', function () {
        store(STORE_VARS, '');
        seedVars();
        renderVars();
        updateResolved();
        flash(this, 'Reset');
    });

    // Filter: match folder + request names; hide folders with no visible request; "no matches" note.
    els.filter.addEventListener('input', function () {
        var q = els.filter.value.trim().toLowerCase();
        var anyVisible = false;
        requests.forEach(function (r) {
            var show = q === '' || r.text.indexOf(q) !== -1;
            r.btn.hidden = !show;
            if (show) anyVisible = true;
        });
        // A folder is visible if it has at least one visible request.
        document.querySelectorAll('.apic-folder').forEach(function (f) {
            var hasVisible = f.querySelector('.apic-req:not([hidden])') !== null;
            f.hidden = !hasVisible;
        });
        els.treeEmpty.hidden = anyVisible;
    });

    // ---- Boot --------------------------------------------------------------------------------
    seedVars();
    buildTree(collection.item, $('apic-tree'), []);
    renderVars();
    // Re-select the operator's last request if it's still in the collection; else the first one.
    var lastKey = load(STORE_LAST);
    var target = lastKey && document.querySelector('.apic-req[data-key="' + (window.CSS && CSS.escape ? CSS.escape(lastKey) : lastKey) + '"]');
    (target || document.querySelector('.apic-req') || { click: function () {} }).click();
})();
