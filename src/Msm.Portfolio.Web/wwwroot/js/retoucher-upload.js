// Batch uploader for the retoucher workspace.
//
// Each file is sent in its own request. That is what makes the behaviour specification
// section 42 asks for possible: a per-file progress bar, a failure reported against the
// file that caused it, and a retry that re-sends only that file rather than restarting
// the whole batch.
(function () {
    'use strict';

    const root = document.getElementById('uploader');
    if (!root) {
        return;
    }

    const url = root.dataset.uploadUrl;
    const token = root.dataset.token;
    const maxBytes = parseInt(root.dataset.maxBytes, 10);
    const accepted = (root.dataset.accept || '').split(',').filter(Boolean);
    const remainingAtStart = parseInt(root.dataset.remaining, 10);

    const dropZone = document.getElementById('drop-zone');
    const input = document.getElementById('file-input');
    const list = document.getElementById('upload-list');
    const summary = document.getElementById('upload-summary');

    let remaining = remainingAtStart;
    let inFlight = 0;
    let uploaded = 0;

    function announce(message) {
        // aria-live, so a screen reader hears progress rather than only seeing bars.
        summary.textContent = message;
    }

    function row(file) {
        const item = document.createElement('li');
        item.className = 'list-group-item';
        item.innerHTML =
            '<div class="d-flex justify-content-between align-items-center gap-2">' +
            '<span class="text-truncate"></span>' +
            '<span class="badge text-bg-secondary flex-shrink-0">Waiting</span>' +
            '</div>' +
            '<div class="progress mt-2" role="progressbar" aria-label="Upload progress"' +
            ' aria-valuemin="0" aria-valuemax="100" aria-valuenow="0" style="height:6px">' +
            '<div class="progress-bar" style="width:0%"></div></div>';

        item.querySelector('span.text-truncate').textContent = file.name;
        list.appendChild(item);

        return {
            element: item,
            label: item.querySelector('.badge'),
            bar: item.querySelector('.progress-bar'),
            progress: item.querySelector('.progress')
        };
    }

    function setState(ui, text, variant) {
        ui.label.textContent = text;
        ui.label.className = 'badge flex-shrink-0 text-bg-' + variant;
    }

    function addRetry(ui, file) {
        if (ui.element.querySelector('.retry')) {
            return;
        }

        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'btn btn-sm btn-outline-secondary mt-2 retry';
        button.textContent = 'Retry';
        button.addEventListener('click', function () {
            button.remove();
            // Only this file is sent again; everything already uploaded is untouched.
            // Queued rather than sent directly, so clicking several Retry buttons in a
            // row cannot put the server back under the load that failed them.
            queue(file, ui);
        });

        ui.element.appendChild(button);
    }

    function validate(file) {
        if (file.size === 0) {
            return 'This file is empty.';
        }
        if (file.size > maxBytes) {
            return 'Larger than ' + Math.round(maxBytes / 1048576) + 'MB.';
        }
        if (accepted.length && accepted.indexOf(file.type) === -1) {
            return 'That format is not supported.';
        }
        return null;
    }

    function send(file, ui) {
        setState(ui, 'Uploading', 'secondary');
        ui.progress.classList.remove('d-none');
        inFlight += 1;

        const body = new FormData();
        body.append('files', file);

        const request = new XMLHttpRequest();
        request.open('POST', url);
        request.setRequestHeader('RequestVerificationToken', token);

        request.upload.addEventListener('progress', function (event) {
            if (!event.lengthComputable) {
                return;
            }
            const percent = Math.round((event.loaded / event.total) * 100);
            ui.bar.style.width = percent + '%';
            ui.progress.setAttribute('aria-valuenow', String(percent));
        });

        request.addEventListener('load', function () {
            inFlight -= 1;
            sendFinished();

            let payload = null;
            try {
                payload = JSON.parse(request.responseText);
            } catch (e) {
                payload = null;
            }

            if (request.status !== 200) {
                // A signed-out session redirects to a sign-in page rather than
                // answering in JSON, so say so plainly instead of "try again".
                const reason = payload && payload.error
                    ? payload.error
                    : (payload === null
                        ? 'Your session may have expired. Reload the page and sign in again.'
                        : 'Upload failed. Please try again.');
                fail(ui, file, reason);
                return;
            }

            if (payload === null) {
                fail(ui, file, 'Unexpected response from the server.');
                return;
            }

            const result = payload.results && payload.results[0];

            if (!result || !result.succeeded) {
                fail(ui, file, (result && result.error) || 'This file was not accepted.');
                return;
            }

            setState(ui, 'Uploaded', 'success');
            ui.bar.style.width = '100%';
            ui.progress.setAttribute('aria-valuenow', '100');
            uploaded += 1;
            remaining -= 1;
            announce(uploaded + ' uploaded.');
            finishIfIdle();
        });

        request.addEventListener('error', function () {
            inFlight -= 1;
            sendFinished();
            fail(ui, file, 'The connection dropped. The server may have been busy — try again.');
        });

        request.send(body);
    }

    function fail(ui, file, message) {
        setState(ui, message, 'danger');
        ui.progress.classList.add('d-none');
        addRetry(ui, file);
        announce(message);
        finishIfIdle();
    }

    function finishIfIdle() {
        if (inFlight !== 0 || pending.length > 0 || uploaded === 0) {
            return;
        }

        // Reloads itself rather than revealing a button and asking for a click. The
        // page is re-rendered from what was actually stored, so the grid can never
        // drift from the library it claims to show.
        //
        // Briefly delayed so the last "Uploaded" badge is visible, and so a failed file
        // still shows its Retry button rather than vanishing on reload.
        const failures = list.querySelectorAll('.retry').length;

        if (failures > 0) {
            // Something needs attention, so the page is left alone and the manual link
            // offered instead — reloading would discard the Retry buttons.
            const manual = document.getElementById('refresh-after-upload');
            if (manual) {
                manual.classList.remove('d-none');
            }
            return;
        }

        announce(uploaded + ' uploaded. Refreshing.');
        window.setTimeout(function () { window.location.reload(); }, 700);
    }

    // Files wait here rather than all being sent at once.
    //
    // Sending a whole selection simultaneously means the server decodes several large
    // photographs at the same moment. A camera file is tens of megabytes of pixels once
    // decoded, and a modest server runs out of memory and restarts — which arrives here
    // as "the connection dropped" partway through a batch, with no indication why.
    //
    // One at a time is also kinder to a studio's upload speed, and makes each progress
    // bar mean something: with six at once they all crawl together.
    const pending = [];
    let active = 0;

    // Two at a time, not one and not all of them.
    //
    // One leaves the connection idle while the server decodes the file just sent, which
    // over a whole shoot is a lot of waiting — sixty camera photographs take 33 seconds
    // one at a time and 19 two at a time. Much beyond two and several large photographs
    // are decoded at once, which is what exhausted the server and dropped a batch once
    // before.
    //
    // Measured rather than assumed: a sixty-photograph batch of 4000x6000 files peaks at
    // 391MB against a 512MB container. It was 636MB before the allocator was told to stop
    // hoarding freed image memory — see the Dockerfile — and dropping to one at a time
    // barely touched that, because concurrency was never the thing driving it.
    const atOnce = 2;

    function pump() {
        while (active < atOnce && pending.length > 0) {
            active += 1;
            const next = pending.shift();
            send(next.file, next.ui);
        }
    }

    // Called when a request finishes, whether it succeeded or not, so one bad file
    // never strands the rest of the queue.
    function sendFinished() {
        active -= 1;
        pump();
    }

    function queue(file, ui) {
        pending.push({ file: file, ui: ui });
        setState(ui, 'Waiting', 'secondary');
        pump();
    }

    function handle(files) {
        Array.prototype.forEach.call(files, function (file) {
            const ui = row(file);
            const problem = validate(file);

            if (problem) {
                // Rejected in the browser, so an obviously invalid file never leaves
                // the machine. The server repeats every one of these checks.
                setState(ui, problem, 'danger');
                ui.progress.classList.add('d-none');
                return;
            }

            if (remaining <= 0) {
                setState(ui, 'The library is full.', 'danger');
                ui.progress.classList.add('d-none');
                return;
            }

            remaining -= 1;
            queue(file, ui);
        });
    }

    // Absent when the library is full: there is a notice where the button was. The
    // drop handlers below still run, so photographs dropped on a full library are
    // answered with a reason rather than with silence.
    if (input) {
        input.addEventListener('change', function () {
            handle(input.files);
            input.value = '';
        });
    }

    // ── Dropping photographs in ─────────────────────────────────────────────────
    //
    // The whole page is the target, not just the dashed box. Aiming a dragged handful
    // of files at one rectangle is a needless piece of precision, and a drop that lands
    // an inch outside it does not fail — the browser navigates away to the photograph
    // instead, or nothing happens at all and there is no way to tell which. Either way
    // the retoucher is left looking at a page that appears to have ignored them.
    //
    // The dashed box still lights up, so it stays obvious where the photographs are
    // going; it is the aiming that is no longer required.

    // Only drags carrying files. A portfolio tile being dragged into a new place is
    // also a drag, and hijacking it here would break reordering.
    function carriesFiles(event) {
        const transfer = event.dataTransfer;

        if (!transfer) {
            return false;
        }

        return transfer.types
            ? Array.prototype.indexOf.call(transfer.types, 'Files') !== -1
            : true;
    }

    let overCount = 0;

    function showDropping(active) {
        dropZone.classList.toggle('is-dropping', active);
    }

    // Counted rather than set and cleared: moving the pointer between two elements
    // fires dragleave on the one being left after dragenter on the one being entered,
    // so a plain flag flickers off over every boundary the pointer crosses.
    window.addEventListener('dragenter', function (event) {
        if (!carriesFiles(event)) {
            return;
        }

        event.preventDefault();
        overCount += 1;
        showDropping(true);
    });

    window.addEventListener('dragover', function (event) {
        if (!carriesFiles(event)) {
            return;
        }

        // Without this the browser refuses the drop and opens the photograph instead,
        // replacing the workspace with a picture and losing everything queued.
        event.preventDefault();
        showDropping(true);
    });

    window.addEventListener('dragleave', function (event) {
        if (!carriesFiles(event)) {
            return;
        }

        overCount = Math.max(0, overCount - 1);

        if (overCount === 0) {
            showDropping(false);
        }
    });

    window.addEventListener('drop', function (event) {
        if (!carriesFiles(event)) {
            return;
        }

        event.preventDefault();
        overCount = 0;
        showDropping(false);

        if (event.dataTransfer.files && event.dataTransfer.files.length > 0) {
            handle(event.dataTransfer.files);

            // The list of what is happening lives further down the page on a long
            // library, and a drop that scrolls nothing looks like a drop that did
            // nothing.
            dropZone.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        }
    });
})();
