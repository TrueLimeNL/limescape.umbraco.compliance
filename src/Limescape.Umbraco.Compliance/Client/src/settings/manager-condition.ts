import { UmbConditionBase } from "@umbraco-cms/backoffice/extension-registry";
import type { UmbConditionControllerArguments, UmbExtensionCondition } from "@umbraco-cms/backoffice/extension-api";
import { UMB_CURRENT_USER_CONTEXT } from "@umbraco-cms/backoffice/current-user";
import type { UmbControllerHost } from "@umbraco-cms/backoffice/controller-api";
import { LsRetentionApi } from "../workspace-view/retention-api.js";

/**
 * Permitted for administrators and for users in one of the configured manager groups (Compliance:Retention:ManagerGroups).
 * The groups are server configuration, so for non-administrators the condition asks the API: the list of ended items needs
 * the same permission as changing classes. This only decides what is shown; the API stays the real protection.
 */
export class LsRetentionManagerCondition extends UmbConditionBase<{ alias: string }> implements UmbExtensionCondition {
  constructor(host: UmbControllerHost, args: UmbConditionControllerArguments<{ alias: string }>) {
    super(host, args);
    this.consumeContext(UMB_CURRENT_USER_CONTEXT, (context) => {
      if (!context) return;
      this.observe(context.isAdmin, (isAdmin) => {
        if (isAdmin === undefined) return; // the user is still loading
        this.#generation++;
        if (isAdmin) {
          this.permitted = true;
          return;
        }
        void this.#probe(host, this.#generation);
      });
    });
  }

  /** Guards against a slow probe overwriting a newer answer. */
  #generation = 0;

  async #probe(host: UmbControllerHost, generation: number) {
    let permitted = false;
    try {
      await new LsRetentionApi(host).getEnded(0, 1);
      permitted = true;
    } catch {
      // 403 (not a manager) or any other failure: do not show the dashboard.
    }
    if (generation === this.#generation) this.permitted = permitted;
  }
}

export const api = LsRetentionManagerCondition;
