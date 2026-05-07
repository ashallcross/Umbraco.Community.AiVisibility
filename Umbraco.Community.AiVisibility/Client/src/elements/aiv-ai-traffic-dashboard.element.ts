import {
  LitElement,
  html,
  css,
  nothing,
  customElement,
  state,
  property,
} from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import type {
  UmbDashboardElement,
  ManifestDashboard,
} from "@umbraco-cms/backoffice/dashboard";
import {
  authenticatedFetch,
  AuthContextUnavailableError,
} from "../util/authenticated-fetch.js";

// ────────────────────────────────────────────────────────────────────────
// View-model contracts shared with AnalyticsManagementApiController.cs
// ────────────────────────────────────────────────────────────────────────

interface AnalyticsRequestViewModel {
  id: number;
  createdUtc: string;          // ISO-8601 UTC
  path: string;
  contentKey: string | null;   // GUID string
  culture: string;
  userAgentClass: string;      // UserAgentClass enum NAME
  referrerHost: string | null;
}

interface AnalyticsRequestPageViewModel {
  items: AnalyticsRequestViewModel[];
  total: number;
  page: number;
  pageSize: number;
  totalPages: number;
  rangeFrom: string;
  rangeTo: string;
  totalCappedAt: number | null;
}

interface AnalyticsClassificationViewModel {
  class: string;
  count: number;
}

interface AnalyticsSummaryViewModel {
  totalRequests: number;
  firstSeenUtc: string | null;
  lastSeenUtc: string | null;
  rangeFrom: string;
  rangeTo: string;
}

interface AnalyticsRetentionViewModel {
  durationDays: number;
}

interface ProblemDetails { title?: string; detail?: string; status?: number; }

// ────────────────────────────────────────────────────────────────────────
// Discriminated UI states (mirrors Story 3.2 shape)
// ────────────────────────────────────────────────────────────────────────

type DashboardState =
  | { kind: "loading" }
  | {
      kind: "ready";
      requests: AnalyticsRequestPageViewModel;
      classifications: AnalyticsClassificationViewModel[];
      summary: AnalyticsSummaryViewModel;
    }
  | { kind: "error"; message: string }
  | { kind: "auth-error" };

interface FilterState {
  fromDate: string;       // YYYY-MM-DD (input value)
  toDate: string;         // YYYY-MM-DD (input value, exclusive upper bound)
  selectedClasses: Set<string>;
  page: number;
  pageSize: number;
}

const REQUESTS_PATH = "/umbraco/management/api/v1/aivisibility/analytics/requests";
const CLASSIFICATIONS_PATH = "/umbraco/management/api/v1/aivisibility/analytics/classifications";
const SUMMARY_PATH = "/umbraco/management/api/v1/aivisibility/analytics/summary";
const RETENTION_PATH = "/umbraco/management/api/v1/aivisibility/analytics/retention";

const DEFAULT_PAGE_SIZE = 50;
const DEFAULT_RANGE_DAYS = 7;
const PAGINATION_PAGE_BUTTON_CAP = 200;

// AC7 — colour-coded badge per UA classification.
function classColor(className: string): "default" | "primary" | "positive" | "warning" | "danger" {
  switch (className) {
    case "AiTraining":
    case "AiSearchRetrieval":
    case "AiUserTriggered":
      return "primary";
    case "AiDeprecated":
      return "warning";
    case "HumanBrowser":
      return "positive";
    case "CrawlerOther":
    case "Unknown":
    default:
      return "default";
  }
}

function todayUtcMidnightIso(): string {
  const now = new Date();
  const utcMidnight = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  return new Date(utcMidnight).toISOString();
}

function dateInputValueToUtcMidnightIso(yyyymmdd: string): string {
  // Input is already YYYY-MM-DD per HTML5 <input type="date">; treat as UTC midnight.
  return new Date(`${yyyymmdd}T00:00:00.000Z`).toISOString();
}

