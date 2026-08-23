// Drag-and-drop / click-to-browse document upload for the Browse page — see ADR
// "Drag-and-drop document upload". Both the folder rows in the tree and the detail-pane drop zone carry a
// `data-drop-folder="<documentId>"` attribute; a single delegated handler on the Browse root wires them all
// (and survives tree nodes being added/removed as folders expand). The browser PUTs the file bytes straight
// to the presigned MinIO URL (never proxied through the Api — ADR 0006/0184); .NET does only the metadata
// API calls (create child, create version, finalize, index-data, mask).

// Custom drag MIME for an internal move/reference drag — distinguishes a node drag from an OS-file drop.
const NODE_MIME = 'application/x-simplarchive-node';

// Returns true once wired (or already wired), false if `root` isn't a real element yet — the workbench DOM
// lives inside <Authorized>, so on a page reload auth may still be resolving when this is first called.
export function initDropRoot(root, dotNetRef) {
    if (!root || typeof root.addEventListener !== 'function') {
        return false;
    }
    if (root._dropWired) {
        return true;
    }
    root._dropWired = true;

    let active = null;
    const setActive = (el) => {
        if (active === el) {
            return;
        }
        active?.classList.remove('drop-target-active');
        active = el;
        active?.classList.add('drop-target-active');
    };

    // An internal move/reference drag (a list row or a tree folder) carries our custom MIME type "<id>|<isRef>";
    // an OS-file drag carries "Files". The two paths never mix. See ADR "Desktop drag-and-drop move and reference".
    const isInternalDrag = (e) => e.dataTransfer && [...e.dataTransfer.types].includes(NODE_MIME);

    root.addEventListener('dragstart', (e) => {
        const src = e.target.closest('[data-node-id]');
        if (src) {
            const isRef = src.getAttribute('data-node-ref') === 'true';
            e.dataTransfer.setData(NODE_MIME, `${src.getAttribute('data-node-id')}|${isRef}`);
            e.dataTransfer.effectAllowed = 'copyMove';
        }
    });

    root.addEventListener('dragover', (e) => {
        // Internal drag: only a folder (data-drop-folder) accepts it — never a document row.
        if (isInternalDrag(e)) {
            // Personal ▸ Intray takes a document as a TEMPLATE — a copy, not a move, so the effect says 'copy'.
            const template = e.target.closest('[data-drop-intray]');
            if (template) {
                e.preventDefault();
                e.dataTransfer.dropEffect = 'copy';
                setActive(template);
                return;
            }

            const folder = e.target.closest('[data-drop-folder]');
            if (folder) {
                e.preventDefault();
                e.dataTransfer.dropEffect = 'move';
                setActive(folder);
            }
            return;
        }
        // The Personal launchers accept an OS-file drag; the work itself happens on `drop`, never here —
        // dragover fires continuously while the pointer moves (#467).
        const launcher = e.target.closest('[data-drop-intray]') || e.target.closest('[data-drop-checkout]');
        if (launcher) {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
            setActive(launcher);
            return;
        }

        // OS-file drag: a document row (data-drop-doc) offers the filing dialog; a folder row files into it.
        const target = e.target.closest('[data-drop-doc]') || e.target.closest('[data-drop-folder]');
        if (target) {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
            setActive(target);
        }
    });

    root.addEventListener('dragleave', (e) => {
        // Only clear when the pointer actually left the Browse root (not when moving between child elements).
        if (!root.contains(e.relatedTarget)) {
            setActive(null);
        }
    });

    root.addEventListener('drop', async (e) => {
        // Internal move/reference drag: hand the dragged node + target folder to .NET, which prompts + bulk-moves.
        if (isInternalDrag(e)) {
            const [draggedId] = (e.dataTransfer.getData(NODE_MIME) || '').split('|');

            // Copy an existing document into the intray as a template, carrying its mask + index values (#467).
            const template = e.target.closest('[data-drop-intray]');
            if (template) {
                e.preventDefault();
                setActive(null);
                if (draggedId) {
                    await dotNetRef.invokeMethodAsync('CopyDocumentToIntrayAsync', draggedId);
                }
                return;
            }

            const folder = e.target.closest('[data-drop-folder]');
            setActive(null);
            if (!folder) {
                return;
            }
            e.preventDefault();
            const [nodeId, isRef] = (e.dataTransfer.getData(NODE_MIME) || '').split('|');
            if (nodeId) {
                await dotNetRef.invokeMethodAsync('PerformNodeDropAsync', folder.getAttribute('data-drop-folder'), nodeId, isRef === 'true');
            }
            return;
        }

        // Personal ▸ Check-out takes an edited working copy back, matched to a checked-out document BY NAME;
        // Personal ▸ Intray takes the files as intray items (#467).
        const checkout = e.target.closest('[data-drop-checkout]');
        const intray = e.target.closest('[data-drop-intray]');
        if (checkout || intray) {
            e.preventDefault();
            setActive(null);
            const dropped = [...(e.dataTransfer?.files ?? [])];
            await (checkout ? uploadFilesToStash(dotNetRef, dropped) : uploadFilesToIntray(dotNetRef, dropped));
            return;
        }

        // A document row wins over its containing folder pane (closest() finds the nearest [data-drop-doc]).
        const docTarget = e.target.closest('[data-drop-doc]');
        const folderTarget = e.target.closest('[data-drop-folder]');
        if (!docTarget && !folderTarget) {
            return;
        }
        e.preventDefault();
        setActive(null);
        const files = [...(e.dataTransfer?.files ?? [])];
        if (docTarget) {
            await uploadFilesToDocument(dotNetRef, docTarget.getAttribute('data-drop-doc'), files);
        } else {
            await uploadFiles(dotNetRef, folderTarget.getAttribute('data-drop-folder'), files);
        }
    });

    // Click-to-browse: elements marked data-drop-browse open a hidden file picker.
    root.addEventListener('click', (e) => {
        const target = e.target.closest('[data-drop-browse]');
        if (!target) {
            return;
        }
        const folderId = target.getAttribute('data-drop-folder');
        pickFiles(dotNetRef, folderId);
    });

    return true;
}

