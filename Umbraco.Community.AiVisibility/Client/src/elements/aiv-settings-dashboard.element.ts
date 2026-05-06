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
import { UMB_CURRENT_USER_CONTEXT } from "@umbraco-cms/backoffice/current-user";
import { filter, firstValueFrom } from "@umbraco-cms/backoffice/external/rxjs";
import type {
  UmbDashboardElement,
  ManifestDashboard,
} from "@umbraco-cms/backoffice/dashboard";

// ────────────────────────────────────────────────────────────────────────
// View-model contracts shared with LlmsSettingsManagementApiController.cs
// ────────────────────────────────────────────────────────────────────────

interface LlmsSettingsViewModel {
  siteName: string | null;
  siteSummary: string | null;
  excludedDoctypeAliases: string[];
  summaryMaxChars: number;
  settingsNodeKey: string | null;
}

interface LlmsDoctypeViewModel {
  alias: string;
  name: string;
  iconCss: string | null;
}

interface LlmsExcludedPageViewModel {
  key: string;
  name: string;
  path: string;
  culture: string | null;
  contentTypeAlias: string;
  contentTypeName: string;
}

interface LlmsExcludedPagesPageViewModel {
  items: LlmsExcludedPageViewModel[];
  total: number;
  skip: number;
  take: number;
}

interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
}

// ────────────────────────────────────────────────────────────────────────
// Discriminated UI states (mirrors Spike 0.B PingState shape)
// ────────────────────────────────────────────────────────────────────────

type LoadState =
  | { kind: "loading" }
  | { kind: "ready"; settings: LlmsSettingsViewModel; doctypes: LlmsDoctypeViewModel[] }
  | { kind: "error"; message: string };

type SaveState =
  | { kind: "idle" }
  | { kind: "saving" }
  | { kind: "success" }
  | { kind: "error"; message: string };

type ExcludedPagesState =
  | { kind: "loading" }
  | { kind: "ready"; page: LlmsExcludedPagesPageViewModel }
  | { kind: "error"; message: string };

interface FormState {
  siteName: string;
  siteSummary: string;
  excludedDoctypeAliases: string[];
}

const SETTINGS_PATH = "/umbraco/management/api/v1/llmstxt/settings/";
const DOCTYPES_PATH = "/umbraco/management/api/v1/llmstxt/settings/doctypes";
const EXCLUDED_PAGES_PATH = "/umbraco/management/api/v1/llmstxt/settings/excluded-pages";
const SAVE_TOAST_TIMEOUT_MS = 3000;

// Story 3.3 — onboarding hint per-user persistence. Keyed by Backoffice user
// `unique` GUID so multi-user-per-browser deployments isolate dismiss state.
// `v1.` segment lets a future scheme (e.g. Story 5.2's auto-hide tying into
// AI traffic logs) ship a parallel key without ambiguity over which version
// dismissed a given user.
// Body copy verbatim from epics.md:1067.
const ONBOARDING_DISMISS_STORAGE_PREFIX = "llms.onboarding.dismissed.v1.";
const ONBOARDING_RESOLVE_TIMEOUT_MS = 2000;
const ONBOARDING_BODY =
  "LlmsTxt is now active and producing default output. Customise your site name and summary below, or accept the defaults — /llms.txt and /llms-full.txt are already available at your site's root.";

