import { LitElement as P, nothing as u, html as l, css as I, property as F, state as g, customElement as O } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin as M } from "@umbraco-cms/backoffice/element-api";
import { UMB_AUTH_CONTEXT as H } from "@umbraco-cms/backoffice/auth";
import { A as L, a as q } from "./authenticated-fetch-Dotc30dn.js";
var N = Object.defineProperty, j = Object.getOwnPropertyDescriptor, D = (t) => {
  throw TypeError(t);
}, h = (t, e, a, s) => {
  for (var n = s > 1 ? void 0 : s ? j(e, a) : e, o = t.length - 1, d; o >= 0; o--)
    (d = t[o]) && (n = (s ? d(e, a, n) : d(n)) || n);
  return s && n && N(e, a, n), n;
}, K = (t, e, a) => e.has(t) || D("Cannot " + a), B = (t, e, a) => e.has(t) ? D("Cannot add the same private member more than once") : e instanceof WeakSet ? e.add(t) : e.set(t, a), r = (t, e, a) => (K(t, e, "access private method"), a), i, x, p, _, C, f, m, A, T, y, b, k, S, U, E;
const G = "/umbraco/management/api/v1/aivisibility/analytics/requests", Q = "/umbraco/management/api/v1/aivisibility/analytics/classifications", V = "/umbraco/management/api/v1/aivisibility/analytics/summary", Y = "/umbraco/management/api/v1/aivisibility/analytics/retention", W = 50, Z = 7, X = 200;
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
function J() {
  const t = /* @__PURE__ */ new Date(), e = Date.UTC(t.getUTCFullYear(), t.getUTCMonth(), t.getUTCDate());
  return new Date(e).toISOString();
}
function w(t) {
  return (/* @__PURE__ */ new Date(`${t}T00:00:00.000Z`)).toISOString();
}
let c = class extends M(P) {
  constructor() {
    super(...arguments), B(this, i), this._state = { kind: "loading" }, this._filter = r(this, i, x).call(this), this._retentionDays = null, this._lastClassifications = [], this._dateRangeError = null, this._inflight = null;
  }
  connectedCallback() {
    super.connectedCallback(), r(this, i, C).call(this), r(this, i, f).call(this);
  }
  disconnectedCallback() {
    this._inflight?.abort(), this._inflight = null, super.disconnectedCallback();
  }
  render() {
    return l`
      <uui-box headline="AI Traffic">
        ${this._state.kind === "loading" ? l`<uui-loader-bar></uui-loader-bar>` : u}
        ${r(this, i, S).call(this)}
        ${r(this, i, U).call(this)}
        ${this._state.kind === "error" ? l`
              <div role="alert" class="error-banner">
                <span>${this._state.message}</span>
                <uui-button
                  look="primary"
                  @click=${() => {
      r(this, i, f).call(this);
    }}
                >
                  Retry
                </uui-button>
              </div>
            ` : u}
        ${this._state.kind === "auth-error" ? l`
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
        ${r(this, i, E).call(this)}
      </uui-box>
    `;
  }
};
i = /* @__PURE__ */ new WeakSet();
x = function() {
  const t = /* @__PURE__ */ new Date(), e = Date.UTC(t.getUTCFullYear(), t.getUTCMonth(), t.getUTCDate()), a = new Date(e + 864e5);
  return {
    fromDate: new Date(e - Z * 864e5).toISOString().slice(0, 10),
    toDate: a.toISOString().slice(0, 10),
    selectedClasses: /* @__PURE__ */ new Set(),
    page: 1,
    pageSize: W
  };
};
p = async function(t, e) {
  return q(() => this.getContext(H), t, { signal: e });
};
_ = function(t) {
  const e = w(this._filter.fromDate), a = w(this._filter.toDate), s = new URLSearchParams();
  if (s.append("from", e), s.append("to", a), t) {
    for (const n of this._filter.selectedClasses) s.append("class", n);
    s.append("page", String(this._filter.page)), s.append("pageSize", String(this._filter.pageSize));
  }
  return `?${s.toString()}`;
};
C = async function() {
  try {
    const t = new AbortController(), e = await r(this, i, p).call(this, Y, t.signal);
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
  const e = r(this, i, _).call(this, !0), a = r(this, i, _).call(this, !1);
  try {
    const [s, n, o] = await Promise.all([
      r(this, i, p).call(this, `${G}${e}`, t.signal),
      r(this, i, p).call(this, `${Q}${a}`, t.signal),
      r(this, i, p).call(this, `${V}${a}`, t.signal)
    ]);
    if (t.signal.aborted) return;
    if (!s.ok) {
      this._state = {
        kind: "error",
        message: await r(this, i, m).call(this, s)
      };
      return;
    }
    if (!n.ok) {
      this._state = {
        kind: "error",
        message: await r(this, i, m).call(this, n)
      };
      return;
    }
    if (!o.ok) {
      this._state = {
        kind: "error",
        message: await r(this, i, m).call(this, o)
      };
      return;
    }
    const d = await s.json(), v = await n.json(), z = await o.json();
    this._lastClassifications = v, this._state = { kind: "ready", requests: d, classifications: v, summary: z };
    const $ = d.rangeFrom.slice(0, 10);
    $ !== this._filter.fromDate && (this._filter = { ...this._filter, fromDate: $ });
  } catch (s) {
    if (s?.name === "AbortError") return;
    if (s instanceof L) {
      this._state = { kind: "auth-error" };
      return;
    }
    const n = s?.message ?? "Failed to load AI Traffic data";
    this._state = { kind: "error", message: n };
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
A = function(t) {
  const e = t.target.value;
  this._filter = { ...this._filter, fromDate: e, page: 1 }, r(this, i, y).call(this);
};
T = function(t) {
  const e = t.target.value;
  this._filter = { ...this._filter, toDate: e, page: 1 }, r(this, i, y).call(this);
};
y = function() {
  if (this._filter.fromDate && this._filter.toDate && this._filter.fromDate > this._filter.toDate) {
    this._dateRangeError = "From date cannot be after To date";
    return;
  }
  this._dateRangeError = null, r(this, i, f).call(this);
};
b = function(t) {
  const e = new Set(this._filter.selectedClasses);
  e.has(t) ? e.delete(t) : e.add(t), this._filter = { ...this._filter, selectedClasses: e, page: 1 }, r(this, i, f).call(this);
};
k = function(t) {
  const a = t.target.current ?? 1;
  a !== this._filter.page && (this._filter = { ...this._filter, page: a }, r(this, i, f).call(this));
};
S = function() {
  const t = this._state.kind === "ready" ? this._state.summary : null, e = t ? t.totalRequests.toLocaleString() : "…", a = t?.firstSeenUtc ? new Date(t.firstSeenUtc).toLocaleString() : "—", s = t?.lastSeenUtc ? new Date(t.lastSeenUtc).toLocaleString() : "—", n = J().slice(0, 10);
  return l`
      <div class="header-row">
        <label
          >From
          <input
            type="date"
            .value=${this._filter.fromDate}
            min="2020-01-01"
            max=${n}
            @change=${r(this, i, A)}
          />
        </label>
        <label
          >To
          <input
            type="date"
            .value=${this._filter.toDate}
            min="2020-01-01"
            max=${n}
            @change=${r(this, i, T)}
          />
        </label>
        <span class="summary-line">
          Showing ${e} requests from ${a} to ${s}
        </span>
      </div>
      ${this._dateRangeError !== null ? l`<uui-form-validation-message>${this._dateRangeError}</uui-form-validation-message>` : u}
    `;
};
U = function() {
  const t = this._state.kind === "ready" ? this._state.classifications : this._lastClassifications;
  return t.length === 0 ? this._state.kind === "ready" ? l`<div class="chips muted">No classifications recorded for this range.</div>` : u : l`
      <div class="chips" role="group" aria-label="Filter by user-agent classification">
        ${t.map((e) => {
    const a = this._filter.selectedClasses.has(e.class), s = a ? "primary" : "outline", n = R(e.class);
    return l`
            <uui-tag
              class="chip"
              tabindex="0"
              role="button"
              aria-pressed=${a ? "true" : "false"}
              look=${s}
              color=${n}
              @click=${() => r(this, i, b).call(this, e.class)}
              @keydown=${(o) => {
      (o.key === "Enter" || o.key === " ") && (o.preventDefault(), r(this, i, b).call(this, e.class));
    }}
            >
              ${e.class} (${e.count.toLocaleString()})
            </uui-tag>
          `;
  })}
      </div>
    `;
};
E = function() {
  if (this._state.kind !== "ready") return u;
  const t = this._state.requests;
  if (t.total === 0) {
    const e = new Date(t.rangeFrom), a = this._retentionDays !== null && this._retentionDays > 0 && e < new Date(Date.now() - this._retentionDays * 864e5);
    return l`
        <div role="status" class="empty-state">
          <p>
            No AI traffic recorded yet for this filter. The package is logging — check back
            later.
          </p>
          ${a ? l`<p class="retention-hint">
                Retention is configured to ${this._retentionDays} days; older data is not
                available.
              </p>` : u}
        </div>
      `;
  }
  return l`
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
    const a = e.path.length > 80 ? `${e.path.slice(0, 77)}…` : e.path, s = e.contentKey ? `${e.contentKey.slice(0, 8)}…` : "—", n = e.referrerHost ?? "—";
    return l`
            <uui-table-row title=${e.createdUtc}>
              <uui-table-cell>${new Date(e.createdUtc).toLocaleString()}</uui-table-cell>
              <uui-table-cell title=${e.path}>${a}</uui-table-cell>
              <uui-table-cell title=${e.contentKey ?? ""}>${s}</uui-table-cell>
              <uui-table-cell>${e.culture || "—"}</uui-table-cell>
              <uui-table-cell>
                <uui-tag look="primary" color=${R(e.userAgentClass)}>
                  ${e.userAgentClass}
                </uui-tag>
              </uui-table-cell>
              <uui-table-cell>${n}</uui-table-cell>
            </uui-table-row>
          `;
  })}
      </uui-table>
      ${t.totalCappedAt ? l`
            <div class="cap-footer">
              Showing first ${t.totalCappedAt.toLocaleString()} results — narrow your
              date range or add a classification filter to see more.
            </div>
          ` : u}
      ${t.totalPages > 1 ? l`
            <uui-pagination
              total=${Math.min(t.totalPages, X)}
              current=${t.page}
              @change=${r(this, i, k)}
            ></uui-pagination>
          ` : u}
    `;
};
c.styles = [
  I`
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
  F({ attribute: !1 })
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
  O("aiv-ai-traffic-dashboard")
], c);
const st = c, rt = c;
export {
  c as AiVisibilityAiTrafficDashboardElement,
  rt as default,
  st as element
};
