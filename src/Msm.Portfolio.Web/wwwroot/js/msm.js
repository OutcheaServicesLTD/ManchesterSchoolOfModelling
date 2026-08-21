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

        // ── The menu button on a phone or tablet ─────────────────────────────────
        // The section links are a row on a desktop and a panel below the bar under it.
        // Absent on every page but a portfolio, so this binds nothing elsewhere.
        (function () {
            var button = document.querySelector('[data-nav-toggle]');

            if (!button) {
                return;
            }

            var nav = document.getElementById(button.getAttribute('aria-controls'));

            if (!nav) {
                return;
            }

            var setOpen = function (open) {
                nav.classList.toggle('is-open', open);
                button.setAttribute('aria-expanded', open ? 'true' : 'false');
            };

            button.addEventListener('click', function () {
                setOpen(button.getAttribute('aria-expanded') !== 'true');
            });

            // Following a link inside the panel scrolls the page behind it, so leaving
            // the panel covering that section would be answering the tap with a menu.
            nav.addEventListener('click', function (event) {
                if (event.target.closest('a')) {
                    setOpen(false);
                }
            });

            document.addEventListener('keydown', function (event) {
                if (event.key === 'Escape' && nav.classList.contains('is-open')) {
                    setOpen(false);
                    button.focus();
                }
            });

            // Turning a phone on its side can cross into the desktop layout, where the
            // links are a row again and the panel would otherwise be left open on top
            // of them.
            var wide = window.matchMedia('(min-width: 992px)');
            var closeIfWide = function (query) {
                if (query.matches) {
                    setOpen(false);
                }
            };

            if (wide.addEventListener) {
                wide.addEventListener('change', closeIfWide);
            } else if (wide.addListener) {
                wide.addListener(closeIfWide);
            }
        })();

        // ── The photograph viewer ────────────────────────────────────────────────
        // A portfolio photograph used to open as a bare image file, which left the
        // agency looking at a page with no way to the next photograph and no way back
        // but the browser's own button. The links still work as links — this takes them
        // over only once it has run, so a viewer with scripting off loses nothing.
        (function () {
            var gallery = document.querySelector('[data-viewer]');

            if (!gallery) {
                return;
            }

            var links = Array.prototype.slice.call(
                gallery.querySelectorAll('[data-viewer-item]'));

            if (links.length === 0) {
                return;
            }

            var overlay = null;
            var photo = null;
            var strip = null;
            var count = null;
            var closeButton = null;
            var thumbs = [];
            var index = 0;
            var opener = null;

            // An icon drawn rather than typed: a multiplication sign and two chevrons in
            // a text node are read aloud by a screen reader as themselves.
            var icon = function (path) {
                var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                svg.setAttribute('viewBox', '0 0 24 24');
                svg.setAttribute('aria-hidden', 'true');
                var shape = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                shape.setAttribute('d', path);
                svg.appendChild(shape);
                return svg;
            };

            var button = function (className, label, path, onClick) {
                var element = document.createElement('button');
                element.type = 'button';
                element.className = className;
                element.setAttribute('aria-label', label);
                element.appendChild(icon(path));
                element.addEventListener('click', onClick);
                return element;
            };

            var build = function () {
                overlay = document.createElement('div');
                overlay.className = 'viewer' + (links.length === 1 ? ' is-single' : '');
                overlay.setAttribute('role', 'dialog');
                overlay.setAttribute('aria-modal', 'true');
                overlay.setAttribute('aria-label', 'Photograph viewer');

                var bar = document.createElement('div');
                bar.className = 'viewer-bar';

                count = document.createElement('p');
                count.className = 'viewer-count';
                // Announced when it changes, so moving through the shoot is audible as
                // well as visible.
                count.setAttribute('aria-live', 'polite');
                bar.appendChild(count);

                closeButton = button('viewer-close', 'Close', 'M6 6l12 12M18 6L6 18', close);
                bar.appendChild(closeButton);

                var stage = document.createElement('div');
                stage.className = 'viewer-stage';

                photo = document.createElement('img');
                photo.decoding = 'async';
                stage.appendChild(photo);

                stage.appendChild(button('viewer-prev', 'Previous photograph',
                    'M15 5l-7 7 7 7', function () { step(-1); }));
                stage.appendChild(button('viewer-next', 'Next photograph',
                    'M9 5l7 7-7 7', function () { step(1); }));

                strip = document.createElement('div');
                strip.className = 'viewer-strip';
                strip.setAttribute('role', 'group');
                strip.setAttribute('aria-label', 'Photographs in this portfolio');

                thumbs = links.map(function (link, position) {
                    var thumb = document.createElement('button');
                    thumb.type = 'button';
                    thumb.setAttribute('aria-label',
                        'Photograph ' + (position + 1) + ' of ' + links.length);

                    var picture = document.createElement('img');
                    var source = link.querySelector('img');
                    // The thumbnail rendition, not the one in the grid: the strip holds
                    // every photograph in the portfolio at 54 pixels wide, and reusing
                    // the grid's image would fetch sixty 1200-pixel files to do it.
                    picture.src = link.getAttribute('data-thumb')
                        || (source ? source.currentSrc || source.src : link.href);
                    picture.alt = '';
                    picture.loading = 'lazy';
                    thumb.appendChild(picture);

                    thumb.addEventListener('click', function () { show(position); });
                    strip.appendChild(thumb);
                    return thumb;
                });

                overlay.appendChild(bar);
                overlay.appendChild(stage);
                overlay.appendChild(strip);

                // Nothing here closes on a stray click. Clicking the space around a
                // photograph used to close the viewer, which meant a missed tap on an
                // arrow — the thing either side of that space — threw away the shoot
                // being looked through. The cross closes it, and so does Escape.

                document.body.appendChild(overlay);
            };

            var show = function (position) {
                index = (position + links.length) % links.length;

                var link = links[index];
                var source = link.querySelector('img');

                photo.src = link.href;
                photo.alt = source ? source.alt : '';
                count.textContent = (index + 1) + ' of ' + links.length;

                thumbs.forEach(function (thumb, position2) {
                    if (position2 === index) {
                        thumb.setAttribute('aria-current', 'true');
                    } else {
                        thumb.removeAttribute('aria-current');
                    }
                });

                if (thumbs[index]) {
                    thumbs[index].scrollIntoView({ block: 'nearest', inline: 'center' });
                }

                // The next one and the one before are fetched now rather than when they
                // are asked for, so pressing through a shoot does not wait on the network
                // at every step.
                [index + 1, index - 1].forEach(function (near) {
                    var neighbour = links[(near + links.length) % links.length];
                    if (neighbour !== link) {
                        new Image().src = neighbour.href;
                    }
                });
            };

            var step = function (by) {
                show(index + by);
            };

            function close() {
                if (!overlay) {
                    return;
                }

                overlay.remove();
                overlay = null;
                document.body.classList.remove('viewer-open');
                document.removeEventListener('keydown', keys, true);

                // Back to the photograph that was clicked, rather than to the top of the
                // page, so a keyboard carries on where it left off.
                if (opener) {
                    opener.focus();
                    opener = null;
                }
            }

            function keys(event) {
                if (!overlay) {
                    return;
                }

                if (event.key === 'Escape') {
                    event.preventDefault();
                    close();
                } else if (event.key === 'ArrowRight') {
                    event.preventDefault();
                    step(1);
                } else if (event.key === 'ArrowLeft') {
                    event.preventDefault();
                    step(-1);
                } else if (event.key === 'Tab') {
                    // Held inside the viewer: tabbing out of it lands on a page that
                    // cannot be seen, and the focus ring disappears with it.
                    var focusable = Array.prototype.slice.call(
                        overlay.querySelectorAll('button'));

                    if (focusable.length === 0) {
                        return;
                    }

                    var first = focusable[0];
                    var last = focusable[focusable.length - 1];

                    if (event.shiftKey && document.activeElement === first) {
                        event.preventDefault();
                        last.focus();
                    } else if (!event.shiftKey && document.activeElement === last) {
                        event.preventDefault();
                        first.focus();
                    }
                }
            }

            var open = function (position, trigger) {
                opener = trigger;
                build();
                document.body.classList.add('viewer-open');
                document.addEventListener('keydown', keys, true);
                show(position);
                closeButton.focus();
            };

            links.forEach(function (link, position) {
                link.addEventListener('click', function (event) {
                    // A middle click, or a click with a modifier held, is a request for a
                    // new tab or a download. Those are left alone.
                    if (event.button !== 0 || event.metaKey || event.ctrlKey
                        || event.shiftKey || event.altKey) {
                        return;
                    }

                    event.preventDefault();
                    open(position, link);
                });
            });

            // Swiping, because on a phone that is how photographs are moved through.
            // Only a mostly-sideways drag counts, so a scroll of the thumbnail strip is
            // not read as a request for the next photograph.
            var startX = 0;
            var startY = 0;

            document.addEventListener('touchstart', function (event) {
                if (!overlay || event.touches.length !== 1) {
                    return;
                }
                startX = event.touches[0].clientX;
                startY = event.touches[0].clientY;
            }, { passive: true });

            document.addEventListener('touchend', function (event) {
                if (!overlay || startX === 0 || event.changedTouches.length !== 1) {
                    return;
                }

                var movedX = event.changedTouches[0].clientX - startX;
                var movedY = event.changedTouches[0].clientY - startY;
                startX = 0;

                if (Math.abs(movedX) > 50 && Math.abs(movedX) > Math.abs(movedY)) {
                    step(movedX < 0 ? 1 : -1);
                }
            }, { passive: true });
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
