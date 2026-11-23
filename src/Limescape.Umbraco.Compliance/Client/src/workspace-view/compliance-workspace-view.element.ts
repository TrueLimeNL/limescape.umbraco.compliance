import { css, customElement, html, nothing, ref, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { UMB_ENTITY_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/workspace";
import { UMB_CURRENT_USER_CONTEXT } from "@umbraco-cms/backoffice/current-user";
import { umbConfirmModal } from "@umbraco-cms/backoffice/modal";
import { formatDate, formatRule } from "../format.js";
import { LsApiError, LsRetentionApi, type ArchiveState, type ItemRetention, type ItemRetentionRule } from "./retention-api.js";

/**
 * Umbraco's confirm dialog focuses its confirm button; for a legal hold or archive the reason field takes the focus
 * afterwards, so typing (a space presses a focused button) can never confirm by accident.
 */
const focusAfterConfirmButton = (element?: Element) => {
  if (element instanceof HTMLElement) setTimeout(() => element.focus(), 400);
};

/** Enter explicitly confirms; spaces and other typing stay in the reason field. */
const confirmReasonOnEnter = (event: KeyboardEvent) => {
  if (event.key !== "Enter" || event.isComposing) return;
  const root = (event.currentTarget as Element).getRootNode();
  if (root instanceof ShadowRoot) {
    event.preventDefault();
    root.querySelector<HTMLElement>("#confirm")?.click();
  }
};
/**
 * Content app "Retention & audit" on documents and media: status, end date and the classes that apply,
 * each with its rule. Classes are managed in the Settings dashboard "Retention". Administrators can set or lift a legal
 * hold here; everyone else sees the status only. On documents with a lifecycle an owner or functional manager
 * can archive a published material after confirming it. The API is the real protection, buttons are only hidden.
 */
@customElement("ls-compliance-workspace-view")
export class LsComplianceWorkspaceViewElement extends UmbLitElement {
  #api = new LsRetentionApi(this);

  @state()
  private _key?: string;

  @state()
  private _retention?: ItemRetention;

  @state()
  private _loading = false;

  @state()
  private _error?: string;

  @state()
  private _isAdmin = false;

  @state()
  private _holdBusy = false;

  @state()
  private _holdStatus?: string;

  @state()
  private _holdError?: string;

  @state()
  private _archive?: ArchiveState;

  @state()
  private _archiveBusy = false;

  @state()
  private _archiveStatus?: string;

  @state()
  private _archiveError?: string;

  constructor() {
    super();
    this.consumeContext(UMB_CURRENT_USER_CONTEXT, (context) => {
      this.observe(context?.isAdmin, (isAdmin) => (this._isAdmin = isAdmin === true));
    });
    this.consumeContext(UMB_ENTITY_WORKSPACE_CONTEXT, (workspace) => {
      this.observe(workspace?.unique, (unique) => {
        this._key = unique ?? undefined;
        void this.#load();
      });
    });
  }

  async #load() {
    this._retention = undefined;
    this._error = undefined;
    this._holdStatus = undefined;
    this._holdError = undefined;
    this._archive = undefined;
    this._archiveError = undefined;
    if (!this._key) return;
    this._loading = true;
    try {
      this._retention = await this.#api.getItem(this._key);
      // Media and documents without a lifecycle have no archive section; an error here leaves the section out.
      this._archive = await this.#api.getArchiveState(this._key).catch(() => undefined);
    } catch (error) {
      this._error = error instanceof Error ? error.message : String(error);
    } finally {
      this._loading = false;
    }
  }

  /** Asks for confirmation and an optional reason, then sets or lifts the legal hold and shows the answer of the API. */
  async #toggleHold() {
    const retention = this._retention;
    if (!retention || this._holdBusy) return;
    const set = !retention.legalHold;
    let reason = "";
    try {
      await umbConfirmModal(this, {
        headline: this.localize.term(set ? "lsCompliance_holdSetHeadline" : "lsCompliance_holdLiftHeadline"),
        content: html`
          <p>${this.localize.term(set ? "lsCompliance_holdSetContent" : "lsCompliance_holdLiftContent")}</p>
          <uui-label for="legal-hold-reason">${this.localize.term("lsCompliance_holdReason")}</uui-label>
          <uui-textarea
            id="legal-hold-reason"
            @keydown=${confirmReasonOnEnter}
            ${ref(focusAfterConfirmButton)}
            maxlength="500"
            rows="3"
            label=${this.localize.term("lsCompliance_holdReason")}
            @input=${(e: Event) => (reason = (e.target as HTMLTextAreaElement).value)}></uui-textarea>
          <p>${this.localize.term("lsCompliance_holdReasonHint")}</p>
        `,
        color: set ? "warning" : "danger",
        confirmLabel: this.localize.term(set ? "lsCompliance_holdSet" : "lsCompliance_holdLift"),
      });
    } catch {
      return; // cancelled
    }
    this._holdBusy = true;
    this._holdStatus = undefined;
    this._holdError = undefined;
    try {
      this._retention = await this.#api.setLegalHold(retention.itemKey, set, reason.trim());
      this._holdStatus = this.localize.term(set ? "lsCompliance_holdIsSet" : "lsCompliance_holdIsLifted");
    } catch (error) {
      this._holdError =
        error instanceof LsApiError && error.code === "retention_legal_hold_requires_administrator"
          ? this.localize.term("lsCompliance_holdNeedsAdministrator")
          : this.localize.term("lsCompliance_actionFailed", error instanceof Error ? error.message : String(error));
    } finally {
      this._holdBusy = false;
    }
  }

  /** Asks for confirmation and an optional reason, then archives the document and shows the new retention period. */
  async #archiveDocument() {
    const state = this._archive;
    if (!state?.canArchive || this._archiveBusy) return;
    let reason = "";
    try {
      await umbConfirmModal(this, {
        headline: this.localize.term("lsCompliance_archiveConfirmHeadline"),
        content: html`
          <p>${this.localize.term("lsCompliance_archiveConfirmContent")}</p>
          <uui-label for="archive-reason">${this.localize.term("lsCompliance_archiveReason")}</uui-label>
          <uui-textarea
            id="archive-reason"
            ${ref(focusAfterConfirmButton)}
            maxlength="500"
            rows="3"
            label=${this.localize.term("lsCompliance_archiveReason")}
            @input=${(e: Event) => (reason = (e.target as HTMLTextAreaElement).value)}></uui-textarea>
          <p>${this.localize.term("lsCompliance_archiveReasonHint")}</p>
        `,
        color: "danger",
        confirmLabel: this.localize.term("lsCompliance_archiveAction"),
      });
    } catch {
      return; // cancelled
    }
    this._archiveBusy = true;
    this._archiveStatus = undefined;
    this._archiveError = undefined;
    try {
      this._archive = await this.#api.archive(state.documentKey, reason.trim());
      this._retention = await this.#api.getItem(state.documentKey);
      this._archiveStatus = this.localize.term("lsCompliance_archiveDone");
    } catch (error) {
      this._archiveError =
        error instanceof LsApiError && error.code === "archive_not_owner"
          ? this.localize.term("lsCompliance_archiveNotAllowed")
          : this.localize.term("lsCompliance_actionFailed", error instanceof Error ? error.message : String(error));
    } finally {
      this._archiveBusy = false;
    }
  }

  #renderArchive() {
    const state = this._archive;
    if (!state?.supported) return nothing;
    const status = state.status ? this.localize.term(`lsCompliance_lifecycle_${state.status}`) : "–";
    return html`
      <uui-box headline=${this.localize.term("lsCompliance_archiveHeadline")}>
        <dl class="summary">
          <div>
            <dt>${this.localize.term("lsCompliance_archiveStatus")}</dt>
            <dd>${status}</dd>
          </div>
          ${state.archivedUtc
            ? html`<div>
                <dt>${this.localize.term("lsCompliance_archivedOn")}</dt>
                <dd>${this.#date(state.archivedUtc)}</dd>
              </div>`
            : nothing}
        </dl>
        ${state.archived
          ? nothing
          : state.canArchive
            ? html`<uui-button
                look="primary"
                color="danger"
                label=${this.localize.term("lsCompliance_archiveAction")}
                state=${this._archiveBusy ? "waiting" : nothing}
                @click=${() => this.#archiveDocument()}></uui-button>`
            : html`<p>${this.localize.term(state.published ? "lsCompliance_archiveNotAllowed" : "lsCompliance_archiveNotPublished")}</p>`}
        <div class="hold-status" role="status">${this._archiveStatus ?? nothing}</div>
        ${this._archiveError ? html`<p role="alert" class="error">${this._archiveError}</p>` : nothing}
      </uui-box>
    `;
  }

  #renderHoldRow(retention: ItemRetention) {
    return html`<div>
      <dt>${this.localize.term("lsCompliance_legalHold")}</dt>
      <dd>
        ${this.localize.term(retention.legalHold ? "lsCompliance_yes" : "lsCompliance_no")}
        ${this._isAdmin
          ? html`<uui-button
              class="hold-button"
              look="secondary"
              color=${retention.legalHold ? "danger" : "warning"}
              compact
              label=${this.localize.term(retention.legalHold ? "lsCompliance_holdLift" : "lsCompliance_holdSet")}
              state=${this._holdBusy ? "waiting" : nothing}
              @click=${() => this.#toggleHold()}></uui-button>`
          : nothing}
      </dd>
    </div>`;
  }

  /** Result of the last change; the live region is always present so a screen reader announces it. */
  #renderHoldFeedback() {
    return html`
      <div class="hold-status" role="status">${this._holdStatus ?? nothing}</div>
      ${this._holdError ? html`<p role="alert" class="error">${this._holdError}</p>` : nothing}
    `;
  }

  override render() {
    return html`
      <uui-box headline=${this.localize.term("lsCompliance_retentionHeadline")}>
        <uui-button
          slot="header-actions"
          look="secondary"
          compact
          label=${this.localize.term("lsCompliance_refresh")}
          @click=${() => this.#load()}
          ?disabled=${this._loading || !this._key}></uui-button>
        ${this.#renderBody()}
      </uui-box>
      ${this.#renderArchive()}
    `;
  }

  #renderBody() {
    if (!this._key) return html`<p>${this.localize.term("lsCompliance_notSaved")}</p>`;
    if (this._loading) return html`<uui-loader-bar aria-label=${this.localize.term("lsCompliance_loading")}></uui-loader-bar>`;
    if (this._error) return html`<p role="alert">${this.localize.term("lsCompliance_loadFailed", this._error)}</p>`;
    const retention = this._retention;
    if (!retention) return html`<p>${this.localize.term("lsCompliance_notSaved")}</p>`;
    if (retention.status === "none" && !retention.legalHold) {
      // A hold does not depend on a retention class, so an administrator can still set one.
      return html`
        <p>${this.localize.term("lsCompliance_noClass")}</p>
        <dl class="summary">${this.#renderHoldRow(retention)}</dl>
        ${this.#renderHoldFeedback()}
      `;
    }

    const governing = retention.rules.find((r) => r.governing);
    return html`
      <dl class="summary">
        <div>
          <dt>${this.localize.term("lsCompliance_status")}</dt>
          <dd>
            <uui-tag look="primary" color=${this.#statusColor(retention)}>${this.localize.term(`lsCompliance_status_${retention.status}`)}</uui-tag>
          </dd>
        </div>
        <div>
          <dt>${this.localize.term("lsCompliance_until")}</dt>
          <dd>
            ${retention.openEnded
              ? this.localize.term("lsCompliance_openEnded")
              : retention.retentionUntil
                ? this.#date(retention.retentionUntil)
                : "–"}
          </dd>
        </div>
        ${governing
          ? html`<div>
              <dt>${this.localize.term("lsCompliance_governingClass")}</dt>
              <dd>${governing.className}</dd>
            </div>`
          : nothing}
        ${retention.endAction
          ? html`<div>
              <dt>${this.localize.term("lsCompliance_endAction")}</dt>
              <dd>${this.localize.term(`lsCompliance_endAction_${retention.endAction}`)}</dd>
            </div>`
          : nothing}
        ${this.#renderHoldRow(retention)}
      </dl>
      ${this.#renderHoldFeedback()}
      ${retention.heldFromEarlier ? html`<p class="note">${this.localize.term("lsCompliance_heldFromEarlier")}</p>` : nothing}
      ${retention.rules.length ? this.#renderRules(retention.rules) : nothing}
    `;
  }

  #renderRules(rules: Array<ItemRetentionRule>) {
    return html`
      <uui-table aria-label=${this.localize.term("lsCompliance_rulesCaption")}>
        <uui-table-head>
          <uui-table-head-cell>${this.localize.term("lsCompliance_class")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_rule")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_start")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_until")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_source")}</uui-table-head-cell>
        </uui-table-head>
        ${rules.map(
          (rule) => html`
            <uui-table-row>
              <uui-table-cell>
                ${rule.className}
                ${rule.governing ? html`<uui-tag look="secondary">${this.localize.term("lsCompliance_longest")}</uui-tag>` : nothing}
              </uui-table-cell>
              <uui-table-cell>${this.#rule(rule)}</uui-table-cell>
              <uui-table-cell>${rule.startDate ? this.#date(rule.startDate) : this.localize.term("lsCompliance_notStarted")}</uui-table-cell>
              <uui-table-cell>${rule.until ? this.#date(rule.until) : this.localize.term("lsCompliance_openEnded")}</uui-table-cell>
              <uui-table-cell>${this.localize.term(`lsCompliance_source_${rule.source}`)}</uui-table-cell>
            </uui-table-row>
          `,
        )}
      </uui-table>
    `;
  }

  #rule(rule: ItemRetentionRule) {
    return formatRule(this.localize, rule);
  }

  #date(value: string) {
    return formatDate(this.localize, value);
  }

  #statusColor(retention: ItemRetention) {
    switch (retention.status) {
      case "pending":
        return "warning";
      case "ended":
        return "positive";
      default:
        return "default";
    }
  }

  static override styles = [
    UmbTextStyles,
    css`
      :host {
        display: block;
        padding: var(--uui-size-layout-1);
      }

      uui-box + uui-box {
        margin-top: var(--uui-size-space-5);
      }

      .summary {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(12rem, 1fr));
        gap: var(--uui-size-space-4);
        margin: 0 0 var(--uui-size-space-5);
      }

      dt {
        font-weight: bold;
      }

      dd {
        margin: 0;
      }

      .hold-button {
        margin-left: var(--uui-size-space-3);
      }

      .hold-status {
        color: var(--uui-color-positive-standalone);
      }

      .hold-status:not(:empty) {
        margin-bottom: var(--uui-size-space-4);
      }

      .error {
        color: var(--uui-color-danger-standalone);
      }

      .note {
        color: var(--uui-color-warning-standalone);
      }

      uui-table-cell uui-tag {
        margin-left: var(--uui-size-space-2);
      }
    `,
  ];
}

export default LsComplianceWorkspaceViewElement;

declare global {
  interface HTMLElementTagNameMap {
    "ls-compliance-workspace-view": LsComplianceWorkspaceViewElement;
  }
}
