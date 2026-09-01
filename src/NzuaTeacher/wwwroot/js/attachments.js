// Вставка зі скріншота (Ctrl+V) і перетягування файлів у Blazor-компоненти.
window.nzuaAttachments = (() => {
    const MAX_BYTES = 15 * 1024 * 1024;
    const zones = new Map();
    let globalGuardBound = false;
    let pasteHandler = null;

    const readFile = (file) => new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => {
            const result = String(reader.result);
            resolve({
                name: file.name || 'зображення.png',
                mediaType: file.type || 'application/octet-stream',
                base64: result.slice(result.indexOf(',') + 1),
                tooLarge: false
            });
        };
        reader.onerror = () => reject(reader.error);
        reader.readAsDataURL(file);
    });

    const send = async (dotNetRef, fileList) => {
        const payload = [];
        for (const file of fileList) {
            if (!file) continue;
            if (file.size > MAX_BYTES) {
                payload.push({ name: file.name || 'файл', mediaType: file.type || '', base64: '', tooLarge: true });
                continue;
            }
            payload.push(await readFile(file));
        }
        if (payload.length) await dotNetRef.invokeMethodAsync('OnFilesFromBrowser', payload);
    };

    // WebView2 інакше «навігує» на перетягнутий файл і застосунок втрачає сторінку.
    const bindGlobalGuard = () => {
        if (globalGuardBound) return;
        globalGuardBound = true;
        document.addEventListener('dragover', e => e.preventDefault());
        document.addEventListener('drop', e => e.preventDefault());
    };

    return {
        register: function (zoneId, dotNetRef) {
            bindGlobalGuard();
            const zone = document.getElementById(zoneId);
            if (!zone || zones.has(zoneId)) return;

            const onDragEnter = (e) => {
                if (!e.dataTransfer || !Array.from(e.dataTransfer.types || []).includes('Files')) return;
                e.preventDefault();
                zone.classList.add('drag-over');
            };
            const onDragLeave = (e) => {
                if (e.target === zone || !zone.contains(e.relatedTarget)) zone.classList.remove('drag-over');
            };
            const onDrop = (e) => {
                if (!e.dataTransfer || !e.dataTransfer.files.length) return;
                e.preventDefault();
                e.stopPropagation();
                zone.classList.remove('drag-over');
                send(dotNetRef, Array.from(e.dataTransfer.files));
            };
            // Paste слухаємо на документі: скріншот вставляють, не клікаючи в зону.
            const onPaste = (e) => {
                const files = e.clipboardData && e.clipboardData.files;
                if (!files || !files.length) return;
                e.preventDefault();
                send(dotNetRef, Array.from(files));
            };

            zone.addEventListener('dragenter', onDragEnter);
            zone.addEventListener('dragover', onDragEnter);
            zone.addEventListener('dragleave', onDragLeave);
            zone.addEventListener('drop', onDrop);

            if (pasteHandler) document.removeEventListener('paste', pasteHandler);
            pasteHandler = onPaste;
            document.addEventListener('paste', pasteHandler);

            zones.set(zoneId, { zone, onDragEnter, onDragLeave, onDrop, onPaste });
        },

        unregister: function (zoneId) {
            const entry = zones.get(zoneId);
            if (!entry) return;
            entry.zone.removeEventListener('dragenter', entry.onDragEnter);
            entry.zone.removeEventListener('dragover', entry.onDragEnter);
            entry.zone.removeEventListener('dragleave', entry.onDragLeave);
            entry.zone.removeEventListener('drop', entry.onDrop);
            if (pasteHandler === entry.onPaste) {
                document.removeEventListener('paste', pasteHandler);
                pasteHandler = null;
            }
            zones.delete(zoneId);
        }
    };
})();
