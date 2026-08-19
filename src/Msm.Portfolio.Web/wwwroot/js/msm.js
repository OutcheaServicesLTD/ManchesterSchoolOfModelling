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

            // ── A suggested starting selection ───────────────────────────────────
            // Ticks boxes and stops. Nothing reaches the portfolio until the retoucher
            // has looked at the choice and pressed the button themselves, which is the
            // whole point: the ranking knows whether a photograph is sharp, and nothing
            // whatever about whether it is the right photograph.
            var suggest = document.querySelector('[data-pick-suggest]');

            if (suggest) {
                suggest.classList.remove('d-none');

                // A switch, not a one-way action. Press it and the suggestion is on and
                // the button goes green; press it again and everything is cleared —
                // including anything ticked by hand, which is the point: it is the way
                // back to an empty sheet after changing your mind halfway through.
                var suggesting = false;

                function setSuggesting(on) {
                    suggesting = on;

                    suggest.classList.toggle('btn-success', on);
                    suggest.classList.toggle('btn-outline-secondary', !on);
                    suggest.setAttribute('aria-pressed', on ? 'true' : 'false');
                    suggest.textContent = on ? 'Clear the selection' : 'Suggest a selection';
                }

                suggest.setAttribute('aria-pressed', 'false');

                suggest.addEventListener('click', function () {
                    boxes.forEach(function (box) {
                        if (!box.disabled) {
                            // Set rather than add, so the answer is the same every time
                            // rather than the selection quietly growing.
                            box.checked = !suggesting && box.hasAttribute('data-pick-suggested');
                        }
                    });

                    setSuggesting(!suggesting);
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

        // ── Choosing the crop on the cover photograph ────────────────────────────
        // The two sliders are the control and work without any of this. What is added
        // here is pointing straight at the face, and seeing the result before saving.
        (function () {
            var panel = document.querySelector('[data-focal]');

            if (!panel) {
                return;
            }

            var photo = panel.querySelector('[data-focal-photo]');
            var marker = panel.querySelector('[data-focal-marker]');
            var acrossField = panel.querySelector('[data-focal-x]');
            var downField = panel.querySelector('[data-focal-y]');
            var previews = panel.querySelectorAll('[data-focal-preview]');

            if (!photo || !acrossField || !downField) {
                return;
            }

            function clamp(value) {
                return Math.max(0, Math.min(100, Math.round(value)));
            }

            function show() {
                var across = clamp(parseInt(acrossField.value, 10) || 0);
                var down = clamp(parseInt(downField.value, 10) || 0);

                if (marker) {
                    marker.style.left = across + '%';
                    marker.style.top = down + '%';
                }

                Array.prototype.forEach.call(previews, function (preview) {
                    preview.style.objectPosition = across + '% ' + down + '%';
                });
            }

            photo.addEventListener('click', function (event) {
                var box = photo.getBoundingClientRect();

                acrossField.value = clamp(((event.clientX - box.left) / box.width) * 100);
                downField.value = clamp(((event.clientY - box.top) / box.height) * 100);

                show();
            });

            acrossField.addEventListener('input', show);
            downField.addEventListener('input', show);

            show();
        })();

        // ── Writing a biography into the About me box ────────────────────────────
        // The text lands in the box and stops there. Saving is a separate press, made by
        // the person who read it — which is what keeps the biography theirs rather than
        // something that appeared on a public page by itself.
        (function () {
            var button = document.querySelector('[data-suggest-bio]');

            if (!button) {
                return;
            }

            var box = document.getElementById(button.getAttribute('data-suggest-target'));
            var status = document.querySelector('[data-suggest-status]');

            if (!box) {
                return;
            }

            button.classList.remove('d-none');

            function say(message) {
                if (status) {
                    status.textContent = message;
                }
            }

            button.addEventListener('click', function () {
                // Losing something already typed to a button press is not a fair trade,
                // so the box being occupied is a question rather than an overwrite.
                if (box.value.trim().length > 0
                    && !window.confirm('Replace what is already in the About me box?')) {
                    return;
                }

                var restore = button.textContent;

                button.disabled = true;
                button.textContent = 'Writing…';
                say('Writing a biography. This takes a few seconds.');

                var request = new XMLHttpRequest();
                request.open('POST', button.getAttribute('data-suggest-url'));
                request.setRequestHeader('RequestVerificationToken',
                    button.getAttribute('data-suggest-token'));

                function done(message) {
                    button.disabled = false;
                    button.textContent = restore;
                    say(message);
                }

                request.addEventListener('load', function () {
                    var payload = null;

                    try {
                        payload = JSON.parse(request.responseText);
                    } catch (e) {
                        payload = null;
                    }

                    if (request.status !== 200 || payload === null) {
                        // A signed-out session answers with a sign-in page rather than
                        // JSON, so say that instead of "try again".
                        done(payload === null
                            ? 'Your session may have expired. Reload the page and sign in again.'
                            : 'That did not work.');

                        return;
                    }

                    if (!payload.succeeded) {
                        done(payload.error || 'A biography could not be written.');
                        return;
                    }

                    box.value = payload.text;
                    box.focus();
                    done('Written. Read it, change anything wrong, then press Save client details.');
                });

                request.addEventListener('error', function () {
                    done('Could not reach the server. Try again.');
                });

                request.send();
            });
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