@customElement("aiv-ai-traffic-dashboard")
export class AiVisibilityAiTrafficDashboardElement
  extends UmbElementMixin(LitElement)
  implements UmbDashboardElement
{
  @property({ attribute: false }) public manifest?: ManifestDashboard;

  @state() private _state: DashboardState = { kind: "loading" };
  @state() private _filter: FilterState = this.#defaultFilter();
  @state() private _retentionDays: number | null = null;
  @state() private _lastClassifications: AnalyticsClassificationViewModel[] = [];
  @state() private _dateRangeError: string | null = null;

  private _inflight: AbortController | null = null;

  override connectedCallback(): void {
    super.connectedCallback();
    void this.#fetchRetention();
    void this.#refresh();
  }

  override disconnectedCallback(): void {
    this._inflight?.abort();
    this._inflight = null;
    super.disconnectedCallback();
  }

  #defaultFilter(): FilterState {
    const now = new Date();
    const todayUtcMidnight = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
    const tomorrow = new Date(todayUtcMidnight + 86_400_000);
    const sevenDaysAgo = new Date(todayUtcMidnight - DEFAULT_RANGE_DAYS * 86_400_000);
    return {
      fromDate: sevenDaysAgo.toISOString().slice(0, 10),
      toDate: tomorrow.toISOString().slice(0, 10),
      selectedClasses: new Set<string>(),
      page: 1,
      pageSize: DEFAULT_PAGE_SIZE,
    };
  }

  async #authedFetch(path: string, signal: AbortSignal): Promise<Response> {
    // Story 6.0b AC6 — auth-fetch primitive lifted to a shared helper at
    // ../util/authenticated-fetch.ts. The strict-token contract (throws
    // AuthContextUnavailableError on missing context / failed token /
    // empty token) is preserved verbatim; the calling shape `({ signal })`
    // matches the helper's GET-default options.
    return authenticatedFetch(() => this.getContext(UMB_AUTH_CONTEXT), path, { signal });
  }

  #buildQuerystring(includeFilters: boolean): string {
    const fromIso = dateInputValueToUtcMidnightIso(this._filter.fromDate);
    const toIso = dateInputValueToUtcMidnightIso(this._filter.toDate);
    const params = new URLSearchParams();
    params.append("from", fromIso);
    params.append("to", toIso);
    if (includeFilters) {
      for (const c of this._filter.selectedClasses) params.append("class", c);
      params.append("page", String(this._filter.page));
      params.append("pageSize", String(this._filter.pageSize));
    }
    return `?${params.toString()}`;
  }

  async #fetchRetention(): Promise<void> {
    try {
      const ac = new AbortController();
      const res = await this.#authedFetch(RETENTION_PATH, ac.signal);
      if (!res.ok) return;
      const body = (await res.json()) as AnalyticsRetentionViewModel;
      this._retentionDays = body.durationDays;
    } catch {
      // Non-blocking — retention hint just won't surface.
    }
  }

  async #refresh(): Promise<void> {
    if (this._dateRangeError !== null) return;

    this._inflight?.abort();
    const ac = new AbortController();
    this._inflight = ac;
    this._state = { kind: "loading" };

    const requestsQs = this.#buildQuerystring(true);
    const rangeQs = this.#buildQuerystring(false);

    try {
      const [requestsRes, classRes, summaryRes] = await Promise.all([
        this.#authedFetch(`${REQUESTS_PATH}${requestsQs}`, ac.signal),
        this.#authedFetch(`${CLASSIFICATIONS_PATH}${rangeQs}`, ac.signal),
        this.#authedFetch(`${SUMMARY_PATH}${rangeQs}`, ac.signal),
      ]);

      if (ac.signal.aborted) return;

      if (!requestsRes.ok) {
        this._state = {
          kind: "error",
          message: await this.#extractProblemMessage(requestsRes),
        };
        return;
      }
      if (!classRes.ok) {
        this._state = {
          kind: "error",
          message: await this.#extractProblemMessage(classRes),
        };
        return;
      }
      if (!summaryRes.ok) {
        this._state = {
          kind: "error",
          message: await this.#extractProblemMessage(summaryRes),
        };
        return;
      }

      const requests = (await requestsRes.json()) as AnalyticsRequestPageViewModel;
      const classifications = (await classRes.json()) as AnalyticsClassificationViewModel[];
      const summary = (await summaryRes.json()) as AnalyticsSummaryViewModel;

      this._lastClassifications = classifications;
      this._state = { kind: "ready", requests, classifications, summary };

      // Echo server's effective range back into the From input so the editor
      // sees the post-clamp value (Implementation Notes line 935).
      const effectiveFrom = requests.rangeFrom.slice(0, 10);
      if (effectiveFrom !== this._filter.fromDate) {
        this._filter = { ...this._filter, fromDate: effectiveFrom };
      }
    } catch (err) {
      if ((err as { name?: string })?.name === "AbortError") return;
      if (err instanceof AuthContextUnavailableError) {
        this._state = { kind: "auth-error" };
        return;
      }
      const message = (err as Error)?.message ?? "Failed to load AI Traffic data";
      this._state = { kind: "error", message };
    }
  }

  async #extractProblemMessage(res: Response): Promise<string> {
    try {
      const problem = (await res.json()) as ProblemDetails;
      return problem.detail ?? problem.title ?? `HTTP ${res.status}`;
    } catch {
      return `HTTP ${res.status}`;
    }
  }

  // ── Event handlers ──

  #onFromDateChange(e: Event) {
    const value = (e.target as HTMLInputElement).value;
    this._filter = { ...this._filter, fromDate: value, page: 1 };
    this.#validateDateRangeAndRefresh();
  }

  #onToDateChange(e: Event) {
    const value = (e.target as HTMLInputElement).value;
    this._filter = { ...this._filter, toDate: value, page: 1 };
    this.#validateDateRangeAndRefresh();
  }

  #validateDateRangeAndRefresh() {
    // YYYY-MM-DD strings sort lexicographically equal to ISO date order.
    if (this._filter.fromDate && this._filter.toDate
        && this._filter.fromDate > this._filter.toDate) {
      this._dateRangeError = "From date cannot be after To date";
      return;
    }
    this._dateRangeError = null;
    void this.#refresh();
  }

  #toggleClass(className: string) {
    const next = new Set(this._filter.selectedClasses);
    if (next.has(className)) {
      next.delete(className);
    } else {
      next.add(className);
    }
    this._filter = { ...this._filter, selectedClasses: next, page: 1 };
    void this.#refresh();
  }

  #onPageChange(e: Event) {
    const target = e.target as HTMLElement & { current?: number };
    const next = target.current ?? 1;
    if (next === this._filter.page) return;
    this._filter = { ...this._filter, page: next };
    void this.#refresh();
  }

  // ── Render ──

  #renderHeader() {
    const summary = this._state.kind === "ready" ? this._state.summary : null;
    const total = summary ? summary.totalRequests.toLocaleString() : "…";
    const first = summary?.firstSeenUtc ? new Date(summary.firstSeenUtc).toLocaleString() : "—";
    const last = summary?.lastSeenUtc ? new Date(summary.lastSeenUtc).toLocaleString() : "—";

    const todayIso = todayUtcMidnightIso().slice(0, 10);
    return html`
      <div class="header-row">
        <label
          >From
          <input
            type="date"
            .value=${this._filter.fromDate}
            min="2020-01-01"
            max=${todayIso}
            @change=${this.#onFromDateChange}
          />
        </label>
        <label
          >To
          <input
            type="date"
            .value=${this._filter.toDate}
            min="2020-01-01"
            max=${todayIso}
            @change=${this.#onToDateChange}
          />
        </label>
        <span class="summary-line">
          Showing ${total} requests from ${first} to ${last}
        </span>
      </div>
      ${this._dateRangeError !== null
        ? html`<uui-form-validation-message>${this._dateRangeError}</uui-form-validation-message>`
        : nothing}
    `;
  }

  #renderChips() {
    // Source from the ready state when available, else from the cached last
    // successful classifications so chips remain interactive during loading
    // and error states (AC8: "header / chips remain interactive").
    const chips = this._state.kind === "ready"
      ? this._state.classifications
      : this._lastClassifications;
    if (chips.length === 0) {
      if (this._state.kind === "ready") {
        return html`<div class="chips muted">No classifications recorded for this range.</div>`;
      }
      return nothing;
    }
    return html`
      <div class="chips" role="group" aria-label="Filter by user-agent classification">
        ${chips.map((c) => {
          const selected = this._filter.selectedClasses.has(c.class);
          const look = selected ? "primary" : "outline";
          const color = classColor(c.class);
          return html`
            <uui-tag
              class="chip"
              tabindex="0"
              role="button"
              aria-pressed=${selected ? "true" : "false"}
              look=${look}
              color=${color}
              @click=${() => this.#toggleClass(c.class)}
              @keydown=${(e: KeyboardEvent) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.preventDefault();
                  this.#toggleClass(c.class);
                }
              }}
            >
              ${c.class} (${c.count.toLocaleString()})
            </uui-tag>
          `;
        })}
      </div>
    `;
  }

  #renderTable() {
    if (this._state.kind !== "ready") return nothing;
    const requests = this._state.requests;

    if (requests.total === 0) {
      const rangeFrom = new Date(requests.rangeFrom);
      const showRetentionHint =
        this._retentionDays !== null
        && this._retentionDays > 0
        && rangeFrom < new Date(Date.now() - this._retentionDays * 86_400_000);
      return html`
        <div role="status" class="empty-state">
          <p>
            No AI traffic recorded yet for this filter. The package is logging — check back
            later.
          </p>
          ${showRetentionHint
            ? html`<p class="retention-hint">
                Retention is configured to ${this._retentionDays} days; older data is not
                available.
              </p>`
            : nothing}
        </div>
      `;
    }

    return html`
      <uui-table>
        <uui-table-head>
          <uui-table-head-cell>Timestamp</uui-table-head-cell>
          <uui-table-head-cell>Path</uui-table-head-cell>
          <uui-table-head-cell>Content Key</uui-table-head-cell>
          <uui-table-head-cell>Culture</uui-table-head-cell>
          <uui-table-head-cell>UA Class</uui-table-head-cell>
          <uui-table-head-cell>Referrer</uui-table-head-cell>
        </uui-table-head>
        ${requests.items.map((r) => {
          const path = r.path.length > 80 ? `${r.path.slice(0, 77)}…` : r.path;
          const contentKey = r.contentKey ? `${r.contentKey.slice(0, 8)}…` : "—";
          const referrer = r.referrerHost ?? "—";
          return html`
            <uui-table-row title=${r.createdUtc}>
              <uui-table-cell>${new Date(r.createdUtc).toLocaleString()}</uui-table-cell>
              <uui-table-cell title=${r.path}>${path}</uui-table-cell>
              <uui-table-cell title=${r.contentKey ?? ""}>${contentKey}</uui-table-cell>
              <uui-table-cell>${r.culture || "—"}</uui-table-cell>
              <uui-table-cell>
                <uui-tag look="primary" color=${classColor(r.userAgentClass)}>
                  ${r.userAgentClass}
                </uui-tag>
              </uui-table-cell>
              <uui-table-cell>${referrer}</uui-table-cell>
            </uui-table-row>
          `;
        })}
      </uui-table>
      ${requests.totalCappedAt
        ? html`
            <div class="cap-footer">
              Showing first ${requests.totalCappedAt.toLocaleString()} results — narrow your
              date range or add a classification filter to see more.
            </div>
          `
        : nothing}
      ${requests.totalPages > 1
        ? html`
            <uui-pagination
              total=${Math.min(requests.totalPages, PAGINATION_PAGE_BUTTON_CAP)}
              current=${requests.page}
              @change=${this.#onPageChange}
            ></uui-pagination>
          `
        : nothing}
    `;
  }

  override render() {
    return html`
      <uui-box headline="AI Traffic">
        ${this._state.kind === "loading" ? html`<uui-loader-bar></uui-loader-bar>` : nothing}
        ${this.#renderHeader()}
        ${this.#renderChips()}
        ${this._state.kind === "error"
          ? html`
              <div role="alert" class="error-banner">
                <span>${this._state.message}</span>
                <uui-button
                  look="primary"
                  @click=${() => void this.#refresh()}
                >
                  Retry
                </uui-button>
              </div>
            `
          : nothing}
        ${this._state.kind === "auth-error"
          ? html`
              <div role="alert" class="error-banner">
                <span
                  >Could not authenticate with the Backoffice — please refresh and try
                  again</span
                >
                <uui-button look="primary" @click=${() => window.location.reload()}>
                  Refresh
                </uui-button>
              </div>
            `
          : nothing}
        ${this.#renderTable()}
      </uui-box>
    `;
  }

  static override styles = [
    css`
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
    `,
  ];
}

// Spike 0.B locked decision #7 — dual default + named exports so Bellissima's
// ElementLoaderExports<UmbDashboardElement> resolves the dynamic import.
export const element = AiVisibilityAiTrafficDashboardElement;
export default AiVisibilityAiTrafficDashboardElement;

declare global {
  interface HTMLElementTagNameMap {
    "aiv-ai-traffic-dashboard": AiVisibilityAiTrafficDashboardElement;
  }
}
