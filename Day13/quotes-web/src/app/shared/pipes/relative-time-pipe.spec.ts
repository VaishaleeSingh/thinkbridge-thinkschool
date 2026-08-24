import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { RelativeTimePipe } from './relative-time-pipe';

describe('RelativeTimePipe', () => {
  const pipe = new RelativeTimePipe();

  beforeEach(() => {
    // "Now" is pinned, because a test that asserts "2 hours ago" against the real
    // clock is a test that fails at midnight.
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-24T12:00:00Z'));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('reads a UTC timestamp as UTC even without a Z suffix', () => {
    // The case worth a test: the API serialises DateTime with no timezone
    // designator, and Date.parse treats a bare date-time as LOCAL. Without the
    // pipe appending Z, this reads as "5 hours ago" in Asia/Kolkata and something
    // different again in every other timezone.
    expect(pipe.transform('2026-08-24T10:00:00')).toBe('2 hours ago');
    expect(pipe.transform('2026-08-24T10:00:00Z')).toBe('2 hours ago');
  });

  it('picks the largest readable unit', () => {
    expect(pipe.transform('2026-08-24T11:59:30Z')).toBe('30 seconds ago');
    expect(pipe.transform('2026-08-24T11:30:00Z')).toBe('30 minutes ago');
    expect(pipe.transform('2026-08-21T12:00:00Z')).toBe('3 days ago');
  });

  it('says "never" for the null the API returns for an untouched collection', () => {
    expect(pipe.transform(null)).toBe('never');
    expect(pipe.transform(undefined)).toBe('never');
  });

  it('does not throw on an unparseable value', () => {
    expect(pipe.transform('not a date')).toBe('unknown');
  });
});
