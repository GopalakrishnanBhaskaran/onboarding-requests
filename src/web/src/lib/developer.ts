/** Developer tools (4 Nov 2026): simulations and deleting requests. */

/** A real request needs a reason of at least 5 characters (same rule as the API); a simulation needs none. */
export function deleteReasonError(simulated: boolean, reason: string): string | null {
  if (simulated) return null;
  return reason.trim().length >= 5 ? null : 'A real request needs a reason (at least 5 characters): it is kept in the audit trail.';
}
