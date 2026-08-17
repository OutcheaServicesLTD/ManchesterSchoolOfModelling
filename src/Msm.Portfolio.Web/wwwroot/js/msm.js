/*
 * Small behaviours that used to be inline attributes.
 *
 * The Content Security Policy allows scripts from this origin only, with no inline
 * exception — which is what makes it worth having. An `onsubmit="return confirm(...)"`
 * is an inline handler, so the browser refuses to run it and the form submits anyway:
 * the confirmation silently disappears from exactly the destructive actions it was
 * added to guard. Declaring the behaviour with a data attribute and binding it here
 * keeps the policy closed and the guard working.
 */
(function () {
    'use strict';

    document.addEventListener('DOMContentLoaded', function () {

        // ── Confirm before a destructive submit ──────────────────────────────────
        // Unpublishing, archiving, removing a photograph and permanent deletion all
        // carry one. A cancelled confirmation must stop the submission entirely.
        document.querySelectorAll('form[data-confirm]').forEach(function (form) {
            form.addEventListener('submit', function (event) {
                if (!window.confirm(form.getAttribute('data-confirm'))) {
                    event.preventDefault();
                }
            });
        });

        // ── Reload a form when a choice changes what it asks for ─────────────────
        // Onboarding asks for different measurements per profile type, so changing the
        // type has to fetch the matching fields. Without this the form renders one set
        // of fields and validates against another.
        document.querySelectorAll('[data-auto-submit]').forEach(function (control) {
            control.addEventListener('change', function () {
                if (control.form) {
                    control.form.submit();
                }
            });
        });

        // ── Copy a value to the clipboard ────────────────────────────────────────
        // The model's own portfolio address, so they can paste it into a message.
        document.querySelectorAll('[data-copy-target]').forEach(function (button) {
            button.addEventListener('click', function () {
                var target = document.getElementById(button.getAttribute('data-copy-target'));

                if (!target) {
                    return;
                }

                var restore = button.textContent;

                var done = function (message) {
                    button.textContent = message;
                    window.setTimeout(function () { button.textContent = restore; }, 2000);
                };

                // Only available over HTTPS and on a user gesture. Selecting the text is
                // the fallback, so the address is still one keystroke away from copied.
                if (navigator.clipboard && window.isSecureContext) {
                    navigator.clipboard.writeText(target.value).then(
                        function () { done('Copied'); },
                        function () { target.select(); done('Press Ctrl+C'); });
                } else {
                    target.select();
                    done('Press Ctrl+C');
                }
            });
        });
    });
})();
