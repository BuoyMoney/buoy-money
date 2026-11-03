/**
 * PasskeyAuthenticator — Web SDK for the Flutter passkeys_web plugin.
 *
 * This implements the same interface as the @corbado/flutter-passkeys bundle.js
 * using native WebAuthn browser APIs (navigator.credentials.create/get).
 *
 * The passkeys_web Dart plugin calls these methods via @JS() interop:
 *   PasskeyAuthenticator.init()
 *   PasskeyAuthenticator.register(jsonParams)
 *   PasskeyAuthenticator.login(jsonParams)
 *   PasskeyAuthenticator.cancelCurrentAuthenticatorOperation()
 *   PasskeyAuthenticator.hasPasskeySupport()
 *   PasskeyAuthenticator.isUserVerifyingPlatformAuthenticatorAvailable()
 *   PasskeyAuthenticator.isConditionalMediationAvailable()
 */
var PasskeyAuthenticator = (function () {
  'use strict';

  var abortController;

  // ── Helpers ──────────────────────────────────────

  function base64urlToBuffer(base64url) {
    var base64 = base64url.replace(/-/g, '+').replace(/_/g, '/');
    while (base64.length % 4 !== 0) base64 += '=';
    var binary = atob(base64);
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes.buffer;
  }

  function bufferToBase64url(buffer) {
    var bytes = new Uint8Array(buffer);
    var binary = '';
    for (var i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }

  function makePlatformError(code, message) {
    return JSON.stringify({ code: code, message: message, details: '' });
  }

  // ── Public API ───────────────────────────────────

  function init() {
    abortController = undefined;
  }

  function hasPasskeySupport() {
    return Boolean(window.PublicKeyCredential);
  }

  async function isUserVerifyingPlatformAuthenticatorAvailable() {
    if (!window.PublicKeyCredential) return undefined;
    try {
      return await window.PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable();
    } catch (_) {
      return undefined;
    }
  }

  async function isConditionalMediationAvailable() {
    if (!window.PublicKeyCredential) return undefined;
    try {
      return await window.PublicKeyCredential.isConditionalMediationAvailable();
    } catch (_) {
      return undefined;
    }
  }

  function cancelCurrentAuthenticatorOperation() {
    if (abortController) abortController.abort('operation aborted by user.');
  }

  async function register(paramsJson) {
    try {
      var params = JSON.parse(paramsJson);
      var publicKey = params.publicKey;

      var createOptions = {
        publicKey: {
          rp: publicKey.rp,
          user: {
            id: base64urlToBuffer(publicKey.user.id),
            name: publicKey.user.name,
            displayName: publicKey.user.displayName
          },
          challenge: base64urlToBuffer(publicKey.challenge),
          pubKeyCredParams: publicKey.pubKeyCredParams,
          timeout: publicKey.timeout,
          attestation: publicKey.attestation || 'none',
          authenticatorSelection: publicKey.authenticatorSelection,
          excludeCredentials: (publicKey.excludeCredentials || []).map(function (c) {
            return { type: c.type, id: base64urlToBuffer(c.id), transports: c.transports };
          })
        }
      };

      var credential = await navigator.credentials.create(createOptions);

      var response = {
        id: credential.id,
        rawId: bufferToBase64url(credential.rawId),
        type: credential.type,
        response: {
          clientDataJSON: bufferToBase64url(credential.response.clientDataJSON),
          attestationObject: bufferToBase64url(credential.response.attestationObject),
          transports: credential.response.getTransports ? credential.response.getTransports() : []
        }
      };

      return JSON.stringify(response);
    } catch (e) {
      if (e instanceof DOMException && e.name === 'NotAllowedError') {
        return Promise.reject(makePlatformError('cancelled', 'operation was cancelled by the user.'));
      }
      return Promise.reject(makePlatformError(e.name || 'unknown', e.message || '-'));
    }
  }

  async function login(paramsJson) {
    try {
      abortController = new AbortController();
      var params = JSON.parse(paramsJson);
      var publicKey = params.publicKey || params;

      var getOptions = {
        publicKey: {
          challenge: base64urlToBuffer(publicKey.challenge),
          rpId: publicKey.rpId,
          timeout: publicKey.timeout,
          userVerification: publicKey.userVerification,
          allowCredentials: (publicKey.allowCredentials || []).map(function (c) {
            return { type: c.type, id: base64urlToBuffer(c.id), transports: c.transports };
          })
        },
        signal: abortController.signal
      };

      var credential = await navigator.credentials.get(getOptions);

      var response = {
        id: credential.id,
        rawId: bufferToBase64url(credential.rawId),
        type: credential.type,
        response: {
          clientDataJSON: bufferToBase64url(credential.response.clientDataJSON),
          authenticatorData: bufferToBase64url(credential.response.authenticatorData),
          signature: bufferToBase64url(credential.response.signature),
          userHandle: credential.response.userHandle
            ? bufferToBase64url(credential.response.userHandle)
            : null
        }
      };

      return JSON.stringify(response);
    } catch (e) {
      if (e instanceof DOMException && e.name === 'NotAllowedError') {
        return Promise.reject(makePlatformError('cancelled', 'operation was cancelled by the user.'));
      }
      if (e === 'operation aborted by user.') {
        return Promise.reject(makePlatformError('suppressed', e));
      }
      return Promise.reject(makePlatformError(e.name || 'unknown', e.message || '-'));
    }
  }

  return {
    init: init,
    register: register,
    login: login,
    cancelCurrentAuthenticatorOperation: cancelCurrentAuthenticatorOperation,
    hasPasskeySupport: hasPasskeySupport,
    isUserVerifyingPlatformAuthenticatorAvailable: isUserVerifyingPlatformAuthenticatorAvailable,
    isConditionalMediationAvailable: isConditionalMediationAvailable
  };
})();
