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
    // Called the moment the checkbox changes, not when the dialog closes: the modal can also be dismissed by
    // clicking the backdrop, which closes it WITHOUT running any of its own handlers — so a visitor who ticked
    // the box and clicked outside lost the choice and met the promo again next visit (#427).
    setNoticeDismissed: function (dismissed) {
        if (dismissed) { localStorage.setItem('sa.desktopClientNoticeDismissed', '1'); }
        else { localStorage.removeItem('sa.desktopClientNoticeDismissed'); }
    }
};
