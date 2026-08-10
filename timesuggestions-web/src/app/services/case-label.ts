/**
 * Drugorzędna metryczka sprawy: „K-2026-001 · Kowalski". Pola są w kontrakcie
 * nullable (nawigacja sprawy mogła nie zostać załadowana), więc składamy tylko
 * dostępne części; null, gdy nie ma czego pokazać — szablon pomija cały fragment
 * zamiast renderować osierocony separator.
 */
export function formatCaseMeta(caseNumber: string | null, clientName: string | null): string | null {
  const parts = [caseNumber, clientName].filter(
    (part): part is string => part !== null && part.trim().length > 0,
  );
  return parts.length > 0 ? parts.join(' · ') : null;
}