// Invoked by the ribbon's Upload button (see Workbench) to open the file picker for the selected folder.
export function openFilePicker(dotNetRef, folderId) {
    pickFiles(dotNetRef, folderId);
}

// Intray file-list drop-zone (ADR "Inbox file-list drop-zone"): dropping OS files anywhere on the intray list
// uploads them straight into the S3-backed intray (presign + PUT), the same direct-to-storage model as a
// document upload but with no folder/document — the item is filed later. Idempotent + guarded like initDropRoot.
export function initIntrayDrop(zone, dotNetRef) {
    if (!zone || typeof zone.addEventListener !== 'function') {
        return false;
    }
    if (zone._intrayDropWired) {
        return true;
    }
    zone._intrayDropWired = true;

    const hasFiles = (e) => e.dataTransfer && [...e.dataTransfer.types].includes('Files');

    zone.addEventListener('dragover', (e) => {
        if (hasFiles(e)) {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
            zone.classList.add('drop-target-active');
        }
    });
    zone.addEventListener('dragleave', (e) => {
        if (!zone.contains(e.relatedTarget)) {
            zone.classList.remove('drop-target-active');
        }
    });
    zone.addEventListener('drop', async (e) => {
        if (!hasFiles(e)) {
            return;
        }
        e.preventDefault();
        zone.classList.remove('drop-target-active');
        await uploadFilesToIntray(dotNetRef, [...(e.dataTransfer.files ?? [])]);
    });

    return true;
}

async function uploadFilesToIntray(dotNetRef, files) {
    if (files.length === 0) {
        return;
    }

    await dotNetRef.invokeMethodAsync('OnIntrayUploadsStartingAsync', files.length);

    let uploaded = 0;
    for (const file of files) {
        try {
            // .NET returns a presigned intray PUT URL (POST /api/intray); the browser PUTs the bytes directly.
            const url = await dotNetRef.invokeMethodAsync('CreateIntrayUploadTargetAsync', file.name);
            if (!url) {
                continue;
            }
            const response = await fetch(url, { method: 'PUT', body: file });
            if (!response.ok) {
                await dotNetRef.invokeMethodAsync('ReportUploadFailureAsync', file.name, `storage upload failed (${response.status})`);
                continue;
            }
            uploaded++;
        } catch (err) {
            await dotNetRef.invokeMethodAsync('ReportUploadFailureAsync', file.name, String(err));
        }
    }

    await dotNetRef.invokeMethodAsync('OnIntrayUploadsCompleteAsync', uploaded);
}

// Each file is matched to a checked-out document BY NAME; one that matches nothing is reported rather than
// silently ignored, because "nothing happened" is indistinguishable from a broken feature.
async function uploadFilesToStash(dotNetRef, files) {
    if (files.length === 0) {
        return;
    }

    await dotNetRef.invokeMethodAsync('OnUploadsStartingAsync', files.length);
    let stashed = 0;
    for (const file of files) {
        try {
            const url = await dotNetRef.invokeMethodAsync('CreateStashTargetForNameAsync', file.name);
            if (!url) {
                continue;   // .NET has already said why: no checked-out document of that name
            }
            const response = await fetch(url, { method: 'PUT', body: file });
            if (!response.ok) {
                await dotNetRef.invokeMethodAsync('ReportUploadFailureAsync', file.name, `storage upload failed (${response.status})`);
                continue;
            }
            stashed++;
        } catch (err) {
            await dotNetRef.invokeMethodAsync('ReportUploadFailureAsync', file.name, String(err));
        }
    }

    await dotNetRef.invokeMethodAsync('OnStashUploadsCompleteAsync', stashed);
}

function pickFiles(dotNetRef, folderId) {
    const input = document.createElement('input');
    input.type = 'file';
    input.multiple = true;
    input.style.display = 'none';
    document.body.appendChild(input);
    input.addEventListener('change', async () => {
        const files = [...(input.files ?? [])];
        input.remove();
        await uploadFiles(dotNetRef, folderId, files);
    });
    input.click();
}

