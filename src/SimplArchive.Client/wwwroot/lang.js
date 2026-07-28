// UI language preference (ADR "Web UI localization — shared resources"): the saved choice, else the browser's
// language, mapped to a supported code. Read by Program.cs before the first render (no live switch — set()
// persists and the caller reloads). Loaded as a plain script so window.simplArchiveLang exists before Blazor boots.
window.simplArchiveLang = {
    supported: ['en', 'de', 'it', 'es'],
    preferred: function () {
        try {
            var saved = localStorage.getItem('simplarchive.lang');
            if (saved && this.supported.indexOf(saved) >= 0) {
                return saved;
            }
            var nav = ((navigator.language || 'en').slice(0, 2)).toLowerCase();
            return this.supported.indexOf(nav) >= 0 ? nav : 'en';
        } catch (e) {
            return 'en';
        }
    },
    set: function (code) {
        try {
            localStorage.setItem('simplarchive.lang', code);
        } catch (e) { /* ignore */ }
    }
};
