import { Pipe, PipeTransform } from '@angular/core';

/**
 * "3 days ago" from an ISO-8601 timestamp.
 *
 * Used for `CollectionListItem.lastAddedAt` and `CollectionQuote.addedAt` -- both
 * of which the API returns as UTC strings, and both of which are only ever read
 * as "how recently", never as an exact instant. A full date there would be noise;
 * the exact value is still available to a hovering mouse via the `title`
 * attribute at the call site.
 *
 * Intl.RelativeTimeFormat rather than a hand-written table of thresholds: it is
 * built into the platform, it handles singular/plural, and it is translated for
 * every locale the browser knows -- which no bespoke "1 days ago" ever is.
 *
 * A pure pipe, with the caveat that "now" moves: the output is recomputed when
 * the value changes, not on a timer, so a page left open all afternoon keeps
 * saying "2 minutes ago". Correct for this application, where every list is
 * re-fetched on navigation; a ticking version would need a signal-based clock and
 * is not worth it here.
 */
@Pipe({ name: 'relativeTime' })
export class RelativeTimePipe implements PipeTransform {
  private readonly formatter = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

  transform(value: string | null | undefined): string {
    if (!value) {
      // The API returns null for a collection nothing has been added to yet,
      // which is a real state and not a missing value.
      return 'never';
    }

    const timestamp = Date.parse(this.asUtc(value));

    if (Number.isNaN(timestamp)) {
      return 'unknown';
    }

    const seconds = Math.round((timestamp - Date.now()) / 1000);
    const [unit, secondsPerUnit] = pickUnit(Math.abs(seconds));

    return this.formatter.format(Math.round(seconds / secondsPerUnit), unit);
  }

  /**
   * The API serialises DateTime values with no timezone designator (its
   * timestamps are UTC by construction -- IClock.UtcNow -- but
   * "2026-08-24T09:15:00" carries no Z). Date.parse treats a bare date-time as
   * LOCAL time, so without this every timestamp is off by the viewer's UTC
   * offset: in Asia/Kolkata a quote added seconds ago reads as "5 hours ago".
   */
  private asUtc(value: string): string {
    const hasTimezone = /(?:Z|[+-]\d{2}:?\d{2})$/.test(value);
    return hasTimezone ? value : `${value}Z`;
  }
}

/** Largest unit that leaves a number a person can read. */
function pickUnit(absoluteSeconds: number): [Intl.RelativeTimeFormatUnit, number] {
  if (absoluteSeconds < 60) {
    return ['second', 1];
  }
  if (absoluteSeconds < 3600) {
    return ['minute', 60];
  }
  if (absoluteSeconds < 86400) {
    return ['hour', 3600];
  }
  if (absoluteSeconds < 2592000) {
    return ['day', 86400];
  }
  if (absoluteSeconds < 31536000) {
    return ['month', 2592000];
  }
  return ['year', 31536000];
}
