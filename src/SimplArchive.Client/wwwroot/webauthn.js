// WebAuthn/passkey browser helpers (ADR "WebAuthn passkeys as a second factor"). Shared by the Blazor client
// (registration) and the server-rendered /Account/Login page (authentication). Converts between Fido2NetLib's
// base64url JSON and the ArrayBuffers the WebAuthn API uses.
(function () {
    function b64urlToBuf(s) {
        s = s.replace(/-/g, '+').replace(/_/g, '/');
        const pad = s.length % 4 ? '='.repeat(4 - (s.length % 4)) : '';
        const bin = atob(s + pad);
        const buf = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) buf[i] = bin.charCodeAt(i);
        return buf.buffer;
    }

    function bufToB64url(buf) {
        const bytes = new Uint8Array(buf);
        let bin = '';
        for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
        return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    }

    // navigator.credentials.create — returns the attestation as a JSON string (Fido2NetLib shape) or throws.
    async function register(optionsJson) {
        const options = JSON.parse(optionsJson);
        options.challenge = b64urlToBuf(options.challenge);
        options.user.id = b64urlToBuf(options.user.id);
        if (options.excludeCredentials) options.excludeCredentials.forEach(c => c.id = b64urlToBuf(c.id));

        const cred = await navigator.credentials.create({ publicKey: options });
        return JSON.stringify({
            id: cred.id,
            rawId: bufToB64url(cred.rawId),
            type: cred.type,
            extensions: cred.getClientExtensionResults(),
            response: {
                attestationObject: bufToB64url(cred.response.attestationObject),
                clientDataJSON: bufToB64url(cred.response.clientDataJSON),
            },
        });
    }

    // navigator.credentials.get — returns the assertion as a JSON string (Fido2NetLib shape) or throws.
    async function authenticate(optionsJson) {
        const options = JSON.parse(optionsJson);
        options.challenge = b64urlToBuf(options.challenge);
        if (options.allowCredentials) options.allowCredentials.forEach(c => c.id = b64urlToBuf(c.id));

        const assertion = await navigator.credentials.get({ publicKey: options });
        return JSON.stringify({
            id: assertion.id,
            rawId: bufToB64url(assertion.rawId),
            type: assertion.type,
            extensions: assertion.getClientExtensionResults(),
            response: {
                authenticatorData: bufToB64url(assertion.response.authenticatorData),
                clientDataJSON: bufToB64url(assertion.response.clientDataJSON),
                signature: bufToB64url(assertion.response.signature),
                userHandle: assertion.response.userHandle ? bufToB64url(assertion.response.userHandle) : null,
            },
        });
    }

    window.simplArchiveWebAuthn = { register, authenticate, supported: !!(window.PublicKeyCredential) };
})();
