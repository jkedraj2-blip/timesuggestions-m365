/**
 * Wspólna obsługa wywołań Microsoft Graph dla obu serwisów:
 * - walidacja adresu PRZED dołączeniem nagłówka Authorization (adresy z
 *   @odata.nextLink / @odata.deltaLink / localStorage nie mogą wysłać tokenu
 *   do obcej domeny),
 * - token pobierany per strona (MSAL cache'uje) — długi pierwszy przebieg
 *   delta nie padnie na wygasłym tokenie,
 * - ponowienia dla błędów przejściowych (429/502/503/504) z odczytem Retry-After,
 * - limit czasu żądania przez AbortController.
 * Komunikaty błędów po polsku, bez tokenów i pełnych adresów.
 */

const ALLOWED_PROTOCOL = 'https:';
const ALLOWED_HOST = 'graph.microsoft.com';
const REQUEST_TIMEOUT_MS = 30_000;
const MAX_ATTEMPTS = 3;
const RETRYABLE_STATUSES = new Set([429, 502, 503, 504]);
const DEFAULT_RETRY_DELAY_MS = 1000;

/** Rzuca, gdy adres nie jest HTTPS-owym adresem hosta graph.microsoft.com. */
export function assertTrustedGraphUrl(url: string): void {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    throw new Error('Nieprawidłowy adres żądania do Microsoft Graph.');
  }

  if (parsed.protocol !== ALLOWED_PROTOCOL || parsed.host !== ALLOWED_HOST) {
    // Celowo bez pełnego adresu w komunikacie — nie powtarzamy podejrzanego URL.
    throw new Error('Zablokowano żądanie do adresu spoza https://graph.microsoft.com.');
  }
}

/**
 * Pobiera jedną stronę z Graph. Zwraca odpowiedź (także nie-OK, np. 410 Gone —
 * decyzję podejmuje wywołujący); ponawia wyłącznie błędy przejściowe.
 */
export async function fetchGraphPage(
  url: string,
  getToken: () => Promise<string>,
  extraHeaders?: Record<string, string>,
): Promise<Response> {
  assertTrustedGraphUrl(url);

  for (let attempt = 1; ; attempt++) {
    const token = await getToken();

    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
    let response: Response;
    try {
      response = await fetch(url, {
        headers: { Authorization: `Bearer ${token}`, ...extraHeaders },
        signal: controller.signal,
      });
    } catch {
      if (controller.signal.aborted) {
        throw new Error('Przekroczono limit czasu żądania do Microsoft Graph.');
      }
      throw new Error('Błąd połączenia z Microsoft Graph.');
    } finally {
      clearTimeout(timeout);
    }

    if (!RETRYABLE_STATUSES.has(response.status) || attempt >= MAX_ATTEMPTS) {
      return response;
    }

    await delay(retryDelayMs(response, attempt));
  }
}

/** Retry-After w sekundach, gdy Graph go podaje; inaczej prosty rosnący backoff. */
function retryDelayMs(response: Response, attempt: number): number {
  const retryAfterSeconds = Number(response.headers.get('Retry-After'));
  if (Number.isFinite(retryAfterSeconds) && retryAfterSeconds > 0) {
    return retryAfterSeconds * 1000;
  }
  return DEFAULT_RETRY_DELAY_MS * attempt;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
