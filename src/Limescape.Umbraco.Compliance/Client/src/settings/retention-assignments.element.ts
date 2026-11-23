import { css, customElement, html, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { umbConfirmModal, umbOpenModal } from "@umbraco-cms/backoffice/modal";
import { UMB_DOCUMENT_TYPE_PICKER_MODAL } from "@umbraco-cms/backoffice/document-type";
import { UMB_MEDIA_TYPE_PICKER_MODAL } from "@umbraco-cms/backoffice/media-type";
import { UMB_DOCUMENT_PICKER_MODAL } from "@umbraco-cms/backoffice/document";
import { UMB_MEDIA_PICKER_MODAL } from "@umbraco-cms/backoffice/media";
import {
  LsRetentionApi,
  type RetentionAssignment,
  type RetentionAssignmentInput,
  type RetentionAssignmentTarget,
  type RetentionClass,
} from "../workspace-view/retention-api.js";

const TARGETS: Array<RetentionAssignmentTarget> = ["contentType", "propertyValue", "item"];

interface Picked {
  key: string;
  name: string;
}

interface AssignmentForm {
  classKey: string;
  target: RetentionAssignmentTarget;
  contentType?: Picked;
  propertyAlias: string;
  propertyValue: string;
  item?: Picked;
}

/** Assignments of retention classes: to a document or media type, to a category (property value) or to one item. */
@customElement("ls-retention-assignments")
export class LsRetentionAssignmentsElement extends UmbLitElement {
  #api = new LsRetentionApi(this);

  @state()
  private _classes: Array<RetentionClass> = [];

  @state()
  private _assignments: Array<RetentionAssignment> = [];

  @state()
  private _typeNames = new Map<string, string>();

  @state()
  private _itemNames = new Map<string, string>();

  @state()
  private _loading = true;

  @state()
  private _error?: string;

  @state()
  private _form?: AssignmentForm;

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
      [this._classes, this._assignments] = await Promise.all([this.#api.getClasses(), this.#api.getAssignments()]);
      const types = await this.#api.typeNames(this._assignments.flatMap((a) => (a.contentTypeKey ? [a.contentTypeKey] : [])));
      this._typeNames = new Map([...types].map(([key, value]) => [key, value.name]));
      this._itemNames = await this.#api.itemNames(this._assignments.flatMap((a) => (a.itemKey ? [a.itemKey] : [])));
    } catch (error) {
      this._error = error instanceof Error ? error.message : String(error);
    } finally {
      this._loading = false;
    }
  }

  #open() {
    this._status = undefined;
    this._formError = undefined;
    this._refused = [];
    this._form = { classKey: this._classes[0]?.key ?? "", target: "contentType", propertyAlias: "", propertyValue: "" };
    void this.updateComplete.then(() => this.shadowRoot?.querySelector<HTMLElement>("#assignment-class")?.focus());
  }

  #close() {
    this._form = undefined;
    this._formError = undefined;
    void this.updateComplete.then(() => this.shadowRoot?.querySelector<HTMLElement>("#new-assignment")?.focus());
  }

  #set<K extends keyof AssignmentForm>(field: K, value: AssignmentForm[K]) {
    if (this._form) this._form = { ...this._form, [field]: value };
  }

  /** Opens one of Umbraco's own pickers; resolves to the chosen key and name, or undefined when cancelled. */
  async #pick(kind: "documentType" | "mediaType" | "document" | "media"): Promise<Picked | undefined> {
    try {
      const options = { data: { multiple: false }, value: { selection: [] as Array<string | null> } };
      const value =
        kind === "documentType"
          ? await umbOpenModal(this, UMB_DOCUMENT_TYPE_PICKER_MODAL, options)
          : kind === "mediaType"
            ? await umbOpenModal(this, UMB_MEDIA_TYPE_PICKER_MODAL, options)
            : kind === "document"
              ? await umbOpenModal(this, UMB_DOCUMENT_PICKER_MODAL, options)
              : await umbOpenModal(this, UMB_MEDIA_PICKER_MODAL, options);
      const key = value?.selection?.[0];
      if (!key) return undefined;
      const names = kind === "documentType" || kind === "mediaType" ? new Map([...(await this.#api.typeNames([key]))].map(([k, v]) => [k, v.name])) : await this.#api.itemNames([key]);
      return { key, name: names.get(key) ?? key };
    } catch {
      return undefined; // cancelled
    }
  }

  async #pickContentType(kind: "documentType" | "mediaType") {
    const picked = await this.#pick(kind);
    if (picked) this.#set("contentType", picked);
  }

  async #pickItem(kind: "document" | "media") {
    const picked = await this.#pick(kind);
    if (picked) this.#set("item", picked);
  }

  async #save(event: Event) {
    event.preventDefault();
    const form = this._form;
    if (!form) return;
    const input: RetentionAssignmentInput = { classKey: form.classKey, target: form.target };
    if (!form.classKey) {
      this._formError = this.localize.term("lsCompliance_classRequired");
      return;
    }
    switch (form.target) {
      case "contentType":
        if (!form.contentType) return this.#invalid("lsCompliance_typeRequired");
        input.contentTypeKey = form.contentType.key;
        break;
      case "propertyValue":
        if (!form.propertyAlias.trim() || !form.propertyValue.trim()) return this.#invalid("lsCompliance_categoryRequired");
        input.propertyAlias = form.propertyAlias.trim();
        input.propertyValue = form.propertyValue.trim();
        input.contentTypeKey = form.contentType?.key;
        break;
      case "item":
        if (!form.item) return this.#invalid("lsCompliance_itemRequired");
        input.itemKey = form.item.key;
        break;
    }
    this._saving = true;
    this._formError = undefined;
    try {
      const result = await this.#api.createAssignment(input);
      this._refused = result.recalculation.shorteningRefused;
      this._status = this.localize.term("lsCompliance_assignmentCreated", result.recalculation.recalculated);
      this._form = undefined;
      await this.#load();
      await this.updateComplete;
      this.shadowRoot?.querySelector<HTMLElement>("#new-assignment")?.focus();
    } catch (error) {
      this._formError = error instanceof Error ? error.message : String(error);
    } finally {
      this._saving = false;
    }
  }

  #invalid(term: string) {
    this._formError = this.localize.term(term);
  }

  async #delete(assignment: RetentionAssignment) {
    try {
      await umbConfirmModal(this, {
        headline: this.localize.term("lsCompliance_deleteAssignmentHeadline"),
        content: this.localize.term("lsCompliance_deleteAssignmentContent", this.#target(assignment)),
        color: "danger",
        confirmLabel: this.localize.term("lsCompliance_delete"),
      });
    } catch {
      return; // cancelled
    }
    this._status = undefined;
    this._refused = [];
    try {
      const result = await this.#api.deleteAssignment(assignment.id);
      this._error = undefined;
      this._refused = result.recalculation.shorteningRefused;
      this._status = this.localize.term("lsCompliance_assignmentDeleted", result.recalculation.recalculated);
      await this.#load();
    } catch (error) {
      this._error = error instanceof Error ? error.message : String(error);
    }
  }

  #className(key: string) {
    return this._classes.find((c) => c.key === key)?.name ?? key;
  }

  /** What the assignment applies to, as text. */
  #target(assignment: RetentionAssignment) {
    const type = assignment.contentTypeKey ? (this._typeNames.get(assignment.contentTypeKey) ?? assignment.contentTypeKey) : undefined;
    switch (assignment.target) {
      case "contentType":
        return this.localize.term("lsCompliance_targetContentType", type ?? "");
      case "propertyValue":
        return type
          ? this.localize.term("lsCompliance_targetCategoryOfType", assignment.propertyAlias ?? "", assignment.propertyValue ?? "", type)
          : this.localize.term("lsCompliance_targetCategory", assignment.propertyAlias ?? "", assignment.propertyValue ?? "");
      default:
        return this.localize.term("lsCompliance_targetItem", assignment.itemKey ? (this._itemNames.get(assignment.itemKey) ?? assignment.itemKey) : "");
    }
  }

  override render() {
    return html`
      <uui-box headline=${this.localize.term("lsCompliance_assignmentsHeadline")}>
        <uui-button
          id="new-assignment"
          slot="header-actions"
          look="primary"
          label=${this.localize.term("lsCompliance_newAssignment")}
          ?disabled=${!this._classes.length}
          @click=${() => this.#open()}></uui-button>
        <p>${this.localize.term("lsCompliance_assignmentsIntro")}</p>
        <div class="status" role="status">${this._status ?? nothing}</div>
        ${this._error ? html`<p role="alert" class="error">${this.localize.term("lsCompliance_actionFailed", this._error)}</p>` : nothing}
        ${this._refused.length
          ? html`<ls-shortening-notice
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
    if (!this._classes.length) return html`<p>${this.localize.term("lsCompliance_createClassFirst")}</p>`;
    if (!this._assignments.length) return html`<p>${this.localize.term("lsCompliance_noAssignments")}</p>`;
    return html`
      <uui-table aria-label=${this.localize.term("lsCompliance_assignmentsCaption")}>
        <uui-table-head>
          <uui-table-head-cell>${this.localize.term("lsCompliance_class")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_appliesTo")}</uui-table-head-cell>
          <uui-table-head-cell>${this.localize.term("lsCompliance_actions")}</uui-table-head-cell>
        </uui-table-head>
        ${this._assignments.map(
          (assignment) => html`
            <uui-table-row>
              <uui-table-cell>${this.#className(assignment.classKey)}</uui-table-cell>
              <uui-table-cell>${this.#target(assignment)}</uui-table-cell>
              <uui-table-cell>
                <uui-button
                  look="secondary"
                  color="danger"
                  compact
                  label=${this.localize.term("lsCompliance_deleteAssignmentNamed", this.#className(assignment.classKey), this.#target(assignment))}
                  @click=${() => this.#delete(assignment)}>
                  ${this.localize.term("lsCompliance_delete")}
                </uui-button>
              </uui-table-cell>
            </uui-table-row>
          `,
        )}
      </uui-table>
    `;
  }

  #renderForm(form: AssignmentForm) {
    const heading = this.localize.term("lsCompliance_newAssignment");
    const typeButtons = html`
      <div class="pick">
        <uui-button look="secondary" label=${this.localize.term("lsCompliance_chooseDocumentType")} @click=${() => this.#pickContentType("documentType")}></uui-button>
        <uui-button look="secondary" label=${this.localize.term("lsCompliance_chooseMediaType")} @click=${() => this.#pickContentType("mediaType")}></uui-button>
        <span class="picked" aria-live="polite">${form.contentType?.name ?? this.localize.term("lsCompliance_nothingChosen")}</span>
        ${form.contentType && form.target === "propertyValue"
          ? html`<uui-button look="secondary" compact label=${this.localize.term("lsCompliance_clearType")} @click=${() => this.#set("contentType", undefined)}></uui-button>`
          : nothing}
      </div>
    `;
    return html`
      <form class="form" @submit=${(e: Event) => this.#save(e)} aria-label=${heading}>
        <h3>${heading}</h3>
        <uui-form-layout-item>
          <uui-label slot="label" for="assignment-class" required>${this.localize.term("lsCompliance_class")}</uui-label>
          <uui-select
            id="assignment-class"
            label=${this.localize.term("lsCompliance_class")}
            .options=${this._classes.map((c) => ({ name: c.name, value: c.key, selected: c.key === form.classKey }))}
            @change=${(e: Event) => this.#set("classKey", (e.target as HTMLSelectElement).value)}></uui-select>
        </uui-form-layout-item>
        <uui-form-layout-item>
          <uui-label slot="label" for="assignment-target" required>${this.localize.term("lsCompliance_appliesTo")}</uui-label>
          <uui-select
            id="assignment-target"
            label=${this.localize.term("lsCompliance_appliesTo")}
            .options=${TARGETS.map((value) => ({
              name: this.localize.term(`lsCompliance_targetOption_${value}`),
              value,
              selected: value === form.target,
            }))}
            @change=${(e: Event) => this.#set("target", (e.target as HTMLSelectElement).value as RetentionAssignmentTarget)}></uui-select>
        </uui-form-layout-item>
        ${form.target === "contentType"
          ? html`<fieldset>
              <legend>${this.localize.term("lsCompliance_contentType")}</legend>
              ${typeButtons}
            </fieldset>`
          : nothing}
        ${form.target === "propertyValue"
          ? html`
              <uui-form-layout-item>
                <uui-label slot="label" for="assignment-alias" required>${this.localize.term("lsCompliance_propertyAlias")}</uui-label>
                <uui-input
                  id="assignment-alias"
                  label=${this.localize.term("lsCompliance_propertyAlias")}
                  required
                  .value=${form.propertyAlias}
                  @input=${(e: Event) => this.#set("propertyAlias", (e.target as HTMLInputElement).value)}></uui-input>
              </uui-form-layout-item>
              <uui-form-layout-item>
                <uui-label slot="label" for="assignment-value" required>${this.localize.term("lsCompliance_propertyValue")}</uui-label>
                <uui-input
                  id="assignment-value"
                  label=${this.localize.term("lsCompliance_propertyValue")}
                  required
                  .value=${form.propertyValue}
                  @input=${(e: Event) => this.#set("propertyValue", (e.target as HTMLInputElement).value)}></uui-input>
              </uui-form-layout-item>
              <fieldset>
                <legend>${this.localize.term("lsCompliance_limitToType")}</legend>
                ${typeButtons}
              </fieldset>
            `
          : nothing}
        ${form.target === "item"
          ? html`<fieldset>
              <legend>${this.localize.term("lsCompliance_item")}</legend>
              <div class="pick">
                <uui-button look="secondary" label=${this.localize.term("lsCompliance_chooseDocument")} @click=${() => this.#pickItem("document")}></uui-button>
                <uui-button look="secondary" label=${this.localize.term("lsCompliance_chooseMedia")} @click=${() => this.#pickItem("media")}></uui-button>
                <span class="picked" aria-live="polite">${form.item?.name ?? this.localize.term("lsCompliance_nothingChosen")}</span>
              </div>
            </fieldset>`
          : nothing}
        ${this._formError ? html`<p role="alert" class="error">${this.localize.term("lsCompliance_actionFailed", this._formError)}</p>` : nothing}
        <div class="actions">
          <uui-button
            type="submit"
            look="primary"
            color="positive"
            label=${this.localize.term("lsCompliance_save")}
            ?disabled=${this._saving}
            .state=${this._saving ? "waiting" : undefined}></uui-button>
          <uui-button look="secondary" label=${this.localize.term("lsCompliance_cancel")} ?disabled=${this._saving} @click=${() => this.#close()}></uui-button>
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
        margin: 0 0 var(--uui-size-space-4);
        padding: 0;
      }

      legend {
        font-weight: bold;
      }

      uui-input,
      uui-select {
        width: 100%;
      }

      .pick,
      .actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--uui-size-space-3);
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

export default LsRetentionAssignmentsElement;

declare global {
  interface HTMLElementTagNameMap {
    "ls-retention-assignments": LsRetentionAssignmentsElement;
  }
}
