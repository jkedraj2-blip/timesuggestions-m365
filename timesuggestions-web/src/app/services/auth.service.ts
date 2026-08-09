import { Injectable } from '@angular/core';
import {
  PublicClientApplication,
  InteractionRequiredAuthError,
  AccountInfo,
} from '@azure/msal-browser';
import { environment } from '../../environments/environment';

export const GRAPH_SCOPES = ['Calendars.Read', 'Files.Read'];

@Injectable({ providedIn: 'root' })
export class AuthService {
  private msal = new PublicClientApplication({
    auth: {
      clientId: environment.entraClientId,
      authority: environment.entraAuthority,
      redirectUri: environment.entraRedirectUri,
    },
    cache: { cacheLocation: 'sessionStorage' },
  });

  private ready = false;

  async init(): Promise<void> {
    if (this.ready) return;
    await this.msal.initialize();

    const result = await this.msal.handleRedirectPromise();
    if (result?.account) {
      this.msal.setActiveAccount(result.account);
    } else {
      const [first] = this.msal.getAllAccounts();
      if (first) this.msal.setActiveAccount(first);
    }
    this.ready = true;
  }

  get account(): AccountInfo | null {
    return this.msal.getActiveAccount();
  }

  async login(): Promise<void> {
    await this.init();
    await this.msal.loginRedirect({ scopes: GRAPH_SCOPES });
  }

  async logout(): Promise<void> {
    await this.init();
    await this.msal.logoutRedirect();
  }

  async getToken(): Promise<string> {
    await this.init();
    const account = this.msal.getActiveAccount();
    if (!account) throw new Error('Brak zalogowanego konta.');

    try {
      const result = await this.msal.acquireTokenSilent({ account, scopes: GRAPH_SCOPES });
      return result.accessToken;
    } catch (error) {
      if (error instanceof InteractionRequiredAuthError) {
        await this.msal.acquireTokenRedirect({ account, scopes: GRAPH_SCOPES });
      }
      throw error;
    }
  }
}
