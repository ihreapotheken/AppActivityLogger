// Confirmation gate for destructive form submits. Any <form data-confirm="…"> is intercepted on
// submit and routed through the shared modal in _ConfirmDialog (a native <dialog>, so focus
// trapping, Escape, and the backdrop come for free). Only an explicit click on the accept button
// re-submits the form; cancel/Escape/backdrop abort.
//
// Why this and not onsubmit="return confirm(…)": the admin CSP is script-src 'self' with NO
// 'unsafe-inline' (see Admin/Program.cs), so inline event-handler attributes are blocked by the
// browser and never fire — an inline confirm() is silently dead, letting deletes through ungated.
// An external file loaded from 'self' is CSP-clean, and reading the prompt from a data-* attribute
// (set as textContent, never innerHTML) also drops the JS-string-injection risk a filename with a
// quote posed to the old inline confirm. Loaded globally from _Layout.
(function () {
    'use strict';

    var dialog = document.getElementById('confirm-modal');
    // <dialog> + requestSubmit are the same browser generation; if either is missing we leave the
    // native form submit alone rather than half-gate it. The server still requires an antiforgery
    // token + auth cookie, so an un-prompted submit is annoying, not unsafe.
    if (!dialog || typeof dialog.showModal !== 'function' || !HTMLFormElement.prototype.requestSubmit) {
        return;
    }

    var titleEl = document.getElementById('confirm-modal-title');
    var messageEl = document.getElementById('confirm-modal-message');
    var acceptBtn = dialog.querySelector('[data-confirm-accept]');
    var cancelBtn = dialog.querySelector('[data-confirm-cancel]');

    // The form awaiting a decision. Cleared on cancel/close; set true on the form itself once the
    // operator accepts so the re-fired submit passes straight through.
    var pendingForm = null;

    function close() {
        pendingForm = null;
        if (dialog.open) dialog.close();
    }

    // Capture phase + stopPropagation: this runs before any per-form submit listener (e.g. the
    // Maintenance spinner on section.card form), so a cancelled or not-yet-confirmed submit never
    // triggers downstream side effects.
    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-confirm')) return;

        // Second pass: the operator already accepted and we called requestSubmit(). Let it through.
        if (form.dataset.confirmed === '1') {
            delete form.dataset.confirmed;
            return;
        }

        e.preventDefault();
        e.stopPropagation();

        pendingForm = form;
        messageEl.textContent = form.getAttribute('data-confirm') || 'Are you sure?';
        titleEl.textContent = form.getAttribute('data-confirm-title') || 'Confirm deletion';
        acceptBtn.textContent = form.getAttribute('data-confirm-label') || 'Delete';
        dialog.showModal();
        // Default focus to Cancel so a stray Enter/Space doesn't trigger the destructive action.
        if (cancelBtn) cancelBtn.focus();
    }, true);

    acceptBtn.addEventListener('click', function () {
        var form = pendingForm;
        close();
        if (!form) return;
        form.dataset.confirmed = '1';
        form.requestSubmit(); // re-validates (e.g. the WIPE field) and re-fires submit
    });

    if (cancelBtn) cancelBtn.addEventListener('click', close);

    // Backdrop click: a click whose target is the <dialog> itself (not the inner card) lands on
    // the backdrop. Treat it as cancel.
    dialog.addEventListener('click', function (e) {
        if (e.target === dialog) close();
    });

    // Escape fires 'cancel' on a modal <dialog>; clear the pending form so it isn't submitted.
    dialog.addEventListener('cancel', function () { pendingForm = null; });
    dialog.addEventListener('close', function () { pendingForm = null; });
})();
