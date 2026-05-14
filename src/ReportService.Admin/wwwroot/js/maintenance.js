// Confirmation prompt for the Restore form. As soon as the operator selects a file the dialog
// fires; cancelling clears the input so the submit handler below never engages. A second confirm
// guards manual submit (e.g. file dropped without firing change in some browsers).
(function () {
    document.querySelectorAll('form[data-confirm-restore]').forEach(function (form) {
        var fileInput = form.querySelector('input[type="file"]');
        if (!fileInput) return;

        fileInput.addEventListener('change', function () {
            if (!fileInput.files || fileInput.files.length === 0) return;
            var f = fileInput.files[0];
            var ok = window.confirm(
                'Restore the live SQLite index from "' + f.name + '" (' + f.size + ' bytes)?\n\n' +
                'This replaces the current report index. The file is validated before any change is ' +
                'committed; if validation succeeds, the live index becomes this snapshot.'
            );
            if (!ok) fileInput.value = '';
        });

        form.addEventListener('submit', function (e) {
            if (!fileInput.files || fileInput.files.length === 0) return;
            if (!window.confirm('Proceed with restore? This is a destructive action.')) {
                e.preventDefault();
            }
        });
    });
})();

// Inline spinner + disabled state for Maintenance form submits. Most actions return via
// RedirectToPage (the reload restores the button); the Export form triggers a download
// and stays on the page, so we re-enable its button after a few seconds.
(function () {
    document.querySelectorAll('section.card form').forEach(function (form) {
        form.addEventListener('submit', function () {
            var btn = form.querySelector('button[type="submit"]');
            if (!btn || btn.disabled) return;
            btn.dataset.originalText = btn.textContent;
            btn.classList.add('is-loading');
            btn.disabled = true;
            btn.innerHTML = '<span class="spinner" aria-hidden="true"></span>Working…';
            if (form.querySelector('select[name="format"]')) {
                setTimeout(function () {
                    btn.classList.remove('is-loading');
                    btn.disabled = false;
                    btn.textContent = btn.dataset.originalText;
                }, 4000);
            }
        });
    });
})();
