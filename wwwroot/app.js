window.downloadFile = async (name, stream) => {
    const blob = new Blob([await stream.arrayBuffer()]);
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.click();
    URL.revokeObjectURL(url);
};

// The resume code and a couple of preferences live in the browser, so they survive restarts
// and can be set again at any time. Storage can be blocked, so every call is guarded.
window.riisCode = {
    get: (key) => {
        try { return localStorage.getItem(key); } catch { return null; }
    },
    set: (key, value) => {
        try { localStorage.setItem(key, value); return true; } catch { return false; }
    },
    copy: async (text) => {
        try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
    },
};
