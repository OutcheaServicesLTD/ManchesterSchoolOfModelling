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

        // ── Dragging photographs into a new order ────────────────────────────────
        // An addition, never a replacement: the arrow buttons on each tile remain the
        // route for anyone using a keyboard, which specification section 41 requires.
        // Without script, the tiles simply are not draggable and the arrows still work.
        (function () {
            var list = document.querySelector('[data-reorder-list]');
            var form = document.getElementById('reorder-form');

            if (!list || !form) {
                return;
            }

            var dragging = null;
            var target = null;
            var after = false;

            function items() {
                return Array.prototype.slice.call(list.querySelectorAll('[data-reorder-item]'));
            }

            // The tile the pointer is over, ignoring the one being carried.
            function tileUnder(x, y) {
                return items().filter(function (item) {
                    if (item === dragging) {
                        return false;
                    }
                    var box = item.getBoundingClientRect();
                    return x >= box.left && x <= box.right && y >= box.top && y <= box.bottom;
                })[0];
            }

            // Nothing is moved while the pointer travels; the place it would land is
            // only marked. Rearranging on every dragover shifts the whole grid under the
            // pointer, so the tile being hovered changes as a result of the last move and
            // the photograph lands somewhere nobody chose. One move, made on the drop, is
            // both predictable and easier to follow.
            function mark(item, isAfter) {
                if (target === item && after === isAfter) {
                    return;
                }

                unmark();
                target = item;
                after = isAfter;

                if (item) {
                    item.classList.add(isAfter ? 'drop-after' : 'drop-before');
                }
            }

            function unmark() {
                if (target) {
                    target.classList.remove('drop-before', 'drop-after');
                }
                target = null;
            }

            list.addEventListener('dragstart', function (event) {
                var item = event.target.closest('[data-reorder-item]');

                if (!item) {
                    return;
                }

                dragging = item;
                item.classList.add('is-dragging');

                // Firefox will not start a drag without data on the transfer.
                if (event.dataTransfer) {
                    event.dataTransfer.effectAllowed = 'move';
                    event.dataTransfer.setData('text/plain', item.dataset.assetId || '');
                }
            });

            list.addEventListener('dragover', function (event) {
                if (!dragging) {
                    return;
                }

                // Without this the browser refuses the drop and no drop event arrives.
                event.preventDefault();

                if (event.dataTransfer) {
                    event.dataTransfer.dropEffect = 'move';
                }

                var over = tileUnder(event.clientX, event.clientY);

                if (!over) {
                    unmark();
                    return;
                }

                // Past the middle of a tile means the photograph belongs after it, which
                // is what makes dragging leftwards and rightwards both feel right.
                var box = over.getBoundingClientRect();
                mark(over, (event.clientX - box.left) > box.width / 2);
            });

            list.addEventListener('drop', function (event) {
                if (!dragging || !target) {
                    return;
                }

                event.preventDefault();

                var over = target;
                var isAfter = after;

                unmark();
                list.insertBefore(dragging, isAfter ? over.nextSibling : over);

                // Saved here rather than on dragend, so a drag abandoned outside the grid
                // leaves the order exactly as it was.
                save();
            });

            list.addEventListener('dragend', function () {
                unmark();

                if (dragging) {
                    dragging.classList.remove('is-dragging');
                    dragging = null;
                }
            });

            function save() {
                // Rebuilt from the list as it now reads, so the server is told the whole
                // order rather than a description of one move.
                form.innerHTML = '';

                items().forEach(function (item) {
                    var field = document.createElement('input');
                    field.type = 'hidden';
                    field.name = 'order';
                    field.value = item.dataset.assetId;
                    form.appendChild(field);
                });

                var token = document.querySelector('input[name="__RequestVerificationToken"]');

                if (token) {
                    var copy = document.createElement('input');
                    copy.type = 'hidden';
                    copy.name = '__RequestVerificationToken';
                    copy.value = token.value;
                    form.appendChild(copy);
                }

                form.submit();
            }
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
