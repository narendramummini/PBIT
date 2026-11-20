import {
  InteractionRequiredAuthError,
  PublicClientApplication,
  type AccountInfo,
  type IPublicClientApplication,
} from "@azure/msal-browser";

export const FABRIC_SCOPE = "https://api.fabric.microsoft.com/.default";
export const ONELAKE_SCOPE = "https://storage.azure.com/.default";
export const LAKEHOUSE_SQL_SCOPE = "https://database.windows.net//user_impersonation";

let client: IPublicClientApplication | null = null;
let clientKey = "";

function getClient(clientId: string, tenantId: string) {
  if (!clientId || !tenantId) throw new Error("Microsoft sign-in configuration is missing. Sign in again.");
  const key = `${tenantId}:${clientId}`;
  if (client && clientKey === key) return Promise.resolve(client);
  clientKey = key;
  client = new PublicClientApplication({
    auth: {
      clientId,
      authority: `https://login.microsoftonline.com/${tenantId}`,
      redirectUri: window.location.origin,
    },
    cache: { cacheLocation: "sessionStorage", storeAuthStateInCookie: false },
  });
  return client.initialize().then(() => client!);
}

export async function acquireResourceToken(
  instance: Pick<IPublicClientApplication, "acquireTokenSilent" | "acquireTokenPopup">,
  account: AccountInfo,
  scope: string,
) {
  try {
    const result = await instance.acquireTokenSilent({ scopes: [scope], account });
    if (!result.accessToken) throw new Error("Microsoft returned an empty delegated token.");
    return result.accessToken;
  } catch (error) {
    if (!(error instanceof InteractionRequiredAuthError)) throw error;
    const result = await instance.acquireTokenPopup({ scopes: [scope], account, loginHint: account.username });
    if (!result.accessToken) throw new Error("Microsoft returned an empty delegated token.");
    return result.accessToken;
  }
}

export async function acquireTokensForSignedInAccount(
  instance: Pick<IPublicClientApplication, "acquireTokenSilent" | "acquireTokenPopup">,
  account: AccountInfo,
) {
  const fabricAccessToken = await acquireResourceToken(instance, account, FABRIC_SCOPE);
  const oneLakeAccessToken = await acquireResourceToken(instance, account, ONELAKE_SCOPE);
  const lakehouseSqlAccessToken = await acquireResourceToken(instance, account, LAKEHOUSE_SQL_SCOPE);
  return { fabricAccessToken, oneLakeAccessToken, lakehouseSqlAccessToken };
}

function permissionError(error: unknown) {
  const details = error instanceof Error ? `${error.name} ${error.message}` : "";
  if (/consent|AADSTS65001|AADSTS70011|AADSTS90094|AADSTS500011|invalid_scope|interaction_required/i.test(details)) {
    return new Error("Microsoft Fabric or Azure Storage delegated consent is required for this app registration. Ask your Microsoft 365 administrator to grant the delegated permissions listed in the setup notes, then retry.");
  }
  return error instanceof Error ? error : new Error("Microsoft delegated token acquisition failed.");
}

/** Reuses the browser's existing Microsoft session. A popup is only used if SSO/silent consent cannot satisfy the request. */
export async function acquireFabricAndOneLakeTokens(
  tenantId: string,
  clientId: string,
  loginHint?: string,
) {
  try {
    const instance = await getClient(clientId, tenantId);
    let account = instance.getActiveAccount() ?? instance.getAllAccounts().find((item) => !loginHint || item.username.toLowerCase() === loginHint.toLowerCase());
    let fabricAccessToken: string;
    if (!account) {
      try {
        const result = await instance.ssoSilent({ scopes: [FABRIC_SCOPE], ...(loginHint ? { loginHint } : {}) });
        account = result.account;
        fabricAccessToken = result.accessToken;
      } catch (error) {
        if (!(error instanceof InteractionRequiredAuthError)) throw error;
        const result = await instance.acquireTokenPopup({ scopes: [FABRIC_SCOPE], ...(loginHint ? { loginHint } : {}) });
        account = result.account;
        fabricAccessToken = result.accessToken;
      }
      if (account) instance.setActiveAccount(account);
    } else {
      fabricAccessToken = await acquireResourceToken(instance, account, FABRIC_SCOPE);
    }
    if (!account || !fabricAccessToken) throw new Error("Microsoft did not return a Fabric delegated token for the signed-in account.");
    if (!fabricAccessToken) throw new Error("Microsoft did not return a Fabric delegated token for the signed-in account.");
    const oneLakeAccessToken = await acquireResourceToken(instance, account, ONELAKE_SCOPE);
    const lakehouseSqlAccessToken = await acquireResourceToken(instance, account, LAKEHOUSE_SQL_SCOPE);
    return { fabricAccessToken, oneLakeAccessToken, lakehouseSqlAccessToken };
  } catch (error) {
    throw permissionError(error);
  }
}
