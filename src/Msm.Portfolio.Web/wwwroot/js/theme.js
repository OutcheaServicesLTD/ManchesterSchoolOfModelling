/*
 * Light and dark switching.
 *
 * Loaded synchronously in <head>, before anything paints. A deferred script would let
 * the browser draw an ivory page and then repaint it black, which is worse than having
 * no toggle at all.
 *
 * The Content Security Policy allows scripts from this origin only, with no inline
 * exception, so this cannot be an inline <script> however small it is.
 *
 * Three states, not two: "light" and "dark" are explicit choices, and the absence of a
 * stored value means follow the operating system — which the stylesheet handles on its
 * own through prefers-color-scheme. Clearing the attribute is therefore how the site
 * hands control back to the system.
 */
(function () {
    'use strict';

    var KEY = 'msm-theme';

    function stored() {
        try {
            return window.localStorage.getItem(KEY);
        } catch (e) {
            // Private browsing, or storage disabled. Not a failure: the system
            // preference still applies, the choice simply is not remembered.
            return null;
        }
    }

    function apply(theme) {
        if (theme === 'light' || theme === 'dark') {
            document.documentElement.setAttribute('data-theme', theme);
        } else {
            document.documentElement.removeAttribute('data-theme');
        }
    }

    apply(stored());

    function systemPrefersDark() {
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
    }

    function currentlyDark() {
        var explicit = document.documentElement.getAttribute('data-theme');
        return explicit ? explicit === 'dark' : systemPrefersDark();
    }

    document.addEventListener('DOMContentLoaded', function () {
        var toggles = document.querySelectorAll('[data-theme-toggle]');

        Array.prototype.forEach.call(toggles, function (button) {
            button.addEventListener('click', function () {
                var next = currentlyDark() ? 'light' : 'dark';

                apply(next);

                try {
                    window.localStorage.setItem(KEY, next);
                } catch (e) {
                    // As above — the switch still works for this page view.
                }

                button.setAttribute(
                    'aria-label',
                    next === 'dark' ? 'Switch to light appearance' : 'Switch to dark appearance');
            });
        });
    });
})();
