import { LitElement as g, html as i, css as f, property as _, state as v, customElement as k } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin as b } from "@umbraco-cms/backoffice/element-api";
import { UMB_AUTH_CONTEXT as y } from "@umbraco-cms/backoffice/auth";
var x = Object.defineProperty, S = Object.getOwnPropertyDescriptor, h = (e) => {
  throw TypeError(e);
}, d = (e, t, n, a) => {
  for (var r = a > 1 ? void 0 : a ? S(t, n) : t, c = e.length - 1, p; c >= 0; c--)
    (p = e[c]) && (r = (a ? p(t, n, r) : p(r)) || r);
  return a && r && x(t, n, r), r;
}, C = (e, t, n) => t.has(e) || h("Cannot " + n), T = (e, t, n) => t.has(e) ? h("Cannot add the same private member more than once") : t instanceof WeakSet ? t.add(e) : t.set(e, n), l = (e, t, n) => (C(e, t, "access private method"), n), o, m, u;
let s = class extends b(g) {
  constructor() {
    super(...arguments), T(this, o), this._ping = { kind: "pending" };
  }
  connectedCallback() {
    super.connectedCallback(), l(this, o, m).call(this);
  }
  render() {
    return i`
      <uui-box headline="LlmsTxt — Spike 0.B (package mechanics)">
        <p>
          Backoffice manifest discovered, RCL static asset served, Lit element
          rendered via the Bellissima external import map. ✅
        </p>
        <p>
          <strong>Stub Management API ping:</strong>
          ${l(this, o, u).call(this)}
        </p>
        <p class="muted">
          This dashboard is a placeholder for Story 0.B only. The real Settings
          dashboard ships in Story 3.2; the AI traffic dashboard ships in Story
          5.2.
        </p>
      </uui-box>
    `;
  }
};
o = /* @__PURE__ */ new WeakSet();
m = async function() {
  try {
    const e = await this.getContext(y);
    if (!e) {
      this._ping = { kind: "error", message: "auth context unavailable" };
      return;
    }
    const t = e.getOpenApiConfiguration(), n = await t.token(), a = await fetch(
      `${t.base}/umbraco/management/api/v1/llmstxt/spike/ping`,
      {
        credentials: t.credentials,
        headers: {
          Accept: "application/json",
          Authorization: `Bearer ${n}`
        }
      }
    );
    if (!a.ok) {
      this._ping = {
        kind: "error",
        message: `HTTP ${a.status} ${a.statusText}`
      };
      return;
    }
    const r = await a.json();
    this._ping = { kind: "ok", time: r.time, instanceId: r.instanceId };
  } catch (e) {
    this._ping = {
      kind: "error",
      message: e instanceof Error ? e.message : String(e)
    };
  }
};
u = function() {
  return this._ping.kind === "pending" ? i`<em>pending…</em>` : this._ping.kind === "error" ? i`<span class="error">error — ${this._ping.message}</span>` : i`
      <code>${this._ping.time}</code> from
      <code>${this._ping.instanceId}</code>
    `;
};
s.styles = [
  f`
      :host {
        display: block;
        padding: var(--uui-size-layout-1, 24px);
      }
      .muted {
        color: var(--uui-color-text-alt, #888);
        font-size: 0.9em;
      }
      .error {
        color: var(--uui-color-danger, #d42054);
      }
      code {
        font-family: var(
          --uui-font-monospace,
          ui-monospace,
          SFMono-Regular,
          Menlo,
          monospace
        );
      }
    `
];
d([
  _({ attribute: !1 })
], s.prototype, "manifest", 2);
d([
  v()
], s.prototype, "_ping", 2);
s = d([
  k("llms-spike-dashboard")
], s);
const $ = s;
export {
  s as LlmsSpikeDashboardElement,
  $ as element
};
//# sourceMappingURL=llms-spike-dashboard.element-DkguFUaq.js.map
