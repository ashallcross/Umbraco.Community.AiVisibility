import { LitElement as A, html as n, nothing as v, css as w, property as D, state as p, customElement as N } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin as P } from "@umbraco-cms/backoffice/element-api";
import { UMB_AUTH_CONTEXT as E } from "@umbraco-cms/backoffice/auth";
var L = Object.defineProperty, z = Object.getOwnPropertyDescriptor, x = (e) => {
  throw TypeError(e);
}, m = (e, t, a, s) => {
  for (var i = s > 1 ? void 0 : s ? z(t, a) : t, o = e.length - 1, d; o >= 0; o--)
    (d = e[o]) && (i = (s ? d(t, a, i) : d(i)) || i);
  return s && i && L(t, a, i), i;
}, O = (e, t, a) => t.has(e) || x("Cannot " + a), M = (e, t, a) => t.has(e) ? x("Cannot add the same private member more than once") : t instanceof WeakSet ? t.add(e) : t.set(e, a), l = (e, t, a) => (O(e, t, "access private method"), a), r, h, _, f, y, S, b, k, T, C;
const $ = "/umbraco/management/api/v1/llmstxt/settings/", F = "/umbraco/management/api/v1/llmstxt/settings/doctypes", U = "/umbraco/management/api/v1/llmstxt/settings/excluded-pages", H = 3e3;
let c = class extends P(A) {
  constructor() {
    super(...arguments), M(this, r), this._state = { kind: "loading" }, this._saveState = { kind: "idle" }, this._excludedPages = { kind: "loading" }, this._formState = {
      siteName: "",
      siteSummary: "",
      excludedDoctypeAliases: []
    }, this._initialFormState = {
      siteName: "",
      siteSummary: "",
      excludedDoctypeAliases: []
    }, this._saveToastTimer = null, this._abortController = null;
  }
  connectedCallback() {
    super.connectedCallback(), !(this._state.kind === "ready" && this._hasChanges()) && l(this, r, _).call(this);
  }
  disconnectedCallback() {
    this._saveToastTimer && (clearTimeout(this._saveToastTimer), this._saveToastTimer = null), this._abortController && (this._abortController.abort(), this._abortController = null), super.disconnectedCallback();
  }
  _canSave() {
    return this._state.kind !== "ready" || this._saveState.kind === "saving" || this._formState.siteSummary.length > this._state.settings.summaryMaxChars ? !1 : this._hasChanges();
  }
  _hasChanges() {
    if (this._formState.siteName !== this._initialFormState.siteName || this._formState.siteSummary !== this._initialFormState.siteSummary || this._formState.excludedDoctypeAliases.length !== this._initialFormState.excludedDoctypeAliases.length) return !0;
    const e = (s, i) => s.toLowerCase().localeCompare(i.toLowerCase()), t = [...this._formState.excludedDoctypeAliases].sort(e), a = [...this._initialFormState.excludedDoctypeAliases].sort(e);
    for (let s = 0; s < t.length; s += 1)
      if (t[s] !== a[s]) return !0;
    return !1;
  }
  // ────────────────────────────────────────────────────────────────────────
  // Render
  // ────────────────────────────────────────────────────────────────────────
  render() {
    if (this._state.kind === "loading")
      return n`<uui-box headline="LlmsTxt — Settings">
        <uui-loader-bar></uui-loader-bar>
        <p class="muted">Loading settings…</p>
      </uui-box>`;
    if (this._state.kind === "error")
      return n`<uui-box headline="LlmsTxt — Settings">
        <p class="error">Failed to load: ${this._state.message}</p>
        <uui-button look="secondary" @click=${() => {
        l(this, r, _).call(this);
      }}>Retry</uui-button>
      </uui-box>`;
    const { settings: e, doctypes: t } = this._state, a = this._formState.siteSummary.length, s = a > e.summaryMaxChars, i = this._canSave();
    return n`
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
            @input=${(o) => l(this, r, S).call(this, o)}
            placeholder="Override the H1 / site name on /llms.txt"
          ></uui-input>
        </div>

        <div class="field">
          <label for="siteSummary">Site summary</label>
          <uui-textarea
            id="siteSummary"
            .value=${this._formState.siteSummary}
            @input=${(o) => l(this, r, b).call(this, o)}
            placeholder="One-paragraph summary emitted as the blockquote under the H1"
          ></uui-textarea>
          <div class="counter ${s ? "over" : ""}">
            ${a} / ${e.summaryMaxChars}
          </div>
          ${s ? n`<div class="validation">Site summary cannot exceed ${e.summaryMaxChars} characters.</div>` : v}
        </div>

        <div class="field">
          <label>Excluded content types</label>
          ${t.length === 0 ? n`<p class="muted">No content types are configured yet.</p>` : n`<div class="alias-list">
                ${t.map((o) => {
      const d = this._formState.excludedDoctypeAliases.some(
        (u) => u.toLowerCase() === o.alias.toLowerCase()
      );
      return n`<label class="alias-row">
                    <input
                      type="checkbox"
                      .checked=${d}
                      @change=${(u) => l(this, r, k).call(this, o.alias, u.target.checked)}
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
            ?disabled=${!i}
            @click=${() => {
      l(this, r, y).call(this);
    }}
          >
            ${this._saveState.kind === "saving" ? n`<uui-loader></uui-loader>&nbsp;Saving…` : "Save"}
          </uui-button>
          ${l(this, r, T).call(this)}
        </div>
      </uui-box>

      <uui-box headline="Excluded pages">
        <p class="muted">
          Pages whose <code>excludeFromLlmExports</code> property is enabled
          via the <code>llmsTxtSettingsComposition</code> composition. Toggle
          per-page exclusions in the standard Umbraco content tree.
        </p>
        ${l(this, r, C).call(this)}
      </uui-box>
    `;
  }
};
r = /* @__PURE__ */ new WeakSet();
h = async function(e, t = {}) {
  this._abortController || (this._abortController = new AbortController());
  const a = this._abortController.signal;
  try {
    const s = await this.getContext(E);
    if (!s)
      return { ok: !1, status: 0, message: "Backoffice auth context unavailable. Please refresh and try again." };
    const i = s.getOpenApiConfiguration(), o = await i.token(), d = `${i.base}${e}`, u = await fetch(d, {
      method: t.method ?? "GET",
      credentials: i.credentials,
      signal: a,
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        Authorization: `Bearer ${o}`
      },
      body: t.body !== void 0 ? JSON.stringify(t.body) : void 0
    });
    if (!u.ok) {
      let g;
      try {
        g = await u.json();
      } catch {
      }
      return {
        ok: !1,
        status: u.status,
        problem: g,
        message: g?.detail ?? `HTTP ${u.status} ${u.statusText}`
      };
    }
    return { ok: !0, data: await u.json() };
  } catch (s) {
    return s instanceof DOMException && s.name === "AbortError" || a.aborted ? { ok: !1, status: 0, message: "Request aborted.", aborted: !0 } : {
      ok: !1,
      status: 0,
      message: s instanceof Error ? s.message : String(s)
    };
  }
};
_ = async function() {
  this._state = { kind: "loading" };
  const [e, t] = await Promise.all([
    l(this, r, h).call(this, $),
    l(this, r, h).call(this, F)
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
  const a = e.data, s = {
    siteName: a.siteName ?? "",
    siteSummary: a.siteSummary ?? "",
    excludedDoctypeAliases: [...a.excludedDoctypeAliases]
  };
  this._formState = s, this._initialFormState = {
    siteName: s.siteName,
    siteSummary: s.siteSummary,
    excludedDoctypeAliases: [...s.excludedDoctypeAliases]
  }, this._state = { kind: "ready", settings: a, doctypes: t.data }, l(this, r, f).call(this);
};
f = async function() {
  this._excludedPages = { kind: "loading" };
  const e = await l(this, r, h).call(this, `${U}?skip=0&take=100`);
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
  const e = await l(this, r, h).call(this, $, {
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
  const t = e.data, a = {
    siteName: t.siteName ?? "",
    siteSummary: t.siteSummary ?? "",
    excludedDoctypeAliases: [...t.excludedDoctypeAliases]
  };
  this._formState = a, this._initialFormState = {
    siteName: a.siteName,
    siteSummary: a.siteSummary,
    excludedDoctypeAliases: [...a.excludedDoctypeAliases]
  }, this._state.kind === "ready" && (this._state = { ...this._state, settings: t }), this._saveState = { kind: "success" }, this._saveToastTimer = setTimeout(() => {
    this._saveState = { kind: "idle" }, this._saveToastTimer = null;
  }, H), l(this, r, f).call(this);
};
S = function(e) {
  const t = e.target;
  this._formState = { ...this._formState, siteName: t?.value ?? "" };
};
b = function(e) {
  const t = e.target;
  this._formState = { ...this._formState, siteSummary: t?.value ?? "" };
};
k = function(e, t) {
  const a = this._formState.excludedDoctypeAliases, s = e.toLowerCase(), i = a.filter((o) => o.toLowerCase() !== s);
  this._formState = {
    ...this._formState,
    excludedDoctypeAliases: t ? [...i, e] : i
  };
};
T = function() {
  return this._saveState.kind === "success" ? n`<span class="save-success">Settings saved.</span>` : this._saveState.kind === "error" ? n`<span class="save-error">${this._saveState.message}</span>` : v;
};
C = function() {
  if (this._excludedPages.kind === "loading")
    return n`<uui-loader-bar></uui-loader-bar>`;
  if (this._excludedPages.kind === "error")
    return n`<p class="error">${this._excludedPages.message}</p>
        <uui-button look="secondary" @click=${() => {
      l(this, r, f).call(this);
    }}>Retry</uui-button>`;
  const { items: e, total: t } = this._excludedPages.page;
  return e.length === 0 ? n`<p class="muted">No pages are currently excluded from LLM exports.</p>` : n`
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
    (a) => n`<tr>
              <td>${a.name}</td>
              <td><code>${a.path || "(no URL)"}</code></td>
              <td>${a.contentTypeName}</td>
              <td>${a.culture ?? "—"}</td>
            </tr>`
  )}
        </tbody>
      </table>
    `;
};
c.styles = [
  w`
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
    `
];
m([
  D({ attribute: !1 })
], c.prototype, "manifest", 2);
m([
  p()
], c.prototype, "_state", 2);
m([
  p()
], c.prototype, "_saveState", 2);
m([
  p()
], c.prototype, "_excludedPages", 2);
m([
  p()
], c.prototype, "_formState", 2);
c = m([
  N("llms-settings-dashboard")
], c);
const G = c, q = c;
export {
  c as LlmsSettingsDashboardElement,
  q as default,
  G as element
};
//# sourceMappingURL=llms-settings-dashboard.element-dHICHNEw.js.map
