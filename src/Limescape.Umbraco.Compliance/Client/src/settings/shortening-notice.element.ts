import { css, customElement, html, nothing, property, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { UMB_CURRENT_USER_CONTEXT } from "@umbraco-cms/backoffice/current-user";
import { LsRetentionApi } from "../workspace-view/retention-api.js";

const SHOWN = 10;

/**
 * Items that kept a longer retention period because a change would have shortened it. An administrator can
 * confirm the shortening per item set; everyone else sees that an administrator has to. Nothing shortens without that.
 */
@customElement("ls-shortening-notice")
export class LsShorteningNoticeElement extends UmbLitElement {
  #api = new LsRetentionApi(this);

  @property({ attribute: false })
  keys: Array<string> = [];

  @state()
  private _isAdmin = false;

  @state()
  private _names = new Map<string, string>();

  @state()
  private _busy = false;

  @state()
  private _error?: string;

  constructor() {
    super();
    this.consumeContext(UMB_CURRENT_USER_CONTEXT, (context) => {
      this.observe(context?.isAdmin, (isAdmin) => (this._isAdmin = isAdmin === true));
    });
  }

  override willUpdate(changed: Map<string, unknown>) {
    if (changed.has("keys")) void this.#loadNames();
  }

  async #loadNames() {
    this._names = await this.#api.itemNames(this.keys.slice(0, SHOWN));
  }

  async #confirm() {
    this._busy = true;
    this._error = undefined;
    try {
      for (const key of this.keys) await this.#api.recalculateItem(key, true);
      this.dispatchEvent(new CustomEvent("confirmed", { detail: { count: this.keys.length } }));
    } catch (error) {
      this._error = error instanceof Error ? error.message : String(error);
    } finally {
      this._busy = false;
    }
  }

  override render() {
    if (!this.keys.length) return nothing;
    const shown = this.keys.slice(0, SHOWN);
    return html`
      <uui-box headline=${this.localize.term("lsCompliance_shorteningHeadline")} role="alert">
        <p>${this.localize.term("lsCompliance_shorteningText", this.keys.length)}</p>
        <ul>
          ${shown.map((key) => html`<li>${this._names.get(key) ?? key}</li>`)}
        </ul>
        ${this.keys.length > SHOWN ? html`<p>${this.localize.term("lsCompliance_andMore", this.keys.length - SHOWN)}</p>` : nothing}
        ${this._error ? html`<p class="error">${this.localize.term("lsCompliance_actionFailed", this._error)}</p>` : nothing}
        ${this._isAdmin
          ? html`<div class="actions">
              <uui-button
                look="primary"
                color="warning"
                label=${this.localize.term("lsCompliance_confirmShortening")}
                ?disabled=${this._busy}
                @click=${() => this.#confirm()}></uui-button>
              <uui-button
                look="secondary"
                label=${this.localize.term("lsCompliance_keepLonger")}
                ?disabled=${this._busy}
                @click=${() => this.dispatchEvent(new CustomEvent("dismissed"))}></uui-button>
            </div>`
          : html`<p>${this.localize.term("lsCompliance_shorteningNeedsAdministrator")}</p>
              <uui-button
                look="secondary"
                label=${this.localize.term("lsCompliance_close")}
                @click=${() => this.dispatchEvent(new CustomEvent("dismissed"))}></uui-button>`}
      </uui-box>
    `;
  }

  static override styles = [
    UmbTextStyles,
    css`
      :host {
        display: block;
        margin-bottom: var(--uui-size-space-5);
      }

      .actions {
        display: flex;
        gap: var(--uui-size-space-3);
      }

      .error {
        color: var(--uui-color-danger-standalone);
      }
    `,
  ];
}

export default LsShorteningNoticeElement;

declare global {
  interface HTMLElementTagNameMap {
    "ls-shortening-notice": LsShorteningNoticeElement;
  }
}
