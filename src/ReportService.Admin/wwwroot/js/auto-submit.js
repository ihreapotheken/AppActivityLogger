// Auto-submit any form with [data-auto-submit]: selects + datetime-local fire immediately,
// text inputs debounce so we don't reload the page on every keystroke.
(function () {
    'use strict';
    var forms = document.querySelectorAll('form[data-auto-submit]');
    if (forms.length === 0) return;

    var DEBOUNCE_MS = 400;

    forms.forEach(function (form) {
        var timer = null;

        function submitNow() {
            if (timer) { clearTimeout(timer); timer = null; }
            // Strip empty fields so the URL stays readable.
            Array.prototype.forEach.call(form.querySelectorAll('input, select'), function (el) {
                if (el.disabled) return;
                if (!el.value || el.value === '') {
                    el.disabled = true;
                    // Re-enable for any subsequent navigation away from this submit.
                    setTimeout(function () { el.disabled = false; }, 0);
                }
            });
            form.submit();
        }

        function submitDebounced() {
            if (timer) clearTimeout(timer);
            timer = setTimeout(submitNow, DEBOUNCE_MS);
        }

        form.addEventListener('change', function (e) {
            var t = e.target;
            if (!t) return;
            if (t.tagName === 'SELECT' || (t.tagName === 'INPUT' && t.type === 'datetime-local')) {
                submitNow();
            }
        });

        form.addEventListener('input', function (e) {
            var t = e.target;
            if (!t || t.tagName !== 'INPUT') return;
            if (t.type === 'datetime-local' || t.type === 'submit' || t.type === 'button') return;
            submitDebounced();
        });

        // Pressing Enter inside a text input still submits immediately (default browser behavior).
    });
})();
