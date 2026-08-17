/**
 * Wspólna obsługa wywołań Microsoft Graph dla obu serwisów:
 * - walidacja adresu PRZED dołączeniem nagłówka Authorization (adresy z
 *   @odata.nextLink / @odata.deltaLink / localStorage nie mogą wysłać tokenu
 *   do obcej domeny),
 * - token pobierany per strona (MSAL cache'uje) — długi pierwszy przebieg
 *   delta nie padnie na wygasłym tokenie,
 * - ponowienia dla błędów przejściowych (429/502/503/504) z odczytem Retry-After
 *   (sekundy lub data HTTP, z górnym limitem oczekiwania),
 * - limit czasu przez AbortController obejmujący CAŁE żądanie łącznie z odczytem
 *   i parsowaniem body — zatrzymany strumień nie może wisieć bez limitu, dlatego
 *   helper zwraca już sparsowany JSON zamiast surowego Response.
 * Komunikaty błędów po polsku, bez tokenów i pełnych adresów.
 */

const ALLOWED_PROTOCOL = 'https:';
const ALLOWED_HOST = 'graph.microsoft.com';
const REQUEST_TIMEOUT_MS = 30_000;
/**
 * Osobny, dłuższy limit dla treści plików: strona JSON waży kilobajty, a wersja
 * dokumentu bywa wielomegabajtowa i na wolnym łączu 30 s potrafi nie wystarczyć.
 */
const CONTENT_TIMEOUT_MS = 60_000;
const MAX_ATTEMPTS = 3;
const RETRYABLE_STATUSES = new Set([429, 502, 503, 504]);
const DEFAULT_RETRY_DELAY_MS = 1000;
/** Górny limit czekania na ponowienie — Graph potrafi przysłać datę daleko w przyszłości. */
const MAX_RETRY_AFTER_MS = 60_000;

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
 * Strona odpowiedzi Graph ze sparsowanym body. Unia dyskryminowana po `ok`:
 * dla odpowiedzi nie-OK body nie jest czytane (decyzję podejmuje wywołujący
 * po samym statusie, np. 410 Gone dla delty).
 */
export type GraphPageResult<T> =
  | { ok: true; status: number; body: T }
  | { ok: false; status: number; body: null };

/**
 * Pobiera i parsuje jedną stronę z Graph. Zwraca też odpowiedzi nie-OK
 * (np. 410 Gone); ponawia wyłącznie błędy przejściowe.
 */
export async function fetchGraphPage<T>(
  url: string,
  getToken: () => Promise<string>,
  extraHeaders?: Record<string, string>,
): Promise<GraphPageResult<T>> {
  assertTrustedGraphUrl(url);

  for (let attempt = 1; ; attempt++) {
    const token = await getToken();

    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
    let response: Response;
    try {
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
      }

      if (!RETRYABLE_STATUSES.has(response.status) || attempt >= MAX_ATTEMPTS) {
        if (!response.ok) {
          return { ok: false, status: response.status, body: null };
        }

        // Body czytane i parsowane wciąż POD limitem czasu (timeout czyszczony
        // dopiero w finally) — serwer nie może zawiesić klienta zatrzymanym strumieniem.
        try {
          const body = (await response.json()) as T;
          return { ok: true, status: response.status, body };
        } catch {
          if (controller.signal.aborted) {
            throw new Error('Przekroczono limit czasu żądania do Microsoft Graph.');
          }
          throw new Error('Nieprawidłowa odpowiedź Microsoft Graph.');
        }
      }
    } finally {
      clearTimeout(timeout);
    }

    await delay(retryDelayMs(response, attempt));
  }
}

/**
 * Odpowiedź binarna (treść pliku lub wersji). Dla odpowiedzi nie-OK niesie kod
 * błędu z ciała Graph: sam status „400" nie mówi nic, a `invalidRequest`
 * czy `resourceModified` wskazuje wprost, co poszło nie tak.
 */
