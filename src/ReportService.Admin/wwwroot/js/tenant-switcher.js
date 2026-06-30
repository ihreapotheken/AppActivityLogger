// Header tenant/app switcher: submit the scope form on change. The <select> sits in a POST form to
// /Scope, which sets the sticky scope cookie and redirects back to the current page. Kept in an
// external file (not an inline onchange handler) so it works under the strict production CSP
// (script-src 'self'). No-JS users get the <noscript> "Go" button.
(function () {
    'use strict';
    var sel = document.querySelector('select[data-tenant-switcher]');
    if (!sel) return;
    sel.addEventListener('change', function () {
        var form = this.closest('form[data-tenant-switcher-form]');
        if (form) form.submit();
    });
})();
