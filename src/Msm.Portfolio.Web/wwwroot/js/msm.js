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

        // ── Choosing photographs in bulk ─────────────────────────────────────────
        // Keeps the count honest, the "select all" box in step with the individual
        // ones, and the button disabled until something is actually chosen.
        (function () {
            var boxes = Array.prototype.slice.call(document.querySelectorAll('[data-pick]'));

            if (boxes.length === 0) {
                return;
            }

            var all = document.querySelector('[data-pick-all]');
            var count = document.querySelector('[data-pick-count]');
            var submit = document.querySelector('[data-pick-submit]');

            function chosen() {
                return boxes.filter(function (box) { return box.checked; }).length;
            }

            function refresh() {
                var n = chosen();

                if (count) {
                    count.textContent = n === 0
                        ? 'None chosen'
                        : n + (n === 1 ? ' chosen' : ' chosen');
                }

                if (submit) {
                    submit.disabled = n === 0;
                }

                if (all) {
                    all.checked = n === boxes.length;
                    // Neither all nor none, shown as a dash rather than a tick.
                    all.indeterminate = n > 0 && n < boxes.length;
                }
            }

            boxes.forEach(function (box) {
                box.addEventListener('change', refresh);
            });

            if (all) {
                all.addEventListener('change', function () {
                    boxes.forEach(function (box) {
                        if (!box.disabled) {
                            box.checked = all.checked;
                        }
                    });
                    refresh();
                });
            }

            refresh();
        })();

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
