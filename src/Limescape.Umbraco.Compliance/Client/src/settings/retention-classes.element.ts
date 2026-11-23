import { css, customElement, html, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { umbConfirmModal } from "@umbraco-cms/backoffice/modal";
import { formatEndAction, formatRule } from "../format.js";
import {
  LsRetentionApi,
  type RetentionClass,
  type RetentionClassInput,
  type RetentionEndAction,
  type RetentionStartEvent,
} from "../workspace-view/retention-api.js";

const START_EVENTS: Array<RetentionStartEvent> = ["archived", "published", "lastModified", "dateProperty"];
const END_ACTIONS: Array<RetentionEndAction> = ["allowDeletion", "notify"];

interface ClassForm {
  key?: string;
  name: string;
  description: string;
  years: number;
  months: number;
  days: number;
  startEvent: RetentionStartEvent;
  startPropertyAlias: string;
  endAction: RetentionEndAction;
}

const emptyForm = (): ClassForm => ({
  name: "",
  description: "",
  years: 7,
  months: 0,
  days: 0,
  startEvent: "archived",
  startPropertyAlias: "",
  endAction: "notify",
});

/** Retention classes: list, create, change and delete. A shorter period is shown and confirmed by an administrator. */
@customElement("ls-retention-classes")
export class LsRetentionClassesElement extends UmbLitElement {
  #api = new LsRetentionApi(this);

  @state()
  private _classes: Array<RetentionClass> = [];

  @state()
  private _loading = true;

  @state()
  private _error?: string;

  @state()
  private _form?: ClassForm;

  @state()
  private _saving = false;

  @state()
  private _formError?: string;

  @state()
  private _status?: string;

  @state()
  private _refused: Array<string> = [];

  override connectedCallback() {
    super.connectedCallback();
    void this.#load();
  }

  async #load() {
    this._loading = true;
    this._error = undefined;
    try {
      this._classes = await this.#api.getClasses();
    } catch (error) {
      this._error = error instanceof Error ? error.message : String(error);
    } finally {
      this._loading = false;
    }
  }

  #open(cls?: RetentionClass) {
    this._formError = undefined;
    this._status = undefined;
    this._refused = [];
    this._form = cls
      ? {
          key: cls.key,
          name: cls.name,
          description: cls.description ?? "",
          years: cls.duration.years,
          months: cls.duration.months,
          days: cls.duration.days,
          startEvent: cls.startEvent,
          startPropertyAlias: cls.startPropertyAlias ?? "",
          endAction: cls.endAction,
        }
      : emptyForm();
    void this.updateComplete.then(() => this.shadowRoot?.querySelector<HTMLElement>("#class-name")?.focus());
  }

  #close(focusSelector?: string) {
    this._form = undefined;
    this._formError = undefined;
    if (focusSelector) void this.updateComplete.then(() => this.shadowRoot?.querySelector<HTMLElement>(focusSelector)?.focus());
  }

  #set<K extends keyof ClassForm>(field: K, value: ClassForm[K]) {
    if (this._form) this._form = { ...this._form, [field]: value };
  }

  async #save(event: Event) {
    event.preventDefault();
    const form = this._form;
    if (!form) return;
    if (!form.name.trim()) {
      this._formError = this.localize.term("lsCompliance_nameRequired");
      return;
    }
    const input: RetentionClassInput = {
      name: form.name.trim(),
      description: form.description.trim() || undefined,
      durationYears: Number(form.years) || 0,
      durationMonths: Number(form.months) || 0,
      durationDays: Number(form.days) || 0,
      startEvent: form.startEvent,
      startPropertyAlias: form.startEvent === "dateProperty" ? form.startPropertyAlias.trim() : undefined,
      endAction: form.endAction,
    };
    this._saving = true;
    this._formError = undefined;
    try {
      let refused: Array<string> = [];
      if (form.key) {
        const result = await this.#api.updateClass(form.key, input);
        refused = result.recalculation.shorteningRefused;
        this._status = this.localize.term("lsCompliance_classSaved", result.class.name, result.recalculation.recalculated);
      } else {
        const created = await this.#api.createClass(input);
        this._status = this.localize.term("lsCompliance_classCreated", created.name);
      }
      this._refused = refused;
      this._form = undefined;
      await this.#load();
      await this.updateComplete;
      this.shadowRoot?.querySelector<HTMLElement>(refused.length ? "ls-shortening-notice" : "#new-class")?.focus();
    } catch (error) {
      this._formError = error instanceof Error ? error.message : String(error);
    } finally {
      this._saving = false;
    }
  }

  async #delete(cls: RetentionClass) {
    try {
      await umbConfirmModal(this, {
        headline: this.localize.term("lsCompliance_deleteClassHeadline", cls.name),
        content: this.localize.term("lsCompliance_deleteClassContent"),
        color: "danger",
        confirmLabel: this.localize.term("lsCompliance_delete"),
      });
    } catch {
      return; // cancelled
    }
    this._status = undefined;
    this._refused = [];
    try {
      await this.#api.deleteClass(cls.key);
      this._error = undefined;
      this._status = this.localize.term("lsCompliance_classDeleted", cls.name);
      await this.#load();
    } catch (error) {
      // retention_class_in_use shows the server's message: remove the assignments first.
      this._error = error instanceof Error ? error.message : String(error);
    }
  }

  override render() {
    return html`
      <uui-box headline=${this.localize.term("lsCompliance_classesHeadline")}>
        <uui-button
          id="new-class"
          slot="header-actions"
          look="primary"
          label=${this.localize.term("lsCompliance_newClass")}
          @click=${() => this.#open()}></uui-button>
        <p>${this.localize.term("lsCompliance_classesIntro")}</p>
        <div class="status" role="status">${this._status ?? nothing}</div>
        ${this._error ? html`<p role="alert" class="error">${this.localize.term("lsCompliance_actionFailed", this._error)}</p>` : nothing}
        ${this._refused.length
          ? html`<ls-shortening-notice
              tabindex="-1"
              .keys=${this._refused}
              @confirmed=${(e: CustomEvent<{ count: number }>) => {
                this._status = this.localize.term("lsCompliance_shorteningConfirmed", e.detail.count);
                this._refused = [];
              }}
              @dismissed=${() => (this._refused = [])}></ls-shortening-notice>`
          : nothing}
        ${this._form ? this.#renderForm(this._form) : nothing}
        ${this.#renderList()}
      </uui-box>
    `;
  }

  #renderList() {
    if (this._loading) return html`<uui-loader-bar aria-label=${this.localize.term("lsCompliance_loading")}></uui-loader-bar>`;
    if (!this._classes.length) return html`<p>${this.localize.term("lsCompliance_noClasses")}</p>`;
    return html`
      <uui-table aria-label=${this.localize.term("lsCompliance_classesCaption")}>
        <uui-table-head>
          <uui-table-head-cell>${this.localize.term("lsCompliance_name")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_rule")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_endAction")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_actions")}</uui-table-head-cell>
        </uui-table-head>
        ${this._classes.map(
          (cls) => html`
            <uui-table-row>
              <uui-table-cell>
                <strong>${cls.name}</strong>
                ${cls.description ? html`<div class="description">${cls.description}</div>` : nothing}
              </uui-table-cell>
              <uui-table-cell>${formatRule(this.localize, cls)}</uui-table-cell>
              <uui-table-cell>${formatEndAction(this.localize, cls.endAction)}</uui-table-cell>
              <uui-table-cell>
                <uui-button
                  look="secondary"
                  compact
                  label=${this.localize.term("lsCompliance_editNamed", cls.name)}
                  @click=${() => this.#open(cls)}>
                  ${this.localize.term("lsCompliance_edit")}
                </uui-button>
                <uui-button
                  look="secondary"
                  color="danger"
                  compact
                  label=${this.localize.term("lsCompliance_deleteNamed", cls.name)}
                  @click=${() => this.#delete(cls)}>
                  ${this.localize.term("lsCompliance_delete")}
                </uui-button>
              </uui-table-cell>
            </uui-table-row>
          `,
        )}
      </uui-table>
    `;
  }

  #renderForm(form: ClassForm) {
    const heading = form.key ? this.localize.term("lsCompliance_editClass") : this.localize.term("lsCompliance_newClass");
    const text = (id: string, label: string, value: string, set: (v: string) => void, required = false) => html`
      <uui-form-layout-item>
        <uui-label slot="label" for=${id} ?required=${required}>${label}</uui-label>
        <uui-input id=${id} label=${label} .value=${value} ?required=${required} @input=${(e: Event) => set((e.target as HTMLInputElement).value)}></uui-input>
      </uui-form-layout-item>
    `;
    const number = (id: string, label: string, value: number, set: (v: number) => void) => html`
      <uui-form-layout-item>
        <uui-label slot="label" for=${id}>${label}</uui-label>
        <uui-input id=${id} label=${label} type="number" min="0" .value=${String(value)} @input=${(e: Event) => set(Number((e.target as HTMLInputElement).value))}></uui-input>
      </uui-form-layout-item>
    `;
    return html`
      <form class="form" @submit=${(e: Event) => this.#save(e)} aria-label=${heading}>
        <h3>${heading}</h3>
        ${text("class-name", this.localize.term("lsCompliance_name"), form.name, (v) => this.#set("name", v), true)}
        ${text("class-description", this.localize.term("lsCompliance_description"), form.description, (v) => this.#set("description", v))}
        <fieldset>
          <legend>${this.localize.term("lsCompliance_duration")}</legend>
          <div class="duration">
            ${number("class-years", this.localize.term("lsCompliance_yearsLabel"), form.years, (v) => this.#set("years", v))}
            ${number("class-months", this.localize.term("lsCompliance_monthsLabel"), form.months, (v) => this.#set("months", v))}
            ${number("class-days", this.localize.term("lsCompliance_daysLabel"), form.days, (v) => this.#set("days", v))}
          </div>
        </fieldset>
        <uui-form-layout-item>
          <uui-label slot="label" for="class-start">${this.localize.term("lsCompliance_startEvent")}</uui-label>
          <uui-select
            id="class-start"
            label=${this.localize.term("lsCompliance_startEvent")}
            .options=${START_EVENTS.map((value) => ({
              name: this.localize.term(`lsCompliance_startEventOption_${value}`),
              value,
              selected: value === form.startEvent,
            }))}
            @change=${(e: Event) => this.#set("startEvent", (e.target as HTMLSelectElement).value as RetentionStartEvent)}></uui-select>
        </uui-form-layout-item>
        ${form.startEvent === "dateProperty"
          ? text("class-alias", this.localize.term("lsCompliance_startPropertyAlias"), form.startPropertyAlias, (v) => this.#set("startPropertyAlias", v), true)
          : nothing}
        <uui-form-layout-item>
          <uui-label slot="label" for="class-end">${this.localize.term("lsCompliance_endAction")}</uui-label>
          <uui-select
            id="class-end"
            label=${this.localize.term("lsCompliance_endAction")}
            .options=${END_ACTIONS.map((value) => ({
              name: formatEndAction(this.localize, value),
              value,
              selected: value === form.endAction,
            }))}
            @change=${(e: Event) => this.#set("endAction", (e.target as HTMLSelectElement).value as RetentionEndAction)}></uui-select>
        </uui-form-layout-item>
        ${form.key ? html`<p class="hint">${this.localize.term("lsCompliance_shorteningHint")}</p>` : nothing}
        ${this._formError ? html`<p role="alert" class="error">${this.localize.term("lsCompliance_actionFailed", this._formError)}</p>` : nothing}
        <div class="actions">
          <uui-button
            type="submit"
            look="primary"
            color="positive"
            label=${this.localize.term("lsCompliance_save")}
            ?disabled=${this._saving}
            .state=${this._saving ? "waiting" : undefined}></uui-button>
          <uui-button
            look="secondary"
            label=${this.localize.term("lsCompliance_cancel")}
            ?disabled=${this._saving}
            @click=${() => this.#close("#new-class")}></uui-button>
        </div>
      </form>
    `;
  }

  static override styles = [
    UmbTextStyles,
    css`
      :host {
        display: block;
      }

      .form {
        max-width: 40rem;
        margin-bottom: var(--uui-size-space-6);
        padding: var(--uui-size-space-5);
        border: 1px solid var(--uui-color-border);
      }

      fieldset {
        border: 0;
        margin: 0;
        padding: 0;
      }

      legend {
        font-weight: bold;
      }

      .duration {
        display: flex;
        gap: var(--uui-size-space-4);
      }

      uui-input,
      uui-select {
        width: 100%;
      }

      .actions {
        display: flex;
        gap: var(--uui-size-space-3);
      }

      .description,
      .hint {
        color: var(--uui-color-text-alt);
      }

      .error {
        color: var(--uui-color-danger-standalone);
      }

      .status:empty {
        display: none;
      }
    `,
  ];
}

export default LsRetentionClassesElement;

declare global {
  interface HTMLElementTagNameMap {
    "ls-retention-classes": LsRetentionClassesElement;
  }
}
