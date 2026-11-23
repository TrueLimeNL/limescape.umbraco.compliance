import { css, customElement, html, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import "./retention-assignments.element.js";
import "./retention-classes.element.js";
import "./retention-ended.element.js";
import "./shortening-notice.element.js";

type Tab = "classes" | "assignments" | "running" | "ended";
const TABS: Array<Tab> = ["classes", "assignments", "running", "ended"];

/**
 * Settings dashboard "Retention": manage retention classes and their assignments, see which periods run (with start and
 * end, for example after archiving) and which items have ended.
 * Shown to administrators and manager groups only; every change goes through the Management API, which enforces it.
 */
@customElement("ls-retention-dashboard")
export class LsRetentionDashboardElement extends UmbLitElement {
  @state()
  private _tab: Tab = "classes";

  #select(tab: Tab) {
    this._tab = tab;
  }

  #keydown(event: KeyboardEvent) {
    const index = TABS.indexOf(this._tab);
    const next = event.key === "ArrowRight" ? (index + 1) % TABS.length : event.key === "ArrowLeft" ? (index + TABS.length - 1) % TABS.length : -1;
    if (next < 0) return;
    event.preventDefault();
    this._tab = TABS[next];
    void this.updateComplete.then(() => this.shadowRoot?.querySelector<HTMLElement>(`#tab-${TABS[next]}`)?.focus());
  }

  override render() {
    return html`
      <div class="tabs" role="tablist" aria-label=${this.localize.term("lsCompliance_dashboardLabel")} @keydown=${this.#keydown}>
        ${TABS.map(
          (tab) => html`
            <button
              id="tab-${tab}"
              role="tab"
              type="button"
              aria-selected=${this._tab === tab}
              aria-controls="panel-${tab}"
              tabindex=${this._tab === tab ? 0 : -1}
              @click=${() => this.#select(tab)}>
              ${this.localize.term(`lsCompliance_tab_${tab}`)}
            </button>
          `,
        )}
      </div>
      <div id="panel-${this._tab}" role="tabpanel" aria-labelledby="tab-${this._tab}" tabindex="0">${this.#renderPanel()}</div>
    `;
  }

  #renderPanel() {
    switch (this._tab) {
      case "assignments":
        return html`<ls-retention-assignments></ls-retention-assignments>`;
      case "running":
        return html`<ls-retention-ended mode="running"></ls-retention-ended>`;
      case "ended":
        return html`<ls-retention-ended></ls-retention-ended>`;
      default:
        return html`<ls-retention-classes></ls-retention-classes>`;
    }
  }

  static override styles = [
    UmbTextStyles,
    css`
      :host {
        display: block;
        padding: var(--uui-size-layout-1);
      }

      .tabs {
        display: flex;
        gap: var(--uui-size-space-2);
        margin-bottom: var(--uui-size-space-5);
        border-bottom: 1px solid var(--uui-color-border);
      }

      button[role="tab"] {
        padding: var(--uui-size-space-3) var(--uui-size-space-5);
        border: 0;
        border-bottom: 3px solid transparent;
        background: none;
        color: var(--uui-color-text);
        font: inherit;
        cursor: pointer;
      }

      button[role="tab"][aria-selected="true"] {
        border-bottom-color: var(--uui-color-current);
        font-weight: bold;
      }

      button[role="tab"]:focus-visible,
      [role="tabpanel"]:focus-visible {
        outline: 2px solid var(--uui-color-focus);
        outline-offset: 2px;
      }
    `,
  ];
}

export default LsRetentionDashboardElement;

declare global {
  interface HTMLElementTagNameMap {
    "ls-retention-dashboard": LsRetentionDashboardElement;
  }
}
