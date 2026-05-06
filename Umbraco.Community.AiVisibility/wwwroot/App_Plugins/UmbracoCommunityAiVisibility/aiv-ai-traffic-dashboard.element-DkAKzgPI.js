import { LitElement as I, nothing as u, html as o, css as F, property as O, state as g, customElement as M } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin as H } from "@umbraco-cms/backoffice/element-api";
import { UMB_AUTH_CONTEXT as L } from "@umbraco-cms/backoffice/auth";
var q = Object.defineProperty, N = Object.getOwnPropertyDescriptor, D = (t) => {
  throw TypeError(t);
}, h = (t, e, a, i) => {
  for (var r = i > 1 ? void 0 : i ? N(e, a) : e, l = t.length - 1, d; l >= 0; l--)
    (d = t[l]) && (r = (i ? d(e, a, r) : d(r)) || r);
  return i && r && q(e, a, r), r;
}, j = (t, e, a) => e.has(t) || D("Cannot " + a), B = (t, e, a) => e.has(t) ? D("Cannot add the same private member more than once") : e instanceof WeakSet ? e.add(t) : e.set(t, a), n = (t, e, a) => (j(t, e, "access private method"), a), s, C, p, b, A, f, m, T, k, v, y, S, E, U, z;
class _ extends Error {
  constructor() {
    super(...arguments), this.name = "AuthContextUnavailableError";
  }
}
const G = "/umbraco/management/api/v1/aivisibility/analytics/requests", K = "/umbraco/management/api/v1/aivisibility/analytics/classifications", Q = "/umbraco/management/api/v1/aivisibility/analytics/summary", V = "/umbraco/management/api/v1/aivisibility/analytics/retention", Y = 50, W = 7, Z = 200;
function R(t) {
  switch (t) {
    case "AiTraining":
    case "AiSearchRetrieval":
    case "AiUserTriggered":
      return "primary";
    case "AiDeprecated":
      return "warning";
    case "HumanBrowser":
      return "positive";
    default:
      return "default";
  }
}
function X() {
  const t = /* @__PURE__ */ new Date(), e = Date.UTC(t.getUTCFullYear(), t.getUTCMonth(), t.getUTCDate());
  return new Date(e).toISOString();
}
function x(t) {
  return (/* @__PURE__ */ new Date(`${t}T00:00:00.000Z`)).toISOString();
}
let c = class extends H(I) {
  constructor() {
    super(...arguments), B(this, s), this._state = { kind: "loading" }, this._filter = n(this, s, C).call(this), this._retentionDays = null, this._lastClassifications = [], this._dateRangeError = null, this._inflight = null;
  }
  connectedCallback() {
    super.connectedCallback(), n(this, s, A).call(this), n(this, s, f).call(this);
  }
  disconnectedCallback() {
    this._inflight?.abort(), this._inflight = null, super.disconnectedCallback();
  }
  render() {
    return o`
      <uui-box headline="AI Traffic">
        ${this._state.kind === "loading" ? o`<uui-loader-bar></uui-loader-bar>` : u}
        ${n(this, s, E).call(this)}
        ${n(this, s, U).call(this)}
        ${this._state.kind === "error" ? o`
              <div role="alert" class="error-banner">
                <span>${this._state.message}</span>
                <uui-button
                  look="primary"
                  @click=${() => {
      n(this, s, f).call(this);
    }}
                >
                  Retry
                </uui-button>
              </div>
            ` : u}
        ${this._state.kind === "auth-error" ? o`
              <div role="alert" class="error-banner">
                <span
                  >Could not authenticate with the Backoffice — please refresh and try
                  again</span
                >
                <uui-button look="primary" @click=${() => window.location.reload()}>
                  Refresh
                </uui-button>
              </div>
            ` : u}
        ${n(this, s, z).call(this)}
      </uui-box>
    `;
  }
};
s = /* @__PURE__ */ new WeakSet();
C = function() {
  const t = /* @__PURE__ */ new Date(), e = Date.UTC(t.getUTCFullYear(), t.getUTCMonth(), t.getUTCDate()), a = new Date(e + 864e5);
  return {
    fromDate: new Date(e - W * 864e5).toISOString().slice(0, 10),
    toDate: a.toISOString().slice(0, 10),
    selectedClasses: /* @__PURE__ */ new Set(),
    page: 1,
    pageSize: Y
  };
};
p = async function(t, e) {
  const a = await this.getContext(L);
  if (!a)
    throw new _("Auth context unavailable");
  const i = a.getOpenApiConfiguration();
  let r;
  try {
    r = await i.token();
  } catch {
    throw new _("Token acquisition failed");
  }
  if (!r)
    throw new _("Token acquisition returned empty");
  return fetch(`${i.base}${t}`, {
    method: "GET",
    credentials: i.credentials,
    signal: e,
    headers: {
      Accept: "application/json",
      Authorization: `Bearer ${r}`
    }
  });
};
b = function(t) {
  const e = x(this._filter.fromDate), a = x(this._filter.toDate), i = new URLSearchParams();
  if (i.append("from", e), i.append("to", a), t) {
    for (const r of this._filter.selectedClasses) i.append("class", r);
    i.append("page", String(this._filter.page)), i.append("pageSize", String(this._filter.pageSize));
  }
  return `?${i.toString()}`;
};
A = async function() {
  try {
    const t = new AbortController(), e = await n(this, s, p).call(this, V, t.signal);
    if (!e.ok) return;
    const a = await e.json();
    this._retentionDays = a.durationDays;
  } catch {
  }
};
f = async function() {
  if (this._dateRangeError !== null) return;
  this._inflight?.abort();
  const t = new AbortController();
  this._inflight = t, this._state = { kind: "loading" };
  const e = n(this, s, b).call(this, !0), a = n(this, s, b).call(this, !1);
  try {
    const [i, r, l] = await Promise.all([
      n(this, s, p).call(this, `${G}${e}`, t.signal),
      n(this, s, p).call(this, `${K}${a}`, t.signal),
      n(this, s, p).call(this, `${Q}${a}`, t.signal)
    ]);
    if (t.signal.aborted) return;
    if (!i.ok) {
      this._state = {
        kind: "error",
        message: await n(this, s, m).call(this, i)
      };
      return;
    }
    if (!r.ok) {
      this._state = {
        kind: "error",
        message: await n(this, s, m).call(this, r)
      };
      return;
    }
    if (!l.ok) {
      this._state = {
        kind: "error",
        message: await n(this, s, m).call(this, l)
      };
      return;
    }
    const d = await i.json(), w = await r.json(), P = await l.json();
    this._lastClassifications = w, this._state = { kind: "ready", requests: d, classifications: w, summary: P };
    const $ = d.rangeFrom.slice(0, 10);
    $ !== this._filter.fromDate && (this._filter = { ...this._filter, fromDate: $ });
  } catch (i) {
    if (i?.name === "AbortError") return;
    if (i instanceof _) {
      this._state = { kind: "auth-error" };
      return;
    }
    const r = i?.message ?? "Failed to load AI Traffic data";
    this._state = { kind: "error", message: r };
  }
};
m = async function(t) {
  try {
    const e = await t.json();
    return e.detail ?? e.title ?? `HTTP ${t.status}`;
  } catch {
    return `HTTP ${t.status}`;
  }
};
T = function(t) {
  const e = t.target.value;
  this._filter = { ...this._filter, fromDate: e, page: 1 }, n(this, s, v).call(this);
};
k = function(t) {
  const e = t.target.value;
  this._filter = { ...this._filter, toDate: e, page: 1 }, n(this, s, v).call(this);
};
v = function() {
  if (this._filter.fromDate && this._filter.toDate && this._filter.fromDate > this._filter.toDate) {
    this._dateRangeError = "From date cannot be after To date";
    return;
  }
  this._dateRangeError = null, n(this, s, f).call(this);
};
y = function(t) {
  const e = new Set(this._filter.selectedClasses);
  e.has(t) ? e.delete(t) : e.add(t), this._filter = { ...this._filter, selectedClasses: e, page: 1 }, n(this, s, f).call(this);
};
S = function(t) {
  const a = t.target.current ?? 1;
  a !== this._filter.page && (this._filter = { ...this._filter, page: a }, n(this, s, f).call(this));
};
E = function() {
  const t = this._state.kind === "ready" ? this._state.summary : null, e = t ? t.totalRequests.toLocaleString() : "…", a = t?.firstSeenUtc ? new Date(t.firstSeenUtc).toLocaleString() : "—", i = t?.lastSeenUtc ? new Date(t.lastSeenUtc).toLocaleString() : "—", r = X().slice(0, 10);
  return o`
      <div class="header-row">
        <label
          >From
          <input
            type="date"
            .value=${this._filter.fromDate}
            min="2020-01-01"
            max=${r}
            @change=${n(this, s, T)}
          />
        </label>
        <label
          >To
          <input
            type="date"
            .value=${this._filter.toDate}
            min="2020-01-01"
            max=${r}
            @change=${n(this, s, k)}
          />
        </label>
        <span class="summary-line">
          Showing ${e} requests from ${a} to ${i}
        </span>
      </div>
      ${this._dateRangeError !== null ? o`<uui-form-validation-message>${this._dateRangeError}</uui-form-validation-message>` : u}
    `;
};
U = function() {
  const t = this._state.kind === "ready" ? this._state.classifications : this._lastClassifications;
  return t.length === 0 ? this._state.kind === "ready" ? o`<div class="chips muted">No classifications recorded for this range.</div>` : u : o`
      <div class="chips" role="group" aria-label="Filter by user-agent classification">
        ${t.map((e) => {
    const a = this._filter.selectedClasses.has(e.class), i = a ? "primary" : "outline", r = R(e.class);
    return o`
            <uui-tag
              class="chip"
              tabindex="0"
              role="button"
              aria-pressed=${a ? "true" : "false"}
              look=${i}
              color=${r}
              @click=${() => n(this, s, y).call(this, e.class)}
              @keydown=${(l) => {
      (l.key === "Enter" || l.key === " ") && (l.preventDefault(), n(this, s, y).call(this, e.class));
    }}
            >
              ${e.class} (${e.count.toLocaleString()})
            </uui-tag>
          `;
  })}
      </div>
    `;
};
z = function() {
  if (this._state.kind !== "ready") return u;
  const t = this._state.requests;
  if (t.total === 0) {
    const e = new Date(t.rangeFrom), a = this._retentionDays !== null && this._retentionDays > 0 && e < new Date(Date.now() - this._retentionDays * 864e5);
    return o`
        <div role="status" class="empty-state">
          <p>
            No AI traffic recorded yet for this filter. The package is logging — check back
            later.
          </p>
          ${a ? o`<p class="retention-hint">
                Retention is configured to ${this._retentionDays} days; older data is not
                available.
              </p>` : u}
        </div>
      `;
  }
  return o`
      <uui-table>
        <uui-table-head>
          <uui-table-head-cell>Timestamp</uui-table-head-cell>
          <uui-table-head-cell>Path</uui-table-head-cell>
          <uui-table-head-cell>Content Key</uui-table-head-cell>
          <uui-table-head-cell>Culture</uui-table-head-cell>
          <uui-table-head-cell>UA Class</uui-table-head-cell>
          <uui-table-head-cell>Referrer</uui-table-head-cell>
        </uui-table-head>
        ${t.items.map((e) => {
    const a = e.path.length > 80 ? `${e.path.slice(0, 77)}…` : e.path, i = e.contentKey ? `${e.contentKey.slice(0, 8)}…` : "—", r = e.referrerHost ?? "—";
    return o`
            <uui-table-row title=${e.createdUtc}>
              <uui-table-cell>${new Date(e.createdUtc).toLocaleString()}</uui-table-cell>
              <uui-table-cell title=${e.path}>${a}</uui-table-cell>
              <uui-table-cell title=${e.contentKey ?? ""}>${i}</uui-table-cell>
              <uui-table-cell>${e.culture || "—"}</uui-table-cell>
              <uui-table-cell>
                <uui-tag look="primary" color=${R(e.userAgentClass)}>
                  ${e.userAgentClass}
                </uui-tag>
              </uui-table-cell>
              <uui-table-cell>${r}</uui-table-cell>
            </uui-table-row>
          `;
  })}
      </uui-table>
      ${t.totalCappedAt ? o`
            <div class="cap-footer">
              Showing first ${t.totalCappedAt.toLocaleString()} results — narrow your
              date range or add a classification filter to see more.
            </div>
          ` : u}
      ${t.totalPages > 1 ? o`
            <uui-pagination
              total=${Math.min(t.totalPages, Z)}
              current=${t.page}
              @change=${n(this, s, S)}
            ></uui-pagination>
          ` : u}
    `;
};
c.styles = [
  F`
      :host {
        display: block;
        padding: var(--uui-size-layout-1, 24px);
      }
      .header-row {
        display: flex;
        flex-wrap: wrap;
        gap: var(--uui-size-space-4, 16px);
        align-items: end;
        margin-bottom: var(--uui-size-space-4, 16px);
      }
      .header-row label {
        display: flex;
        flex-direction: column;
        font-size: var(--uui-size-3, 14px);
        color: var(--uui-color-text-alt, #6c7077);
      }
      .header-row input[type="date"] {
        margin-top: 4px;
        padding: 6px 8px;
        border: 1px solid var(--uui-color-border, #d8d7d9);
        border-radius: 3px;
      }
      .summary-line {
        font-size: var(--uui-size-3, 14px);
        color: var(--uui-color-text-alt, #6c7077);
        margin-left: auto;
      }
      .chips {
        display: flex;
        flex-wrap: wrap;
        gap: var(--uui-size-space-2, 8px);
        margin-bottom: var(--uui-size-space-4, 16px);
      }
      .chips.muted {
        color: var(--uui-color-text-alt, #6c7077);
        font-style: italic;
      }
      .chip {
        cursor: pointer;
      }
      .empty-state {
        padding: var(--uui-size-space-6, 24px);
        text-align: center;
        color: var(--uui-color-text-alt, #6c7077);
      }
      .retention-hint {
        font-size: var(--uui-size-3, 14px);
        color: var(--uui-color-text-alt, #6c7077);
        margin-top: var(--uui-size-space-2, 8px);
      }
      .error-banner {
        display: flex;
        gap: var(--uui-size-space-3, 12px);
        align-items: center;
        padding: var(--uui-size-space-3, 12px);
        border: 1px solid var(--uui-color-danger, #d42054);
        background: var(--uui-color-danger-emphasis, rgba(212, 32, 84, 0.08));
        border-radius: 3px;
        margin-bottom: var(--uui-size-space-4, 16px);
      }
      .cap-footer {
        font-size: var(--uui-size-3, 14px);
        color: var(--uui-color-text-alt, #6c7077);
        margin-top: var(--uui-size-space-2, 8px);
        font-style: italic;
      }
      uui-table {
        margin-top: var(--uui-size-space-2, 8px);
      }
      uui-pagination {
        display: block;
        margin-top: var(--uui-size-space-4, 16px);
      }
    `
];
h([
  O({ attribute: !1 })
], c.prototype, "manifest", 2);
h([
  g()
], c.prototype, "_state", 2);
h([
  g()
], c.prototype, "_filter", 2);
h([
  g()
], c.prototype, "_retentionDays", 2);
h([
  g()
], c.prototype, "_lastClassifications", 2);
h([
  g()
], c.prototype, "_dateRangeError", 2);
c = h([
  M("aiv-ai-traffic-dashboard")
], c);
const at = c, it = c;
export {
  c as AiVisibilityAiTrafficDashboardElement,
  it as default,
  at as element
};
//# sourceMappingURL=aiv-ai-traffic-dashboard.element-DkAKzgPI.js.map
