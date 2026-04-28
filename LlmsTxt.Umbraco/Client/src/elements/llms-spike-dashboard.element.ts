import {
  LitElement,
  html,
  css,
  customElement,
  state,
  property,
} from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import type { UmbDashboardElement } from "@umbraco-cms/backoffice/dashboard";
import type { ManifestDashboard } from "@umbraco-cms/backoffice/dashboard";

type PingState =
  | { kind: "pending" }
  | { kind: "ok"; time: string; instanceId: string }
  | { kind: "error"; message: string };

@customElement("llms-spike-dashboard")
export class LlmsSpikeDashboardElement
  extends UmbElementMixin(LitElement)
  implements UmbDashboardElement
{
  @property({ attribute: false })
  public manifest?: ManifestDashboard;

  @state()
  private _ping: PingState = { kind: "pending" };

  override connectedCallback(): void {
    super.connectedCallback();
    void this.#fetchPing();
  }

  // Management API endpoints expect a bearer token via UMB_AUTH_CONTEXT — the
  // raw cookie auth that works for the older /umbraco/{api}/... routes returns
  // 401 here. Pattern from `UmbAuthContext.getOpenApiConfiguration()` JSDoc.
  async #fetchPing(): Promise<void> {
    try {
      const authContext = await this.getContext(UMB_AUTH_CONTEXT);
      if (!authContext) {
        this._ping = { kind: "error", message: "auth context unavailable" };
        return;
      }
      const config = authContext.getOpenApiConfiguration();
      const token = await config.token();
      const response = await fetch(
        `${config.base}/umbraco/management/api/v1/llmstxt/spike/ping`,
        {
          credentials: config.credentials,
          headers: {
            Accept: "application/json",
            Authorization: `Bearer ${token}`,
          },
        },
      );
      if (!response.ok) {
        this._ping = {
          kind: "error",
          message: `HTTP ${response.status} ${response.statusText}`,
        };
        return;
      }
      const body = (await response.json()) as { time: string; instanceId: string };
      this._ping = { kind: "ok", time: body.time, instanceId: body.instanceId };
    } catch (err) {
      this._ping = {
        kind: "error",
        message: err instanceof Error ? err.message : String(err),
      };
    }
  }

  override render() {
    return html`
      <uui-box headline="LlmsTxt — Spike 0.B (package mechanics)">
        <p>
          Backoffice manifest discovered, RCL static asset served, Lit element
          rendered via the Bellissima external import map. ✅
        </p>
        <p>
          <strong>Stub Management API ping:</strong>
          ${this.#renderPing()}
        </p>
        <p class="muted">
          This dashboard is a placeholder for Story 0.B only. The real Settings
          dashboard ships in Story 3.2; the AI traffic dashboard ships in Story
          5.2.
        </p>
      </uui-box>
    `;
  }

  #renderPing() {
    if (this._ping.kind === "pending") {
      return html`<em>pending…</em>`;
    }
    if (this._ping.kind === "error") {
      return html`<span class="error">error — ${this._ping.message}</span>`;
    }
    return html`
      <code>${this._ping.time}</code> from
      <code>${this._ping.instanceId}</code>
    `;
  }

  static override styles = [
    css`
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
    `,
  ];
}

// Bellissima's `ElementLoaderExports` accepts either a `default` or `element` export
// from a dynamic import. We export `element` so the manifest's
// `element: () => import("./llms-spike-dashboard.element.js")` resolves cleanly.
export const element = LlmsSpikeDashboardElement;

declare global {
  interface HTMLElementTagNameMap {
    "llms-spike-dashboard": LlmsSpikeDashboardElement;
  }
}
