/** Jednolita obsługa błędów stron: kontekst + szczegóły, jeśli błąd je niesie. */
export function toUserMessage(error: unknown, fallback: string): string {
  return error instanceof Error ? `${fallback} ${error.message}` : fallback;
}
