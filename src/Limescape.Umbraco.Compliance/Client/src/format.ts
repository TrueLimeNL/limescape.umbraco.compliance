import type { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import type { RetentionDuration, RetentionEndAction, RetentionStartEvent } from "./workspace-view/retention-api.js";

type Localize = UmbLitElement["localize"];

/** "7 years", "1 year and 6 months": the localized text of a retention duration. */
export function formatDuration(localize: Localize, duration: RetentionDuration): string {
  const parts: Array<string> = [];
  if (duration.years) parts.push(localize.term("lsCompliance_years", duration.years));
  if (duration.months) parts.push(localize.term("lsCompliance_months", duration.months));
  if (duration.days) parts.push(localize.term("lsCompliance_days", duration.days));
  return localize.list(parts, { type: "conjunction" });
}

/** "7 years after archiving": duration and start event of a class. */
export function formatRule(
  localize: Localize,
  rule: { duration: RetentionDuration; startEvent: RetentionStartEvent; startPropertyAlias?: string },
): string {
  const start = localize.term(`lsCompliance_startEvent_${rule.startEvent}`, rule.startPropertyAlias ?? "");
  return localize.term("lsCompliance_ruleText", formatDuration(localize, rule.duration), start);
}

export function formatEndAction(localize: Localize, action: RetentionEndAction): string {
  return localize.term(`lsCompliance_endAction_${action}`);
}

/** Dates from the API are UTC; they are shown as the UTC calendar date. */
export function formatDate(localize: Localize, value: string): string {
  return localize.date(value, { dateStyle: "long", timeZone: "UTC" });
}
