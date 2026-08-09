export const environment = {
  /** Adres backendu TimeSuggestions (profil http z launchSettings.json). */
  apiBaseUrl: 'http://localhost:5188',

  /**
   * Rejestracja aplikacji w Microsoft Entra ID (platforma SPA, klient publiczny —
   * client ID jest identyfikatorem publicznym, nie sekretem). Rejestracja jest
   * multi-tenant i obsługuje konta osobiste, więc po sklonowaniu repo działa bez zmian.
   */
  entraClientId: '3f813718-6d67-4bca-b359-6d9cb6ab0c35',
  entraAuthority: 'https://login.microsoftonline.com/common',
  entraRedirectUri: 'http://localhost:4200',
};
