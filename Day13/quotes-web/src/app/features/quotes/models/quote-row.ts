import { Quote } from '../../../core/models/quote';

/**
 * A quote plus the two things about it that depend on who is looking.
 *
 * Neither is a fact about a quote -- both are facts about this quote and this
 * session together -- and they are deliberately separate, because they are not
 * the same question:
 *
 *   owned     -- this user created it. Shown as a badge.
 *   deletable -- this user may delete it. Shown as a control.
 *
 * Both are computed once in QuotesStore (see `rows`) so no component has to know
 * the ownership rule.
 */
export interface QuoteRow {
  readonly quote: Quote;
  readonly owned: boolean;
  readonly deletable: boolean;
}
