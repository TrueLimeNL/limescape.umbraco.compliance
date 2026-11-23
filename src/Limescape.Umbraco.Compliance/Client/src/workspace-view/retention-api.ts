import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import type { UmbControllerHost } from "@umbraco-cms/backoffice/controller-api";
import { UmbControllerBase } from "@umbraco-cms/backoffice/class-api";

// Shapes returned by GET /umbraco/management/api/v1/compliance/retention/item/{key}.
export type RetentionStatus = "none" | "pending" | "active" | "ended";
export type RetentionStartEvent = "archived" | "published" | "lastModified" | "dateProperty";
export type RetentionEndAction = "allowDeletion" | "notify";
export type RetentionSource = "contentType" | "propertyValue" | "item";

export interface RetentionDuration {
  years: number;
  months: number;
  days: number;
}

export interface ItemRetentionRule {
  classKey: string;
  className: string;
  startEvent: RetentionStartEvent;
  startPropertyAlias?: string;
  duration: RetentionDuration;
  endAction: RetentionEndAction;
  source: RetentionSource;
  startDate?: string;
  until?: string;
  governing: boolean;
}

export interface ItemRetention {
  itemKey: string;
  entityType: string;
  status: RetentionStatus;
  rules: Array<ItemRetentionRule>;
  retentionUntil?: string;
  startDate?: string;
  openEnded: boolean;
  governingClassKey?: string;
  endAction?: RetentionEndAction;
  heldFromEarlier: boolean;
  legalHold: boolean;
  calculatedUtc?: string;
}

const BASE = "/umbraco/management/api/v1/compliance/retention";
const ARCHIVE = "/umbraco/management/api/v1/compliance/archive";
const MANAGEMENT = "/umbraco/management/api/v1";

export interface RetentionClass {
  key: string;
  name: string;
  description?: string;
  duration: RetentionDuration;
  startEvent: RetentionStartEvent;
  startPropertyAlias?: string;
  endAction: RetentionEndAction;
}

/** Body of POST and PUT class. */
export interface RetentionClassInput {
  name: string;
  description?: string;
  durationYears: number;
  durationMonths: number;
  durationDays: number;
  startEvent: RetentionStartEvent;
  startPropertyAlias?: string;
  endAction: RetentionEndAction;
}

export type RetentionAssignmentTarget = "contentType" | "propertyValue" | "item";

export interface RetentionAssignment {
  id: number;
  classKey: string;
  target: RetentionAssignmentTarget;
  contentTypeKey?: string;
  propertyAlias?: string;
  propertyValue?: string;
  itemKey?: string;
  createdUtc?: string;
}

/** Body of POST assignment. */
export interface RetentionAssignmentInput {
  classKey: string;
  target: RetentionAssignmentTarget;
  contentTypeKey?: string;
  propertyAlias?: string;
  propertyValue?: string;
  itemKey?: string;
}

export interface RetentionRecalculation {
  recalculated: number;
  changed: number;
  shorteningRefused: Array<string>;
}

/** An item of the ended list; same shape as the per-item retention. */
export type EndedItem = ItemRetention;

/** Shape returned by GET and POST compliance/archive/document/{key}. */
export interface ArchiveState {
  documentKey: string;
  status?: string;
  published: boolean;
  archivedUtc?: string;
  supported: boolean;
  canArchive: boolean;
  archived: boolean;
}

/** A failed call, with the stable error code of the package's problem+json answer when there is one. */
export class LsApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string | undefined,
    detail: string,
  ) {
    super(detail);
  }
}

/** Calls the package's Management API extension with the backoffice user's token. */
export class LsRetentionApi extends UmbControllerBase {
  #auth?: typeof UMB_AUTH_CONTEXT.TYPE;

  constructor(host: UmbControllerHost) {
    super(host);
    this.consumeContext(UMB_AUTH_CONTEXT, (auth) => {
      this.#auth = auth;
    });
  }

  /** Retention of the item, or undefined when the item does not exist (yet). Throws on other errors. */
  async getItem(key: string): Promise<ItemRetention | undefined> {
    try {
      return await this.#json<ItemRetention>(`${BASE}/item/${encodeURIComponent(key)}`);
    } catch (error) {
      if (error instanceof LsApiError && error.status === 404) return undefined;
      throw error;
    }
  }

  getClasses(): Promise<Array<RetentionClass>> {
    return this.#json(`${BASE}/class`);
  }

  createClass(input: RetentionClassInput): Promise<RetentionClass> {
    return this.#json(`${BASE}/class`, "POST", input);
  }

  /** Saves the class. When a shorter period is refused for items, they are listed in the recalculation. */
  async updateClass(key: string, input: RetentionClassInput): Promise<{ class: RetentionClass; recalculation: RetentionRecalculation }> {
    return this.#json(`${BASE}/class/${encodeURIComponent(key)}`, "PUT", input);
  }

  async deleteClass(key: string): Promise<void> {
    await this.#send(`${BASE}/class/${encodeURIComponent(key)}`, "DELETE");
  }

