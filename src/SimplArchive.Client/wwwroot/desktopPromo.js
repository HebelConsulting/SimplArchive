// Desktop-client promotion (ADR 0505): resolve the visitor's OS for the /download/clients/<os>/ link, and
// remember the one-time post-logon notice's dismissal in localStorage (device/browser-local by design).
window.simplArchiveDesktop = {
    resolveOs: function () {
        var p = ((navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || navigator.userAgent || '').toLowerCase();
        if (p.indexOf('win') >= 0) { return 'windows'; }
        if (p.indexOf('mac') >= 0 || p.indexOf('iphone') >= 0 || p.indexOf('ipad') >= 0) { return 'macos'; }
        return 'linux';
    },
    noticeDismissed: function () { return localStorage.getItem('sa.desktopClientNoticeDismissed') === '1'; },
    dismissNotice: function () { localStorage.setItem('sa.desktopClientNoticeDismissed', '1'); }
};