@customElement("aiv-settings-dashboard")
export class AiVisibilitySettingsDashboardElement
  extends UmbElementMixin(LitElement)
  implements UmbDashboardElement
{
  @property({ attribute: false })
  public manifest?: ManifestDashboard;

  @state() private _state: LoadState = { kind: "loading" };
  @state() private _saveState: SaveState = { kind: "idle" };
  @state() private _excludedPages: ExcludedPagesState = { kind: "loading" };
  @state() private _formState: FormState = {
    siteName: "",
    siteSummary: "",
    excludedDoctypeAliases: [],
  };
  // Story 3.3 — onboarding notice dismiss state. Default `true` (hide) until
  // we resolve the current user + read their per-user flag; this prevents the
  // notice from flashing on a previously-dismissed user's reload while the
  // current-user context resolves asynchronously. `_currentUserUnique` stays
  // null until the context lands.
  @state() private _onboardingDismissed: boolean = true;
  @state() private _currentUserUnique: string | null = null;

  // Captured snapshot of the form on first ready — drives the "no changes →
  // disable Save" client-side gate (AC8).
  private _initialFormState: FormState = {
    siteName: "",
    siteSummary: "",
    excludedDoctypeAliases: [],
  };

  private _saveToastTimer: ReturnType<typeof setTimeout> | null = null;
  private _abortController: AbortController | null = null;

  override connectedCallback(): void {
    super.connectedCallback();
    // Onboarding state is independent of the dirty-form re-attach guard:
    // re-mounts with unsaved form changes still need to re-resolve the
    // current user (the auth context may have landed since the initial
    // mount). Fire it before the early-return so it always runs.
    void this.#resolveOnboardingState();
    // Re-attach guard: if the element re-enters the DOM (Backoffice section
    // switch, dev-mode HMR) while the form is dirty, do NOT overwrite the
    // editor's unsaved changes by re-fetching. The existing form state is
    // preserved; editor can hit Save or Reset (future) when ready.
    if (this._state.kind === "ready" && this._hasChanges()) {
      return;
    }
    void this.#fetchInitial();
  }

  override disconnectedCallback(): void {
    if (this._saveToastTimer) {
      clearTimeout(this._saveToastTimer);
      this._saveToastTimer = null;
    }
    // Abort any in-flight fetch so a late resolve doesn't mutate state on a
    // disconnected element (Lit warns + setTimeout would fire post-disconnect).
    if (this._abortController) {
      this._abortController.abort();
      this._abortController = null;
    }
    super.disconnectedCallback();
  }

  // ────────────────────────────────────────────────────────────────────────
  // Auth helper (Spike 0.B locked decision #11 — bearer-token only)
  // ────────────────────────────────────────────────────────────────────────

  async #fetchJson<T>(
    path: string,
    init: { method?: string; body?: unknown } = {},
  ): Promise<{ ok: true; data: T } | { ok: false; status: number; problem?: ProblemDetails; message: string; aborted?: boolean }> {
    if (!this._abortController) {
      this._abortController = new AbortController();
    }
    const signal = this._abortController.signal;
    try {
      const authContext = await this.getContext(UMB_AUTH_CONTEXT);
      if (!authContext) {
        return { ok: false, status: 0, message: "Backoffice auth context unavailable. Please refresh and try again." };
      }
      const config = authContext.getOpenApiConfiguration();
      const token = await config.token();
      const url = `${config.base}${path}`;
      const response = await fetch(url, {
        method: init.method ?? "GET",
        credentials: config.credentials,
        signal,
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
          Authorization: `Bearer ${token}`,
        },
        body: init.body !== undefined ? JSON.stringify(init.body) : undefined,
      });
      if (!response.ok) {
        let problem: ProblemDetails | undefined;
        try {
          problem = (await response.json()) as ProblemDetails;
        } catch {
          // Body wasn't JSON — fall through to a status-only message.
        }
        return {
          ok: false,
          status: response.status,
          problem,
          message: problem?.detail ?? `HTTP ${response.status} ${response.statusText}`,
        };
      }
      const data = (await response.json()) as T;
      return { ok: true, data };
    } catch (err) {
      if ((err instanceof DOMException && err.name === "AbortError") || signal.aborted) {
        return { ok: false, status: 0, message: "Request aborted.", aborted: true };
      }
      return {
        ok: false,
        status: 0,
        message: err instanceof Error ? err.message : String(err),
      };
    }
  }

  // ────────────────────────────────────────────────────────────────────────
  // Initial load
  // ────────────────────────────────────────────────────────────────────────

  async #fetchInitial(): Promise<void> {
    this._state = { kind: "loading" };
    const [settingsResult, doctypesResult] = await Promise.all([
      this.#fetchJson<LlmsSettingsViewModel>(SETTINGS_PATH),
      this.#fetchJson<LlmsDoctypeViewModel[]>(DOCTYPES_PATH),
    ]);

    if (!this.isConnected) return;
    if (!settingsResult.ok) {
      if (settingsResult.aborted) return;
      this._state = { kind: "error", message: settingsResult.message };
      return;
    }
    if (!doctypesResult.ok) {
      if (doctypesResult.aborted) return;
      this._state = { kind: "error", message: doctypesResult.message };
      return;
    }

    const settings = settingsResult.data;
    const formState: FormState = {
      siteName: settings.siteName ?? "",
      siteSummary: settings.siteSummary ?? "",
      excludedDoctypeAliases: [...settings.excludedDoctypeAliases],
    };
    this._formState = formState;
    this._initialFormState = {
      siteName: formState.siteName,
      siteSummary: formState.siteSummary,
      excludedDoctypeAliases: [...formState.excludedDoctypeAliases],
    };
    this._state = { kind: "ready", settings, doctypes: doctypesResult.data };

    void this.#fetchExcludedPages();
  }

  async #fetchExcludedPages(): Promise<void> {
    this._excludedPages = { kind: "loading" };
    const result = await this.#fetchJson<LlmsExcludedPagesPageViewModel>(
      `${EXCLUDED_PAGES_PATH}?skip=0&take=100`,
    );
    if (!this.isConnected) return;
    if (!result.ok) {
      if (result.aborted) return;
      this._excludedPages = { kind: "error", message: result.message };
      return;
    }
    this._excludedPages = { kind: "ready", page: result.data };
  }

  // ────────────────────────────────────────────────────────────────────────
  // Save (PUT) — AC7 / AC8
  // ────────────────────────────────────────────────────────────────────────

  async #onSave(): Promise<void> {
    if (!this._canSave()) {
      return;
    }
    if (this._saveToastTimer) {
      clearTimeout(this._saveToastTimer);
      this._saveToastTimer = null;
    }
    this._saveState = { kind: "saving" };

    const result = await this.#fetchJson<LlmsSettingsViewModel>(SETTINGS_PATH, {
      method: "PUT",
      body: {
        siteName: this._formState.siteName.length === 0 ? null : this._formState.siteName,
        siteSummary: this._formState.siteSummary.length === 0 ? null : this._formState.siteSummary,
        excludedDoctypeAliases: this._formState.excludedDoctypeAliases,
      },
    });

    if (!this.isConnected) return;
    if (!result.ok) {
      if (result.aborted) return;
      this._saveState = { kind: "error", message: result.message };
      return;
    }

    // Round-trip the freshly-published values into the form.
    const settings = result.data;
    const formState: FormState = {
      siteName: settings.siteName ?? "",
      siteSummary: settings.siteSummary ?? "",
      excludedDoctypeAliases: [...settings.excludedDoctypeAliases],
    };
    this._formState = formState;
    this._initialFormState = {
      siteName: formState.siteName,
      siteSummary: formState.siteSummary,
      excludedDoctypeAliases: [...formState.excludedDoctypeAliases],
    };
    if (this._state.kind === "ready") {
      this._state = { ...this._state, settings };
    }
    this._saveState = { kind: "success" };
    this._saveToastTimer = setTimeout(() => {
      this._saveState = { kind: "idle" };
      this._saveToastTimer = null;
    }, SAVE_TOAST_TIMEOUT_MS);

    // Refresh the read-only excluded-pages list — its content depends on the
    // doctype-alias filter the editor may have just changed.
    void this.#fetchExcludedPages();
  }

  private _canSave(): boolean {
    if (this._state.kind !== "ready") return false;
    if (this._saveState.kind === "saving") return false;
    if (this._formState.siteSummary.length > this._state.settings.summaryMaxChars) return false;
    return this._hasChanges();
  }

  private _hasChanges(): boolean {
    if (this._formState.siteName !== this._initialFormState.siteName) return true;
    if (this._formState.siteSummary !== this._initialFormState.siteSummary) return true;
    if (this._formState.excludedDoctypeAliases.length !== this._initialFormState.excludedDoctypeAliases.length) return true;
    // Match server's OrdinalIgnoreCase ordering (BuildViewModel sorts case-
    // insensitively) so a re-checked alias doesn't read as "changed" purely
    // because JS default sort uses UTF-16 code points.
    const compareCi = (a: string, b: string) => a.toLowerCase().localeCompare(b.toLowerCase());
    const sortedNow = [...this._formState.excludedDoctypeAliases].sort(compareCi);
    const sortedThen = [...this._initialFormState.excludedDoctypeAliases].sort(compareCi);
    for (let i = 0; i < sortedNow.length; i += 1) {
      if (sortedNow[i] !== sortedThen[i]) return true;
    }
    return false;
  }

  // ────────────────────────────────────────────────────────────────────────
  // Field event handlers
  // ────────────────────────────────────────────────────────────────────────

  #onSiteNameInput(e: Event): void {
    const target = e.target as HTMLInputElement | null;
    this._formState = { ...this._formState, siteName: target?.value ?? "" };
  }

  #onSiteSummaryInput(e: Event): void {
    const target = e.target as HTMLTextAreaElement | null;
    this._formState = { ...this._formState, siteSummary: target?.value ?? "" };
  }

  #onAliasToggle(alias: string, checked: boolean): void {
    const current = this._formState.excludedDoctypeAliases;
    const lower = alias.toLowerCase();
    const filtered = current.filter((a) => a.toLowerCase() !== lower);
    this._formState = {
      ...this._formState,
      excludedDoctypeAliases: checked ? [...filtered, alias] : filtered,
    };
  }

  // ────────────────────────────────────────────────────────────────────────
  // Story 3.3 — onboarding notice
  // ────────────────────────────────────────────────────────────────────────

  async #resolveOnboardingState(): Promise<void> {
    try {
      const currentUser = await this.getContext(UMB_CURRENT_USER_CONTEXT);
      if (!this.isConnected) return;
      if (!currentUser) {
        // Context not yet registered (early Backoffice race) — leave the
        // notice hidden until a re-mount tries again. Worst case the editor
        // sees the notice on the next session.
        return;
      }
      // `currentUser.getUnique()` is the synchronous BehaviorSubject getter:
      // it returns `undefined` until the context's `load()` has populated
      // the unique GUID. Awaiting the observable's first defined emission
      // (race against a 2 s timeout so we never hang) is the canonical
      // Bellissima pattern for context-backed observable values.
      const unique = await Promise.race<string | null>([
        firstValueFrom(currentUser.unique.pipe(filter((u): u is string => !!u))),
        new Promise<null>((resolve) =>
          setTimeout(() => resolve(null), ONBOARDING_RESOLVE_TIMEOUT_MS),
        ),
      ]);
      if (!this.isConnected) return;
      this._currentUserUnique = unique;
      if (!unique) {
        // Timeout elapsed without a defined `unique` (auth still loading or
        // user unauthenticated). Leave dismissed=true; a later re-mount
        // re-runs this resolver.
        return;
      }
      const key = ONBOARDING_DISMISS_STORAGE_PREFIX + unique;
      let dismissed = false;
      try {
        dismissed = localStorage.getItem(key) === "1";
      } catch {
        // localStorage access can throw under aggressive privacy modes.
        // Treat as "not dismissed" so the editor can still see the hint.
      }
      this._onboardingDismissed = dismissed;
    } catch {
      // If context resolution itself throws, leave dismissed=true (default
      // initial state) — onboarding is non-essential to the dashboard's
      // mission, never let it surface as an error toast.
    }
  }

  #onDismissOnboarding(): void {
    this._onboardingDismissed = true;
    const unique = this._currentUserUnique;
    if (!unique) return;
    try {
      localStorage.setItem(ONBOARDING_DISMISS_STORAGE_PREFIX + unique, "1");
    } catch {
      // Storage unavailable — dismiss survives the current session only.
      // Acceptable; the notice carries no critical UX (Story 5.2's
      // auto-hide-after-AI-traffic is the long-term replacement).
    }
  }

  #renderOnboardingNotice() {
    if (this._onboardingDismissed) return nothing;
    return html`
      <div class="onboarding-notice" role="status">
        <p class="onboarding-body">${ONBOARDING_BODY}</p>
        <uui-button
          class="onboarding-dismiss"
          look="secondary"
          @click=${() => this.#onDismissOnboarding()}
        >
          Dismiss
        </uui-button>
      </div>
    `;
  }

  // ────────────────────────────────────────────────────────────────────────
  // Render
  // ────────────────────────────────────────────────────────────────────────

  override render() {
    if (this._state.kind === "loading") {
      return html`<uui-box headline="LlmsTxt — Settings">
        <uui-loader-bar></uui-loader-bar>
        <p class="muted">Loading settings…</p>
      </uui-box>`;
    }
    if (this._state.kind === "error") {
      return html`<uui-box headline="LlmsTxt — Settings">
        <p class="error">Failed to load: ${this._state.message}</p>
        <uui-button look="secondary" @click=${() => void this.#fetchInitial()}>Retry</uui-button>
      </uui-box>`;
    }

    const { settings, doctypes } = this._state;
    // String.length counts UTF-16 code units; emoji + non-BMP characters
    // count as 2 each. Server-side validation uses `string.Length` which
    // matches exactly, so client and server agree on the cap. Documented
    // here so a future grapheme-based counter is an opt-in change.
    const summaryLength = this._formState.siteSummary.length;
    const summaryOverLimit = summaryLength > settings.summaryMaxChars;
    const canSave = this._canSave();

    return html`
      ${this.#renderOnboardingNotice()}
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
            @input=${(e: Event) => this.#onSiteNameInput(e)}
            placeholder="Override the H1 / site name on /llms.txt"
          ></uui-input>
        </div>

        <div class="field">
          <label for="siteSummary">Site summary</label>
          <uui-textarea
            id="siteSummary"
            .value=${this._formState.siteSummary}
            @input=${(e: Event) => this.#onSiteSummaryInput(e)}
            placeholder="One-paragraph summary emitted as the blockquote under the H1"
          ></uui-textarea>
          <div class="counter ${summaryOverLimit ? "over" : ""}">
            ${summaryLength} / ${settings.summaryMaxChars}
          </div>
          ${summaryOverLimit
            ? html`<div class="validation">Site summary cannot exceed ${settings.summaryMaxChars} characters.</div>`
            : nothing}
        </div>

        <div class="field">
          <label>Excluded content types</label>
          ${doctypes.length === 0
            ? html`<p class="muted">No content types are configured yet.</p>`
            : html`<div class="alias-list">
                ${doctypes.map((dt) => {
                  const checked = this._formState.excludedDoctypeAliases.some(
                    (a) => a.toLowerCase() === dt.alias.toLowerCase(),
                  );
                  return html`<label class="alias-row">
                    <input
                      type="checkbox"
                      .checked=${checked}
                      @change=${(e: Event) =>
                        this.#onAliasToggle(dt.alias, (e.target as HTMLInputElement).checked)}
                    />
                    <span class="alias-name">${dt.name}</span>
                    <code class="alias-code">${dt.alias}</code>
                  </label>`;
                })}
              </div>`}
        </div>

        <div class="actions">
          <uui-button
            look="primary"
            color="positive"
            ?disabled=${!canSave}
            @click=${() => void this.#onSave()}
          >
            ${this._saveState.kind === "saving" ? html`<uui-loader></uui-loader>&nbsp;Saving…` : "Save"}
          </uui-button>
          ${this.#renderSaveStatus()}
        </div>
      </uui-box>

      <uui-box headline="Excluded pages">
        <p class="muted">
          Pages whose <code>excludeFromLlmExports</code> property is enabled
          via the <code>llmsTxtSettingsComposition</code> composition. Toggle
          per-page exclusions in the standard Umbraco content tree.
        </p>
        ${this.#renderExcludedPages()}
      </uui-box>
    `;
  }

  #renderSaveStatus() {
    if (this._saveState.kind === "success") {
      return html`<span class="save-success">Settings saved.</span>`;
    }
    if (this._saveState.kind === "error") {
      return html`<span class="save-error">${this._saveState.message}</span>`;
    }
    return nothing;
  }

  #renderExcludedPages() {
    if (this._excludedPages.kind === "loading") {
      return html`<uui-loader-bar></uui-loader-bar>`;
    }
    if (this._excludedPages.kind === "error") {
      return html`<p class="error">${this._excludedPages.message}</p>
        <uui-button look="secondary" @click=${() => void this.#fetchExcludedPages()}>Retry</uui-button>`;
    }
    const { items, total } = this._excludedPages.page;
    if (items.length === 0) {
      return html`<p class="muted">No pages are currently excluded from LLM exports.</p>`;
    }
    return html`
      <p class="muted">Showing ${items.length} of ${total} excluded page${total === 1 ? "" : "s"}.</p>
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
          ${items.map(
            (p) => html`<tr>
              <td>${p.name}</td>
              <td><code>${p.path || "(no URL)"}</code></td>
              <td>${p.contentTypeName}</td>
              <td>${p.culture ?? "—"}</td>
            </tr>`,
          )}
        </tbody>
      </table>
    `;
  }

  static override styles = [
    css`
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
    `,
  ];
}

// Bellissima accepts `default` OR `element` named export from the dynamic
// import (Spike 0.B locked decision #7). Export both for belt-and-braces.
export const element = AiVisibilitySettingsDashboardElement;
export default AiVisibilitySettingsDashboardElement;

declare global {
  interface HTMLElementTagNameMap {
    "aiv-settings-dashboard": AiVisibilitySettingsDashboardElement;
  }
}