  getAssignments(): Promise<Array<RetentionAssignment>> {
    return this.#json(`${BASE}/assignment`);
  }

  createAssignment(input: RetentionAssignmentInput): Promise<{ assignment: RetentionAssignment; recalculation: RetentionRecalculation }> {
    return this.#json(`${BASE}/assignment`, "POST", input);
  }

  deleteAssignment(id: number): Promise<{ recalculation: RetentionRecalculation }> {
    return this.#json(`${BASE}/assignment/${id}`, "DELETE");
  }

  /** Recalculates one item; with confirmShortening an administrator accepts a shorter period. */
  recalculateItem(key: string, confirmShortening: boolean): Promise<{ recalculation: RetentionRecalculation; item?: ItemRetention }> {
    return this.#json(`${BASE}/item/${encodeURIComponent(key)}/recalculate?confirmShortening=${confirmShortening}`, "POST");
  }

  /** Sets or lifts the legal hold (administrators only; the server answers 403 for anyone else) and returns the item's retention. */
  setLegalHold(key: string, legalHold: boolean, reason?: string): Promise<ItemRetention> {
    return this.#json(`${BASE}/item/${encodeURIComponent(key)}/legal-hold`, "PUT", { legalHold, reason: reason || undefined });
  }

  async getEnded(skip: number, take: number): Promise<Array<EndedItem>> {
    const result = await this.#json<{ items: Array<EndedItem> }>(`${BASE}/ended?skip=${skip}&take=${take}`);
    return result.items;
  }

  /** Items whose retention period has started and not ended (for example archived ones), earliest end first. */
  async getRunning(skip: number, take: number): Promise<Array<EndedItem>> {
    const result = await this.#json<{ items: Array<EndedItem> }>(`${BASE}/running?skip=${skip}&take=${take}`);
    return result.items;
  }

  /** Whether the signed-in person may archive the document; undefined when it does not exist. */
  async getArchiveState(key: string): Promise<ArchiveState | undefined> {
    try {
      return await this.#json<ArchiveState>(`${ARCHIVE}/document/${encodeURIComponent(key)}`);
    } catch (error) {
      if (error instanceof LsApiError && error.status === 404) return undefined;
      throw error;
    }
  }

  /** Archives the document (owner or functional manager; the server checks it) and returns its new state. */
  archive(key: string, reason?: string): Promise<ArchiveState> {
    return this.#json(`${ARCHIVE}/document/${encodeURIComponent(key)}`, "POST", { reason: reason || undefined });
  }

  /** Names of documents and media by key; keys that cannot be found are left out. */
  async itemNames(keys: Array<string>): Promise<Map<string, string>> {
    return this.#names(keys, ["document", "media"]);
  }

  /** Names of document and media types by key. */
  async typeNames(keys: Array<string>): Promise<Map<string, { name: string; kind: "document" | "media" }>> {
    const names = new Map<string, { name: string; kind: "document" | "media" }>();
    for (const [path, kind] of [
      ["document-type", "document"],
      ["media-type", "media"],
    ] as const) {
      for (const [key, name] of await this.#names(keys, [path])) names.set(key, { name, kind });
    }
    return names;
  }

  async #names(keys: Array<string>, paths: Array<string>): Promise<Map<string, string>> {
    const names = new Map<string, string>();
    const distinct = [...new Set(keys)];
    if (!distinct.length) return names;
    const query = distinct.map((key) => `id=${encodeURIComponent(key)}`).join("&");
    for (const path of paths) {
      try {
        const items = await this.#json<Array<{ id: string; name?: string; variants?: Array<{ name: string }> }>>(`${MANAGEMENT}/item/${path}?${query}`);
        for (const item of items) names.set(item.id, item.name ?? item.variants?.[0]?.name ?? item.id);
      } catch {
        // A name is a convenience; the key is shown when it cannot be looked up.
      }
    }
    return names;
  }

  async #json<T>(path: string, method = "GET", body?: unknown): Promise<T> {
    const response = await this.#send(path, method, body);
    return (await response.json()) as T;
  }

  async #send(path: string, method: string, body?: unknown): Promise<Response> {
    const auth = this.#auth ?? (await this.getContext(UMB_AUTH_CONTEXT));
    if (!auth) throw new Error("No auth context");
    const config = auth.getOpenApiConfiguration();
    const token = await config.token();
    const headers: Record<string, string> = { Accept: "application/json" };
    if (token) headers.Authorization = `Bearer ${token}`;
    if (body !== undefined) headers["Content-Type"] = "application/json";
    const response = await fetch(`${config.base ?? ""}${path}`, {
      method,
      credentials: config.credentials,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (response.ok) return response;
    let code: string | undefined;
    let detail = `${response.status} ${response.statusText}`;
    try {
      const problem = (await response.json()) as { code?: string; detail?: string };
      code = problem.code;
      if (problem.detail) detail = problem.detail;
    } catch {
      // No problem body.
    }
    throw new LsApiError(response.status, code, detail);
  }
}
