import { LitElement as E, html as l, nothing as v, css as P, property as O, state as h, customElement as z } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin as U } from "@umbraco-cms/backoffice/element-api";
import { UMB_AUTH_CONTEXT as L } from "@umbraco-cms/backoffice/auth";
import { UMB_CURRENT_USER_CONTEXT as I } from "@umbraco-cms/backoffice/current-user";
import { firstValueFrom as M, filter as R } from "@umbraco-cms/backoffice/external/rxjs";
var F = Object.defineProperty, q = Object.getOwnPropertyDescriptor, y = (e) => {
  throw TypeError(e);
}, d = (e, t, s, a) => {
  for (var i = a > 1 ? void 0 : a ? q(t, s) : t, n = e.length - 1, m; n >= 0; n--)
    (m = e[n]) && (i = (a ? m(t, s, i) : m(i)) || i);
  return a && i && F(t, s, i), i;
}, B = (e, t, s) => t.has(e) || y("Cannot " + s), G = (e, t, s) => t.has(e) ? y("Cannot add the same private member more than once") : t instanceof WeakSet ? t.add(e) : t.set(e, s), o = (e, t, s) => (B(e, t, "access private method"), s), r, p, _, f, b, x, S, k, T, C, $, A, D;
const N = "/umbraco/management/api/v1/llmstxt/settings/", H = "/umbraco/management/api/v1/llmstxt/settings/doctypes", V = "/umbraco/management/api/v1/llmstxt/settings/excluded-pages", j = 3e3, w = "llms.onboarding.dismissed.v1.", X = 2e3, J = "LlmsTxt is now active and producing default output. Customise your site name and summary below, or accept the defaults — /llms.txt and /llms-full.txt are already available at your site's root.";
let u = class extends U(E) {
  constructor() {
    super(...arguments), G(this, r), this._state = { kind: "loading" }, this._saveState = { kind: "idle" }, this._excludedPages = { kind: "loading" }, this._formState = {
      siteName: "",
      siteSummary: "",
      excludedDoctypeAliases: []
    }, this._onboardingDismissed = !0, this._currentUserUnique = null, this._initialFormState = {
      siteName: "",
      siteSummary: "",
      excludedDoctypeAliases: []
    }, this._saveToastTimer = null, this._abortController = null;
  }
  connectedCallback() {
    super.connectedCallback(), o(this, r, T).call(this), !(this._state.kind === "ready" && this._hasChanges()) && o(this, r, _).call(this);
  }
  disconnectedCallback() {
    this._saveToastTimer && (clearTimeout(this._saveToastTimer), this._saveToastTimer = null), this._abortController && (this._abortController.abort(), this._abortController = null), super.disconnectedCallback();
  }
  _canSave() {
    return this._state.kind !== "ready" || this._saveState.kind === "saving" || this._formState.siteSummary.length > this._state.settings.summaryMaxChars ? !1 : this._hasChanges();
  }
  _hasChanges() {
    if (this._formState.siteName !== this._initialFormState.siteName || this._formState.siteSummary !== this._initialFormState.siteSummary || this._formState.excludedDoctypeAliases.length !== this._initialFormState.excludedDoctypeAliases.length) return !0;
    const e = (a, i) => a.toLowerCase().localeCompare(i.toLowerCase()), t = [...this._formState.excludedDoctypeAliases].sort(e), s = [...this._initialFormState.excludedDoctypeAliases].sort(e);
    for (let a = 0; a < t.length; a += 1)
      if (t[a] !== s[a]) return !0;
    return !1;
  }
  // ────────────────────────────────────────────────────────────────────────
  // Render
  // ────────────────────────────────────────────────────────────────────────
  render() {
    if (this._state.kind === "loading")
      return l`<uui-box headline="LlmsTxt — Settings">
        <uui-loader-bar></uui-loader-bar>
        <p class="muted">Loading settings…</p>
      </uui-box>`;
    if (this._state.kind === "error")
      return l`<uui-box headline="LlmsTxt — Settings">
        <p class="error">Failed to load: ${this._state.message}</p>
        <uui-button look="secondary" @click=${() => {
        o(this, r, _).call(this);
      }}>Retry</uui-button>
      </uui-box>`;
    const { settings: e, doctypes: t } = this._state, s = this._formState.siteSummary.length, a = s > e.summaryMaxChars, i = this._canSave();
    return l`
      ${o(this, r, $).call(this)}
      <uui-box headline="LlmsTxt — Settings">
        <p class="intro">
          Configure the package's site name, site summary, and the list of
          content types to omit from <code>/llms.txt</code>,
          <code>/llms-full.txt</code>, and the per-page <code>.md</code> route.
        </p>

        <div class="field">
          <label for="siteName">Site name</label>
          <uui-input
            id="siteName"
            .value=${this._formState.siteName}
            @input=${(n) => o(this, r, x).call(this, n)}
            placeholder="Override the H1 / site name on /llms.txt"
          ></uui-input>
        </div>

        <div class="field">
          <label for="siteSummary">Site summary</label>
          <uui-textarea
            id="siteSummary"
            .value=${this._formState.siteSummary}
            @input=${(n) => o(this, r, S).call(this, n)}
            placeholder="One-paragraph summary emitted as the blockquote under the H1"
          ></uui-textarea>
          <div class="counter ${a ? "over" : ""}">
            ${s} / ${e.summaryMaxChars}
          </div>
          ${a ? l`<div class="validation">Site summary cannot exceed ${e.summaryMaxChars} characters.</div>` : v}
        </div>

        <div class="field">
          <label>Excluded content types</label>
          ${t.length === 0 ? l`<p class="muted">No content types are configured yet.</p>` : l`<div class="alias-list">
                ${t.map((n) => {
      const m = this._formState.excludedDoctypeAliases.some(
        (c) => c.toLowerCase() === n.alias.toLowerCase()
      );
      return l`<label class="alias-row">
                    <input
                      type="checkbox"
                      .checked=${m}
                      @change=${(c) => o(this, r, k).call(this, n.alias, c.target.checked)}
                    />
                    <span class="alias-name">${n.name}</span>
                    <code class="alias-code">${n.alias}</code>
                  </label>`;
    })}
              </div>`}
        </div>

        <div class="actions">
          <uui-button
            look="primary"
            color="positive"
            ?disabled=${!i}
            @click=${() => {
      o(this, r, b).call(this);
    }}
          >
            ${this._saveState.kind === "saving" ? l`<uui-loader></uui-loader>&nbsp;Saving…` : "Save"}
          </uui-button>
          ${o(this, r, A).call(this)}
        </div>
      </uui-box>

      <uui-box headline="Excluded pages">
        <p class="muted">
          Pages whose <code>excludeFromLlmExports</code> property is enabled
          via the <code>llmsTxtSettingsComposition</code> composition. Toggle
          per-page exclusions in the standard Umbraco content tree.
        </p>
        ${o(this, r, D).call(this)}
      </uui-box>
    `;
  }
};
r = /* @__PURE__ */ new WeakSet();
p = async function(e, t = {}) {
  this._abortController || (this._abortController = new AbortController());
  const s = this._abortController.signal;
  try {
    const a = await this.getContext(L);
    if (!a)
      return { ok: !1, status: 0, message: "Backoffice auth context unavailable. Please refresh and try again." };
    const i = a.getOpenApiConfiguration(), n = await i.token(), m = `${i.base}${e}`, c = await fetch(m, {
      method: t.method ?? "GET",
      credentials: i.credentials,
      signal: s,
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        Authorization: `Bearer ${n}`
      },
      body: t.body !== void 0 ? JSON.stringify(t.body) : void 0
    });
    if (!c.ok) {
      let g;
      try {
        g = await c.json();
      } catch {
      }
      return {
        ok: !1,
        status: c.status,
        problem: g,
        message: g?.detail ?? `HTTP ${c.status} ${c.statusText}`
      };
    }
    return { ok: !0, data: await c.json() };
  } catch (a) {
    return a instanceof DOMException && a.name === "AbortError" || s.aborted ? { ok: !1, status: 0, message: "Request aborted.", aborted: !0 } : {
      ok: !1,
      status: 0,
      message: a instanceof Error ? a.message : String(a)
    };
  }
};
_ = async function() {
  this._state = { kind: "loading" };
  const [e, t] = await Promise.all([
    o(this, r, p).call(this, N),
    o(this, r, p).call(this, H)
  ]);
  if (!this.isConnected) return;
  if (!e.ok) {
    if (e.aborted) return;
    this._state = { kind: "error", message: e.message };
    return;
  }
  if (!t.ok) {
    if (t.aborted) return;
    this._state = { kind: "error", message: t.message };
    return;
  }
  const s = e.data, a = {
    siteName: s.siteName ?? "",
    siteSummary: s.siteSummary ?? "",
    excludedDoctypeAliases: [...s.excludedDoctypeAliases]
  };
  this._formState = a, this._initialFormState = {
    siteName: a.siteName,
    siteSummary: a.siteSummary,
    excludedDoctypeAliases: [...a.excludedDoctypeAliases]
  }, this._state = { kind: "ready", settings: s, doctypes: t.data }, o(this, r, f).call(this);
};
f = async function() {
  this._excludedPages = { kind: "loading" };
  const e = await o(this, r, p).call(this, `${V}?skip=0&take=100`);
  if (this.isConnected) {
    if (!e.ok) {
      if (e.aborted) return;
      this._excludedPages = { kind: "error", message: e.message };
      return;
    }
    this._excludedPages = { kind: "ready", page: e.data };
  }
};
b = async function() {
  if (!this._canSave())
    return;
  this._saveToastTimer && (clearTimeout(this._saveToastTimer), this._saveToastTimer = null), this._saveState = { kind: "saving" };
  const e = await o(this, r, p).call(this, N, {
    method: "PUT",
    body: {
      siteName: this._formState.siteName.length === 0 ? null : this._formState.siteName,
      siteSummary: this._formState.siteSummary.length === 0 ? null : this._formState.siteSummary,
      excludedDoctypeAliases: this._formState.excludedDoctypeAliases
    }
  });
  if (!this.isConnected) return;
  if (!e.ok) {
    if (e.aborted) return;
    this._saveState = { kind: "error", message: e.message };
    return;
  }
  const t = e.data, s = {
    siteName: t.siteName ?? "",
    siteSummary: t.siteSummary ?? "",
    excludedDoctypeAliases: [...t.excludedDoctypeAliases]
  };
  this._formState = s, this._initialFormState = {
    siteName: s.siteName,
    siteSummary: s.siteSummary,
    excludedDoctypeAliases: [...s.excludedDoctypeAliases]
  }, this._state.kind === "ready" && (this._state = { ...this._state, settings: t }), this._saveState = { kind: "success" }, this._saveToastTimer = setTimeout(() => {
    this._saveState = { kind: "idle" }, this._saveToastTimer = null;
  }, j), o(this, r, f).call(this);
};
x = function(e) {
  const t = e.target;
  this._formState = { ...this._formState, siteName: t?.value ?? "" };
};
S = function(e) {
  const t = e.target;
  this._formState = { ...this._formState, siteSummary: t?.value ?? "" };
};
k = function(e, t) {
  const s = this._formState.excludedDoctypeAliases, a = e.toLowerCase(), i = s.filter((n) => n.toLowerCase() !== a);
  this._formState = {
    ...this._formState,
    excludedDoctypeAliases: t ? [...i, e] : i
  };
};
T = async function() {
  try {
    const e = await this.getContext(I);
    if (!this.isConnected || !e)
      return;
    const t = await Promise.race([
      M(e.unique.pipe(R((i) => !!i))),
      new Promise(
        (i) => setTimeout(() => i(null), X)
      )
    ]);
    if (!this.isConnected || (this._currentUserUnique = t, !t))
      return;
    const s = w + t;
    let a = !1;
    try {
      a = localStorage.getItem(s) === "1";
    } catch {
    }
    this._onboardingDismissed = a;
  } catch {
  }
};
C = function() {
  this._onboardingDismissed = !0;
  const e = this._currentUserUnique;
  if (e)
    try {
      localStorage.setItem(w + e, "1");
    } catch {
    }
};
$ = function() {
  return this._onboardingDismissed ? v : l`
      <div class="onboarding-notice" role="status">
        <p class="onboarding-body">${J}</p>
        <uui-button
          class="onboarding-dismiss"
          look="secondary"
          @click=${() => o(this, r, C).call(this)}
        >
          Dismiss
        </uui-button>
      </div>
    `;
};
A = function() {
  return this._saveState.kind === "success" ? l`<span class="save-success">Settings saved.</span>` : this._saveState.kind === "error" ? l`<span class="save-error">${this._saveState.message}</span>` : v;
};
D = function() {
  if (this._excludedPages.kind === "loading")
    return l`<uui-loader-bar></uui-loader-bar>`;
  if (this._excludedPages.kind === "error")
    return l`<p class="error">${this._excludedPages.message}</p>
        <uui-button look="secondary" @click=${() => {
      o(this, r, f).call(this);
    }}>Retry</uui-button>`;
  const { items: e, total: t } = this._excludedPages.page;
  return e.length === 0 ? l`<p class="muted">No pages are currently excluded from LLM exports.</p>` : l`
      <p class="muted">Showing ${e.length} of ${t} excluded page${t === 1 ? "" : "s"}.</p>
      <table class="excluded-table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Path</th>
            <th>Type</th>
            <th>Culture</th>
          </tr>
        </thead>
        <tbody>
          ${e.map(
    (s) => l`<tr>
              <td>${s.name}</td>
              <td><code>${s.path || "(no URL)"}</code></td>
              <td>${s.contentTypeName}</td>
              <td>${s.culture ?? "—"}</td>
            </tr>`
  )}
        </tbody>
      </table>
    `;
};
u.styles = [
  P`
      :host {
        display: block;
        padding: var(--uui-size-layout-1, 24px);
      }
      uui-box {
        margin-bottom: var(--uui-size-layout-1, 24px);
      }
      .intro {
        margin-top: 0;
        color: var(--uui-color-text, inherit);
      }
      .muted {
        color: var(--uui-color-text-alt, #888);
        font-size: 0.9em;
      }
      .error,
      .save-error {
        color: var(--uui-color-danger, #d42054);
      }
      .save-success {
        color: var(--uui-color-positive, #2bc37c);
      }
      .field {
        margin-bottom: var(--uui-size-space-4, 16px);
      }
      .field label {
        display: block;
        font-weight: 600;
        margin-bottom: var(--uui-size-space-2, 8px);
      }
      uui-input,
      uui-textarea {
        width: 100%;
      }
      .counter {
        text-align: right;
        font-size: 0.85em;
        color: var(--uui-color-text-alt, #888);
        margin-top: var(--uui-size-space-1, 4px);
      }
      .counter.over {
        color: var(--uui-color-danger, #d42054);
        font-weight: 600;
      }
      .validation {
        color: var(--uui-color-danger, #d42054);
        font-size: 0.9em;
        margin-top: var(--uui-size-space-1, 4px);
      }
      .alias-list {
        display: flex;
        flex-direction: column;
        gap: var(--uui-size-space-2, 8px);
        max-height: 320px;
        overflow-y: auto;
        border: 1px solid var(--uui-color-divider, #e6e6e6);
        padding: var(--uui-size-space-3, 12px);
        border-radius: var(--uui-border-radius, 3px);
      }
      .alias-row {
        display: flex;
        align-items: center;
        gap: var(--uui-size-space-2, 8px);
        cursor: pointer;
      }
      .alias-name {
        flex: 1;
      }
      .alias-code {
        font-family: var(--uui-font-monospace, monospace);
        color: var(--uui-color-text-alt, #888);
        font-size: 0.85em;
      }
      .actions {
        display: flex;
        align-items: center;
        gap: var(--uui-size-space-3, 12px);
        margin-top: var(--uui-size-space-4, 16px);
      }
      .excluded-table {
        width: 100%;
        border-collapse: collapse;
      }
      .excluded-table th,
      .excluded-table td {
        text-align: left;
        padding: var(--uui-size-space-2, 8px);
        border-bottom: 1px solid var(--uui-color-divider, #e6e6e6);
      }
      .excluded-table code {
        font-family: var(--uui-font-monospace, monospace);
        font-size: 0.9em;
      }
      /* Story 3.3 — onboarding notice. Inline info banner above the dashboard;
         per-user dismiss via localStorage keyed by Backoffice user unique. */
      .onboarding-notice {
        display: flex;
        align-items: flex-start;
        gap: var(--uui-size-space-3, 12px);
        padding: var(--uui-size-space-4, 16px);
        margin-bottom: var(--uui-size-layout-1, 24px);
        background: var(--uui-color-surface-alt, #f3f6fb);
        border-left: 4px solid var(--uui-color-focus, #3879ff);
        border-radius: var(--uui-border-radius, 3px);
      }
      .onboarding-body {
        flex: 1;
        margin: 0;
        line-height: 1.4;
      }
      .onboarding-dismiss {
        flex-shrink: 0;
      }
    `
];
d([
  O({ attribute: !1 })
], u.prototype, "manifest", 2);
d([
  h()
], u.prototype, "_state", 2);
d([
  h()
], u.prototype, "_saveState", 2);
d([
  h()
], u.prototype, "_excludedPages", 2);
d([
  h()
], u.prototype, "_formState", 2);
d([
  h()
], u.prototype, "_onboardingDismissed", 2);
d([
  h()
], u.prototype, "_currentUserUnique", 2);
u = d([
  z("aiv-settings-dashboard")
], u);
const te = u, se = u;
export {
  u as AiVisibilitySettingsDashboardElement,
  se as default,
  te as element
};
//# sourceMappingURL=aiv-settings-dashboard.element-D4iVnL7A.js.map
