import { css, customElement, html, nothing, property, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { formatDate, formatEndAction } from "../format.js";
import { LsRetentionApi, type EndedItem } from "../workspace-view/retention-api.js";

const PAGE = 50;

/**
 * Items whose retention period has ended, oldest end first; with mode "running" the items whose period has
 * started and not ended, with start and end date, earliest end first (an archived material's period).
 */
@customElement("ls-retention-ended")
export class LsRetentionEndedElement extends UmbLitElement {
  #api = new LsRetentionApi(this);

  @property()
  mode: "ended" | "running" = "ended";

  @state()
  private _items: Array<EndedItem> = [];

  @state()
  private _names = new Map<string, string>();

  @state()
  private _loading = true;

  @state()
  private _more = false;

  @state()
  private _error?: string;

  override connectedCallback() {
    super.connectedCallback();
    void this.#load(true);
  }

  async #load(reset: boolean) {
    this._loading = true;
    this._error = undefined;
    try {
      const skip = reset ? 0 : this._items.length;
      const page = this.mode === "running" ? await this.#api.getRunning(skip, PAGE) : await this.#api.getEnded(skip, PAGE);
      this._items = reset ? page : [...this._items, ...page];
      this._more = page.length === PAGE;
      const names = await this.#api.itemNames(page.map((item) => item.itemKey));
      this._names = reset ? names : new Map([...this._names, ...names]);
    } catch (error) {
      this._error = error instanceof Error ? error.message : String(error);
    } finally {
      this._loading = false;
    }
  }

  #href(item: EndedItem) {
    return item.entityType === "media"
      ? `/umbraco/section/media/workspace/media/edit/${item.itemKey}`
      : `/umbraco/section/content/workspace/document/edit/${item.itemKey}`;
  }

  override render() {
    return html`
      <uui-box headline=${this.localize.term(`lsCompliance_${this.mode}Headline`)}>
        <uui-button
          slot="header-actions"
          look="secondary"
          compact
          label=${this.localize.term("lsCompliance_refresh")}
          ?disabled=${this._loading}
          @click=${() => this.#load(true)}></uui-button>
        <p>${this.localize.term(`lsCompliance_${this.mode}Intro`)}</p>
        ${this._error ? html`<p role="alert" class="error">${this.localize.term("lsCompliance_loadFailed", this._error)}</p>` : nothing}
        ${this.#renderList()}
      </uui-box>
    `;
  }

  #renderList() {
    if (!this._items.length) {
      return this._loading
        ? html`<uui-loader-bar aria-label=${this.localize.term("lsCompliance_loading")}></uui-loader-bar>`
        : this._error
          ? nothing
          : html`<p>${this.localize.term(this.mode === "running" ? "lsCompliance_noRunning" : "lsCompliance_noEnded")}</p>`;
    }
    return html`
      <uui-table aria-label=${this.localize.term(`lsCompliance_${this.mode}Caption`)}>
        <uui-table-head>
          <uui-table-head-cell>${this.localize.term("lsCompliance_item")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_kind")}</uui-table-head-cell>
          ${this.mode === "running" ? html`<uui-table-head-cell>${this.localize.term("lsCompliance_startedOn")}</uui-table-head-cell>` : nothing}
          <uui-table-head-cell>${this.localize.term(this.mode === "running" ? "lsCompliance_endsOn" : "lsCompliance_endedOn")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_governingClass")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_endAction")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_legalHold")}</uui-table-head-cell>
        </uui-table-head>
        ${this._items.map((item) => {
          const governing = item.rules.find((rule) => rule.governing);
          return html`
            <uui-table-row>
              <uui-table-cell><a href=${this.#href(item)}>${this._names.get(item.itemKey) ?? item.itemKey}</a></uui-table-cell>
              <uui-table-cell>${this.localize.term(`lsCompliance_kind_${item.entityType}`)}</uui-table-cell>
              ${this.mode === "running"
                ? html`<uui-table-cell>${item.startDate ? formatDate(this.localize, item.startDate) : "–"}</uui-table-cell>`
                : nothing}
              <uui-table-cell>${item.retentionUntil ? formatDate(this.localize, item.retentionUntil) : "–"}</uui-table-cell>
              <uui-table-cell>${governing?.className ?? "–"}</uui-table-cell>
              <uui-table-cell>${item.endAction ? formatEndAction(this.localize, item.endAction) : "–"}</uui-table-cell>
              <uui-table-cell>${this.localize.term(item.legalHold ? "lsCompliance_yes" : "lsCompliance_no")}</uui-table-cell>
            </uui-table-row>
          `;
        })}
      </uui-table>
      ${this._more
        ? html`<uui-button
            look="secondary"
            label=${this.localize.term("lsCompliance_loadMore")}
            ?disabled=${this._loading}
            @click=${() => this.#load(false)}></uui-button>`
        : nothing}
    `;
  }

  static override styles = [
    UmbTextStyles,
    css`
      :host {
        display: block;
      }

      a {
        color: var(--uui-color-interactive);
      }

      uui-table {
        margin-bottom: var(--uui-size-space-4);
      }

      .error {
        color: var(--uui-color-danger-standalone);
      }
    `,
  ];
}

export default LsRetentionEndedElement;

declare global {
  interface HTMLElementTagNameMap {
    "ls-retention-ended": LsRetentionEndedElement;
  }
}
