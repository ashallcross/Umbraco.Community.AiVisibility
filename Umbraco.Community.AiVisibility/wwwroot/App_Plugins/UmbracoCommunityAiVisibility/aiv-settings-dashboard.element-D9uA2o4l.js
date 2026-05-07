import { LitElement as w, html as l, nothing as _, css as E, property as P, state as d, customElement as O } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin as U } from "@umbraco-cms/backoffice/element-api";
import { UMB_AUTH_CONTEXT as z } from "@umbraco-cms/backoffice/auth";
import { UMB_CURRENT_USER_CONTEXT as I } from "@umbraco-cms/backoffice/current-user";
import { firstValueFrom as L, filter as M } from "@umbraco-cms/backoffice/external/rxjs";
import { a as R, A as F } from "./authenticated-fetch-Dotc30dn.js";
var q = Object.defineProperty, V = Object.getOwnPropertyDescriptor, v = (e) => {
  throw TypeError(e);
}, c = (e, t, i, s) => {
  for (var r = s > 1 ? void 0 : s ? V(t, i) : t, o = e.length - 1, m; o >= 0; o--)
    (m = e[o]) && (r = (s ? m(t, i, r) : m(r)) || r);
  return s && r && q(t, i, r), r;
}, B = (e, t, i) => t.has(e) || v("Cannot " + i), H = (e, t, i) => t.has(e) ? v("Cannot add the same private member more than once") : t instanceof WeakSet ? t.add(e) : t.set(e, i), n = (e, t, i) => (B(e, t, "access private method"), i), a, h, g, p, y, b, x, S, k, T, C, A, D;
const $ = "/umbraco/management/api/v1/aivisibility/settings/", G = "/umbraco/management/api/v1/aivisibility/settings/doctypes", X = "/umbraco/management/api/v1/aivisibility/settings/excluded-pages", j = 3e3, N = "aivisibility.onboarding.dismissed.v1.", W = 2e3, Y = "AI Visibility is now active and producing default output. Customise your site name and summary below, or accept the defaults — /llms.txt and /llms-full.txt are already available at your site's root.";
let u = class extends U(w) {
  constructor() {
    super(...arguments), H(this, a), this._state = { kind: "loading" }, this._saveState = { kind: "idle" }, this._excludedPages = { kind: "loading" }, this._formState = {
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
    super.connectedCallback(), n(this, a, k).call(this), !(this._state.kind === "ready" && this._hasChanges()) && n(this, a, g).call(this);
  }
  disconnectedCallback() {
    this._saveToastTimer && (clearTimeout(this._saveToastTimer), this._saveToastTimer = null), this._abortController && (this._abortController.abort(), this._abortController = null), super.disconnectedCallback();
  }
  _canSave() {
    return this._state.kind !== "ready" || this._saveState.kind === "saving" || this._formState.siteSummary.length > this._state.settings.summaryMaxChars ? !1 : this._hasChanges();
  }
  _hasChanges() {
    if (this._formState.siteName !== this._initialFormState.siteName || this._formState.siteSummary !== this._initialFormState.siteSummary || this._formState.excludedDoctypeAliases.length !== this._initialFormState.excludedDoctypeAliases.length) return !0;
    const e = (s, r) => s.toLowerCase().localeCompare(r.toLowerCase()), t = [...this._formState.excludedDoctypeAliases].sort(e), i = [...this._initialFormState.excludedDoctypeAliases].sort(e);
    for (let s = 0; s < t.length; s += 1)
      if (t[s] !== i[s]) return !0;
    return !1;
  }
  // ────────────────────────────────────────────────────────────────────────
  // Render
  // ────────────────────────────────────────────────────────────────────────
  render() {
    if (this._state.kind === "loading")
      return l`<uui-box headline="AI Visibility — Settings">
        <uui-loader-bar></uui-loader-bar>
        <p class="muted">Loading settings…</p>
      </uui-box>`;
    if (this._state.kind === "error")
      return l`<uui-box headline="AI Visibility — Settings">
        <p class="error">Failed to load: ${this._state.message}</p>
        <uui-button look="secondary" @click=${() => {
        n(this, a, g).call(this);
      }}>Retry</uui-button>
      </uui-box>`;
    const { settings: e, doctypes: t } = this._state, i = this._formState.siteSummary.length, s = i > e.summaryMaxChars, r = this._canSave();
    return l`
      ${n(this, a, C).call(this)}
      <uui-box headline="AI Visibility — Settings">
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
            @input=${(o) => n(this, a, b).call(this, o)}
            placeholder="Override the H1 / site name on /llms.txt"
          ></uui-input>
        </div>

        <div class="field">
          <label for="siteSummary">Site summary</label>
          <uui-textarea
            id="siteSummary"
            .value=${this._formState.siteSummary}
            @input=${(o) => n(this, a, x).call(this, o)}
            placeholder="One-paragraph summary emitted as the blockquote under the H1"
          ></uui-textarea>
          <div class="counter ${s ? "over" : ""}">
            ${i} / ${e.summaryMaxChars}
          </div>
          ${s ? l`<div class="validation">Site summary cannot exceed ${e.summaryMaxChars} characters.</div>` : _}
        </div>

        <div class="field">
          <label>Excluded content types</label>
          ${t.length === 0 ? l`<p class="muted">No content types are configured yet.</p>` : l`<div class="alias-list">
                ${t.map((o) => {
      const m = this._formState.excludedDoctypeAliases.some(
        (f) => f.toLowerCase() === o.alias.toLowerCase()
      );
      return l`<label class="alias-row">
                    <input
                      type="checkbox"
                      .checked=${m}
                      @change=${(f) => n(this, a, S).call(this, o.alias, f.target.checked)}
                    />
                    <span class="alias-name">${o.name}</span>
                    <code class="alias-code">${o.alias}</code>
                  </label>`;
    })}
              </div>`}
        </div>

        <div class="actions">
          <uui-button
            look="primary"
            color="positive"
            ?disabled=${!r}
            @click=${() => {
      n(this, a, y).call(this);
    }}
          >
            ${this._saveState.kind === "saving" ? l`<uui-loader></uui-loader>&nbsp;Saving…` : "Save"}
          </uui-button>
          ${n(this, a, A).call(this)}
        </div>
      </uui-box>

      <uui-box headline="Excluded pages">
        <p class="muted">
          Pages whose <code>excludeFromLlmExports</code> property is enabled
          via the <code>llmsTxtSettingsComposition</code> composition. Toggle
          per-page exclusions in the standard Umbraco content tree.
        </p>
        ${n(this, a, D).call(this)}
      </uui-box>
    `;
  }
};
a = /* @__PURE__ */ new WeakSet();
h = async function(e, t = {}) {
  this._abortController || (this._abortController = new AbortController());
  const i = this._abortController.signal;
  try {
    const s = await R(
      () => this.getContext(z),
      e,
      {
        method: t.method,
        body: t.body,
        signal: i
      }
    );
    if (!s.ok) {
      let o;
      try {
        o = await s.json();
      } catch {
      }
      return {
        ok: !1,
        status: s.status,
        problem: o,
        message: o?.detail ?? `HTTP ${s.status} ${s.statusText}`
      };
    }
    return { ok: !0, data: await s.json() };
  } catch (s) {
    return s instanceof DOMException && s.name === "AbortError" || i.aborted ? { ok: !1, status: 0, message: "Request aborted.", aborted: !0 } : s instanceof F ? { ok: !1, status: 0, message: "Backoffice auth context unavailable. Please refresh and try again." } : {
      ok: !1,
      status: 0,
      message: s instanceof Error ? s.message : String(s)
    };
  }
};
g = async function() {
  this._state = { kind: "loading" };
  const [e, t] = await Promise.all([
    n(this, a, h).call(this, $),
    n(this, a, h).call(this, G)
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
  const i = e.data, s = {
    siteName: i.siteName ?? "",
    siteSummary: i.siteSummary ?? "",
    excludedDoctypeAliases: [...i.excludedDoctypeAliases]
  };
  this._formState = s, this._initialFormState = {
    siteName: s.siteName,
    siteSummary: s.siteSummary,
    excludedDoctypeAliases: [...s.excludedDoctypeAliases]
  }, this._state = { kind: "ready", settings: i, doctypes: t.data }, n(this, a, p).call(this);
};
p = async function() {
  this._excludedPages = { kind: "loading" };
  const e = await n(this, a, h).call(this, `${X}?skip=0&take=100`);
  if (this.isConnected) {
    if (!e.ok) {
      if (e.aborted) return;
      this._excludedPages = { kind: "error", message: e.message };
      return;
    }
    this._excludedPages = { kind: "ready", page: e.data };
  }
};
y = async function() {
  if (!this._canSave())
    return;
  this._saveToastTimer && (clearTimeout(this._saveToastTimer), this._saveToastTimer = null), this._saveState = { kind: "saving" };
  const e = await n(this, a, h).call(this, $, {
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
  const t = e.data, i = {
    siteName: t.siteName ?? "",
    siteSummary: t.siteSummary ?? "",
    excludedDoctypeAliases: [...t.excludedDoctypeAliases]
  };
  this._formState = i, this._initialFormState = {
    siteName: i.siteName,
    siteSummary: i.siteSummary,
    excludedDoctypeAliases: [...i.excludedDoctypeAliases]
  }, this._state.kind === "ready" && (this._state = { ...this._state, settings: t }), this._saveState = { kind: "success" }, this._saveToastTimer = setTimeout(() => {
    this._saveState = { kind: "idle" }, this._saveToastTimer = null;
  }, j), n(this, a, p).call(this);
};
b = function(e) {
  const t = e.target;
  this._formState = { ...this._formState, siteName: t?.value ?? "" };
};
x = function(e) {
  const t = e.target;
  this._formState = { ...this._formState, siteSummary: t?.value ?? "" };
};
S = function(e, t) {
  const i = this._formState.excludedDoctypeAliases, s = e.toLowerCase(), r = i.filter((o) => o.toLowerCase() !== s);
  this._formState = {
    ...this._formState,
    excludedDoctypeAliases: t ? [...r, e] : r
  };
};
k = async function() {
  try {
    const e = await this.getContext(I);
    if (!this.isConnected || !e)
      return;
    const t = await Promise.race([
      L(e.unique.pipe(M((r) => !!r))),
      new Promise(
        (r) => setTimeout(() => r(null), W)
      )
    ]);
    if (!this.isConnected || (this._currentUserUnique = t, !t))
      return;
    const i = N + t;
    let s = !1;
    try {
      s = localStorage.getItem(i) === "1";
    } catch {
    }
    this._onboardingDismissed = s;
  } catch {
  }
};
T = function() {
  this._onboardingDismissed = !0;
  const e = this._currentUserUnique;
  if (e)
    try {
      localStorage.setItem(N + e, "1");
    } catch {
    }
};
C = function() {
  return this._onboardingDismissed ? _ : l`
      <div class="onboarding-notice" role="status">
        <p class="onboarding-body">${Y}</p>
        <uui-button
          class="onboarding-dismiss"
          look="secondary"
          @click=${() => n(this, a, T).call(this)}
        >
          Dismiss
        </uui-button>
      </div>
    `;
};
A = function() {
  return this._saveState.kind === "success" ? l`<span class="save-success">Settings saved.</span>` : this._saveState.kind === "error" ? l`<span class="save-error">${this._saveState.message}</span>` : _;
};
D = function() {
  if (this._excludedPages.kind === "loading")
    return l`<uui-loader-bar></uui-loader-bar>`;
  if (this._excludedPages.kind === "error")
    return l`<p class="error">${this._excludedPages.message}</p>
        <uui-button look="secondary" @click=${() => {
      n(this, a, p).call(this);
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
    (i) => l`<tr>
              <td>${i.name}</td>
              <td><code>${i.path || "(no URL)"}</code></td>
              <td>${i.contentTypeName}</td>
              <td>${i.culture ?? "—"}</td>
            </tr>`
  )}
        </tbody>
      </table>
    `;
};
u.styles = [
  E`
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
c([
  P({ attribute: !1 })
], u.prototype, "manifest", 2);
c([
  d()
], u.prototype, "_state", 2);
c([
  d()
], u.prototype, "_saveState", 2);
c([
  d()
], u.prototype, "_excludedPages", 2);
c([
  d()
], u.prototype, "_formState", 2);
c([
  d()
], u.prototype, "_onboardingDismissed", 2);
c([
  d()
], u.prototype, "_currentUserUnique", 2);
u = c([
  O("aiv-settings-dashboard")
], u);
const se = u, ie = u;
export {
  u as AiVisibilitySettingsDashboardElement,
  ie as default,
  se as element
};