export type GraphBinaryResult =
  | { ok: true; status: number; bytes: Uint8Array }
  | { ok: false; status: number; errorCode: string | null };

/**
 * Pobiera zawartość binarną z Graph pod tym samym reżimem co strony JSON:
 * walidacja adresu, token per próba, ponowienia błędów przejściowych z Retry-After
 * i limit czasu obejmujący także odczyt strumienia. <paramref name="signal"/> to
 * anulowanie po stronie użytkownika — łączymy je z limitem czasu, bo to dwa
 * niezależne powody przerwania tego samego żądania.
 */
export async function fetchGraphBinary(
  url: string,
  getToken: () => Promise<string>,
  signal?: AbortSignal,
): Promise<GraphBinaryResult> {
  assertTrustedGraphUrl(url);

  for (let attempt = 1; ; attempt++) {
    if (signal?.aborted) {
      throw new GraphAbortedError();
    }

    const token = await getToken();

    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), CONTENT_TIMEOUT_MS);
    const abortFromCaller = () => controller.abort();
    signal?.addEventListener('abort', abortFromCaller, { once: true });

    let response: Response;
    try {
      try {
        response = await fetch(url, {
          headers: { Authorization: `Bearer ${token}` },
          signal: controller.signal,
        });
      } catch {
        if (signal?.aborted) {
          throw new GraphAbortedError();
        }
        if (controller.signal.aborted) {
          throw new Error('Przekroczono limit czasu pobierania z Microsoft Graph.');
        }
        throw new Error('Błąd połączenia z Microsoft Graph.');
      }

      if (!RETRYABLE_STATUSES.has(response.status) || attempt >= MAX_ATTEMPTS) {
        if (!response.ok) {
          return { ok: false, status: response.status, errorCode: await readErrorCode(response) };
        }

        // Odczyt strumienia wciąż POD limitem czasu — zatrzymane pobieranie
        // nie może wisieć bez granicy.
        try {
          return { ok: true, status: response.status, bytes: new Uint8Array(await response.arrayBuffer()) };
        } catch {
          if (signal?.aborted) {
            throw new GraphAbortedError();
          }
          throw new Error('Przerwane pobieranie treści z Microsoft Graph.');
        }
      }
    } finally {
      clearTimeout(timeout);
      signal?.removeEventListener('abort', abortFromCaller);
    }

    await delay(retryDelayMs(response, attempt));
  }
}

/** Anulowanie przez użytkownika — wywołujący rozpoznaje je i nie pokazuje błędu. */
export class GraphAbortedError extends Error {
  constructor() {
    super('Żądanie do Microsoft Graph zostało anulowane.');
  }
}

/**
 * Kod błędu z ciała odpowiedzi Graph (`error.code`). Ciało bywa puste albo nie-JSON,
 * dlatego brak kodu jest normalnym wynikiem, nie wyjątkiem.
 */
async function readErrorCode(response: Response): Promise<string | null> {
  try {
    const body = (await response.json()) as { error?: { code?: string } };
    return body.error?.code ?? null;
  } catch {
    return null;
  }
}

/**
 * Retry-After wg RFC 9110: liczba sekund ALBO data HTTP. Wynik przycinany do
 * MAX_RETRY_AFTER_MS (data w przeszłości → 0); wartość nieparsowalna → rosnący backoff.
 */
function retryDelayMs(response: Response, attempt: number): number {
  const header = response.headers.get('Retry-After')?.trim();
  const fallbackMs = DEFAULT_RETRY_DELAY_MS * attempt;
  if (!header) {
    return fallbackMs;
  }

  if (/^\d+$/.test(header)) {
    return Math.min(Number(header) * 1000, MAX_RETRY_AFTER_MS);
  }

  const dateMs = Date.parse(header);
  if (!Number.isNaN(dateMs)) {
    return Math.min(Math.max(dateMs - Date.now(), 0), MAX_RETRY_AFTER_MS);
  }

  return fallbackMs;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
