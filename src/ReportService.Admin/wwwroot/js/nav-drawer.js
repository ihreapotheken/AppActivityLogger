// Mobile navigation drawer. On small screens the sidebar collapses off-canvas; the mobile top bar's
// hamburger toggles it. Desktop is unaffected (the toggle/backdrop are display:none there). CSP-safe:
// state is a body class, no inline styles. Loaded globally from _Layout.
(function () {
    'use strict';

    var toggle = document.querySelector('.nav-toggle');
    if (!toggle) return;
    var body = document.body;
    var backdrop = document.querySelector('.nav-backdrop');

    function setOpen(open) {
        body.classList.toggle('nav-open', open);
        toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
        if (backdrop) backdrop.hidden = !open;
    }

    toggle.addEventListener('click', function () {
        setOpen(!body.classList.contains('nav-open'));
    });

    if (backdrop) {
        backdrop.addEventListener('click', function () { setOpen(false); });
    }

    // Tapping a destination closes the drawer; Escape closes it too.
    var sidebar = document.getElementById('app-sidebar');
    if (sidebar) {
        sidebar.addEventListener('click', function (e) {
            if (e.target.closest('a')) setOpen(false);
        });
    }
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') setOpen(false);
    });

    // If the viewport grows back to desktop, drop the drawer state so nothing is stuck open.
    window.addEventListener('resize', function () {
        if (window.innerWidth > 768 && body.classList.contains('nav-open')) setOpen(false);
    });
})();
