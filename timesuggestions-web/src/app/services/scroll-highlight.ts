/**
 * Wspólny wzorzec "przewiń i pokaż" — wyciągnięty z otwierania formularza edycji
 * sprawy (cases-page), używany też przez nawigację z osi czasu do konkretnej
 * pozycji na innej zakładce.
 */

/** Czas życia podświetlenia — zgrany z animacją .flash-highlight w styles.css. */
const HIGHLIGHT_MS = 2000;

/** Ile czekamy, aż docelowa zakładka załaduje dane i wyrenderuje element. */
const FIND_TIMEOUT_MS = 4000;

const FIND_INTERVAL_MS = 100;

/**
 * Płynność przewijania kontroluje media query prefers-reduced-motion w styles.css
 * (scroll-behavior na html) — tu tylko żądanie przewinięcia. Wywołanie opcjonalne:
 * środowisko testowe (jsdom) nie implementuje scrollIntoView.
 */
export function scrollToElement(element: HTMLElement, block: ScrollLogicalPosition = 'center'): void {
  element.scrollIntoView?.({ block });
}

/**
 * Czeka, aż element o podanym id pojawi się w DOM (docelowa zakładka może dopiero
 * ładować dane), przewija do niego i chwilowo podświetla. false = element nie
 * pojawił się w limicie czasu — wołający pokazuje toast z wyjaśnieniem, nie ciszę.
 */
export async function scrollToAndHighlight(elementId: string): Promise<boolean> {
  const deadline = Date.now() + FIND_TIMEOUT_MS;

  for (;;) {
    const element = document.getElementById(elementId);
    if (element) {
      scrollToElement(element);
      element.classList.add('flash-highlight');
      setTimeout(() => element.classList.remove('flash-highlight'), HIGHLIGHT_MS);
      return true;
    }

    if (Date.now() >= deadline) {
      return false;
    }

    await new Promise((resolve) => setTimeout(resolve, FIND_INTERVAL_MS));
  }
}
