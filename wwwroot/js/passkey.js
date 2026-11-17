// Passkey prompts for the patient pages: the only custom JavaScript on the site.
// A button with data-passkey="enroll" or data-passkey="sign-in" starts the ceremony. Errors show in the
// element named by data-passkey-error.
(function () {
  function toBase64Url(buffer) {
    let binary = "";
    for (const byte of new Uint8Array(buffer)) {
      binary += String.fromCharCode(byte);
    }
    return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  }

  function fromBase64Url(text) {
    const base64 = text.replace(/-/g, "+").replace(/_/g, "/");
    const binary = atob(base64 + "===".slice((base64.length + 3) % 4));
    return Uint8Array.from(binary, (character) => character.charCodeAt(0)).buffer;
  }

  // Drops nulls the server sends for unset options, which the browser APIs reject.
  function withoutNulls(value) {
    return JSON.parse(JSON.stringify(value, (key, item) => (item === null ? undefined : item)));
  }

  function creationOptions(json) {
    const options = withoutNulls(json);
    if (PublicKeyCredential.parseCreationOptionsFromJSON) {
      return PublicKeyCredential.parseCreationOptionsFromJSON(options);
    }
    options.challenge = fromBase64Url(options.challenge);
    options.user.id = fromBase64Url(options.user.id);
    options.excludeCredentials = (options.excludeCredentials || []).map((credential) => ({
      ...credential,
      id: fromBase64Url(credential.id),
    }));
    return options;
  }

  function requestOptions(json) {
    const options = withoutNulls(json);
    if (PublicKeyCredential.parseRequestOptionsFromJSON) {
      return PublicKeyCredential.parseRequestOptionsFromJSON(options);
    }
    options.challenge = fromBase64Url(options.challenge);
    options.allowCredentials = (options.allowCredentials || []).map((credential) => ({
      ...credential,
      id: fromBase64Url(credential.id),
    }));
    return options;
  }

  function attestationJson(credential) {
    return {
      id: credential.id,
      rawId: toBase64Url(credential.rawId),
      type: credential.type,
      response: {
        attestationObject: toBase64Url(credential.response.attestationObject),
        clientDataJSON: toBase64Url(credential.response.clientDataJSON),
        transports: credential.response.getTransports ? credential.response.getTransports() : [],
      },
      clientExtensionResults: credential.getClientExtensionResults(),
    };
  }

  function assertionJson(credential) {
    const response = credential.response;
    return {
      id: credential.id,
      rawId: toBase64Url(credential.rawId),
      type: credential.type,
      response: {
        authenticatorData: toBase64Url(response.authenticatorData),
        clientDataJSON: toBase64Url(response.clientDataJSON),
        signature: toBase64Url(response.signature),
        userHandle: response.userHandle ? toBase64Url(response.userHandle) : null,
      },
      clientExtensionResults: credential.getClientExtensionResults(),
    };
  }

  async function postJson(url, body) {
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "same-origin",
      body: JSON.stringify(body || {}),
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(data.error || "Something went wrong. Please try again.");
    }
    return data;
  }

  async function enroll(button) {
    const form = button.form;
    const options = await postJson("/api/patient/passkey/create-registration-options", {
      inviteCode: form.elements.inviteCode.value,
      displayName: form.elements.displayName.value,
    });
    const credential = await navigator.credentials.create({ publicKey: creationOptions(options) });
    return postJson("/api/patient/passkey/complete-registration", {
      attestation: attestationJson(credential),
      appUris: Array.from(form.querySelectorAll('input[name="appUris"]:checked')).map(
        (checkbox) => checkbox.value
      ),
    });
  }

  async function signIn() {
    const options = await postJson("/api/patient/passkey/create-sign-in-options");
    const credential = await navigator.credentials.get({ publicKey: requestOptions(options) });
    return postJson("/api/patient/passkey/complete-sign-in", {
      assertion: assertionJson(credential),
    });
  }

  for (const button of document.querySelectorAll("[data-passkey]")) {
    button.addEventListener("click", async (event) => {
      event.preventDefault();
      const error = document.getElementById(button.dataset.passkeyError);
      if (button.form && !button.form.reportValidity()) {
        return;
      }
      error.textContent = "";
      button.disabled = true;
      try {
        const result = button.dataset.passkey === "enroll" ? await enroll(button) : await signIn();
        window.location.assign(result.redirectTo);
      } catch (failure) {
        // The phone's own cancel arrives as NotAllowedError; anything else is the server's message.
        error.textContent =
          failure.name === "NotAllowedError" ? "Passkey cancelled. Try again." : failure.message;
        button.disabled = false;
      }
    });
  }
})();
