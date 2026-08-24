/**
 * A process-unique id for wiring a label, hint or error to the control it
 * describes.
 *
 * Needed because those relationships are expressed by id in HTML
 * (`for`, `aria-describedby`, `aria-labelledby`), and a component rendered twice
 * on one page -- two text fields, two dialogs -- would otherwise emit the same id
 * twice. Duplicate ids do not throw; they silently point every label at the first
 * match, which is a bug only a screen-reader user would ever notice.
 *
 * A counter rather than a random string or crypto.randomUUID(): it is
 * deterministic, which makes a rendered DOM diffable in a test, and it cannot
 * collide.
 */
let sequence = 0;

export function nextId(prefix: string): string {
  sequence += 1;
  return `${prefix}-${sequence}`;
}