async function uploadFiles(dotNetRef, folderId, files) {
    if (!folderId || files.length === 0) {
        return;
    }

    await dotNetRef.invokeMethodAsync('OnUploadsStartingAsync', files.length);

    for (const file of files) {
        try {
            // Duplicate detection (ADR "Duplicate document detection"): hash the file's content and ask .NET whether
            // an identical document already exists. .NET shows the reference/file-anyway/cancel modal and returns:
            // 'file' → upload normally; 'referenced' → a shortcut was created instead, skip; 'cancel' → skip.
            const hash = await sha256Hex(file);
            // For an .eml, hand .NET the header region too — the shared extractor pulls the Message-ID from
            // it so byte-different copies of one message still meet in the dialog (#704). The slice is a few
            // KB; the whole file never crosses the interop boundary.
            const headerText = file.name.toLowerCase().endsWith('.eml') ? await file.slice(0, 8192).text() : null;
            const decision = await dotNetRef.invokeMethodAsync('PrepareUploadAsync', folderId, hash, file.name, headerText);
            if (decision !== 'file') {
                continue;
            }

            // .NET creates the document + a pending version and returns the presigned PUT URL.
            const target = await dotNetRef.invokeMethodAsync('CreateUploadTargetAsync', folderId, file.name);
            if (!target) {
                // .NET already surfaced the reason (e.g. duplicate name, insufficient rights).
                continue;
            }

            const response = await fetch(target.uploadUrl, { method: 'PUT', body: file });
            if (!response.ok) {
                await dotNetRef.invokeMethodAsync('ReportUploadFailureAsync', file.name, `storage upload failed (${response.status})`);
                continue;
            }

            // .NET finalizes (server-side hash), sets index data, and assigns the default mask — at the
            // address the create response advertised, which rode through on the target (ADR 0543, #416).
            await dotNetRef.invokeMethodAsync('FinalizeUploadAsync', target.finalizeHref, file.name, target.comment ?? null);
        } catch (err) {
            await dotNetRef.invokeMethodAsync('ReportUploadFailureAsync', file.name, String(err));
        }
    }

    await dotNetRef.invokeMethodAsync('OnUploadsCompleteAsync', folderId);
}

// Files dropped onto a document row (ADR "List-pane drop filing"): .NET shows the intray-style filing dialog
// (file as a new version of the document, or into a folder, with an optional comment) and returns the choice.
async function uploadFilesToDocument(dotNetRef, docId, files) {
    if (!docId || files.length === 0) {
        return;
    }

    const decision = await dotNetRef.invokeMethodAsync('BeginDocumentDropAsync', docId, files.length);
    if (!decision) {
        return; // cancelled
    }

    await dotNetRef.invokeMethodAsync('OnUploadsStartingAsync', files.length);

    if (decision.mode === 'version') {
        // File each dropped file as a new version of the target document.
        for (const file of files) {
            try {
                const target = await dotNetRef.invokeMethodAsync('CreateVersionTargetAsync', docId, file.name);
                if (!target) {
                    continue;
                }
                const response = await fetch(target.uploadUrl, { method: 'PUT', body: file });
                if (!response.ok) {
                    await dotNetRef.invokeMethodAsync('ReportUploadFailureAsync', file.name, `storage upload failed (${response.status})`);
                    continue;
                }
                await dotNetRef.invokeMethodAsync('FinalizeVersionAsync', target.finalizeHref, file.name, decision.comment);
            } catch (err) {
                await dotNetRef.invokeMethodAsync('ReportUploadFailureAsync', file.name, String(err));
            }
        }
        await dotNetRef.invokeMethodAsync('OnDocumentVersionsFiledAsync', docId);
        return;
    }

    // 'folder' — file each dropped file as a new document in the chosen folder (with the same comment).
    const folderId = decision.folderId;
    for (const file of files) {
        try {
            const hash = await sha256Hex(file);
            const dupDecision = await dotNetRef.invokeMethodAsync('PrepareUploadAsync', folderId, hash, file.name);
            if (dupDecision !== 'file') {
                continue;
            }
            const target = await dotNetRef.invokeMethodAsync('CreateUploadTargetAsync', folderId, file.name);
            if (!target) {
                continue;
            }
            const response = await fetch(target.uploadUrl, { method: 'PUT', body: file });
            if (!response.ok) {
                await dotNetRef.invokeMethodAsync('ReportUploadFailureAsync', file.name, `storage upload failed (${response.status})`);
                continue;
            }
            await dotNetRef.invokeMethodAsync('FinalizeUploadAsync', target.finalizeHref, file.name, decision.comment);
        } catch (err) {
            await dotNetRef.invokeMethodAsync('ReportUploadFailureAsync', file.name, String(err));
        }
    }
    await dotNetRef.invokeMethodAsync('OnUploadsCompleteAsync', folderId);
}

// Hex-encoded SHA-256 of a File's content — matches the server-side hash so a duplicate is detected before upload.
async function sha256Hex(file) {
    const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer());
    return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}
