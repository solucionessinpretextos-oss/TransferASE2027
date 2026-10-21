import {
  createContext,
  ReactNode,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState
} from "react";
import {
  createSignedLoginRequest,
  EfirmaCredentials,
  LoginChallenge
} from "./efirma";

const SESSION_KEY = "filexfer.admin.session.v1";
const DEVICE_KEY = "filexfer.admin.device-id.v1";
const LEGACY_ACCESS_TOKEN_KEY = "filexfer.admin.access-token";
const PRIVILEGED_ROLES = new Set(["admin", "ops"]);

type AuthStatus = "bootstrapping" | "anonymous" | "authenticated";

export interface AuthSession {
  accessToken: string;
  refreshExpiresUtc: string;
  rfc: string;
  roles: string[];
  deviceId: string;
}

interface LoginResponse {
  token?: string;
  refreshToken?: string;
  refreshExpiresUtc?: string;
  rfc?: string;
  roles?: string[];
  deviceId?: string;
}

interface SessionResponse {
  rfc: string;
  roles: string[];
  deviceId?: string;
  authenticationMethod: string;
}

export type LoginProgress = (percentage: number, action: string) => void;

interface AuthContextValue {
  status: AuthStatus;
  session: AuthSession | null;
  login: (credentials: EfirmaCredentials, progress?: LoginProgress) => Promise<void>;
  logout: () => Promise<void>;
  authorizedFetch: (path: string, init?: RequestInit) => Promise<Response>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

function apiBaseUrl(): string {
  const configured = import.meta.env.VITE_API_BASE_URL as string | undefined;
  return configured ? configured.replace(/\/+$/, "") : window.location.origin;
}

async function problemMessage(response: Response, fallback: string): Promise<string> {
  const text = await response.text();
  if (!text) return fallback;
  try {
    const body = JSON.parse(text) as { title?: string; detail?: string; message?: string };
    return body.title ?? body.detail ?? body.message ?? fallback;
  } catch {
    return text;
  }
}

function hasPrivilegedRole(roles: string[] | undefined): boolean {
  return (roles ?? []).some((role) => PRIVILEGED_ROLES.has(role.toLowerCase()));
}

function getOrCreateDeviceId(): string {
  const existing = window.localStorage.getItem(DEVICE_KEY);
  if (existing && /^[A-Za-z0-9._:-]{1,64}$/.test(existing)) return existing;
  const created = `dashboard-${crypto.randomUUID()}`;
  window.localStorage.setItem(DEVICE_KEY, created);
  return created;
}

function readStoredSession(): AuthSession | null {
  const raw = window.sessionStorage.getItem(SESSION_KEY);
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as AuthSession;
    if (
      !parsed.accessToken ||
      !parsed.rfc ||
      !Array.isArray(parsed.roles) ||
      !hasPrivilegedRole(parsed.roles)
    ) {
      return null;
    }
    return parsed;
  } catch {
    return null;
  }
}

function normalizeLoginResponse(response: LoginResponse): AuthSession {
  if (
    !response.token ||
    !response.refreshExpiresUtc ||
    !response.rfc ||
    !response.deviceId ||
    !hasPrivilegedRole(response.roles)
  ) {
    throw new Error("La e.firma es válida, pero la cuenta no tiene rol admin u ops.");
  }

  return {
    accessToken: response.token,
    refreshExpiresUtc: response.refreshExpiresUtc,
    rfc: response.rfc,
    roles: response.roles ?? [],
    deviceId: response.deviceId
  };
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>("bootstrapping");
  const [session, setSession] = useState<AuthSession | null>(null);
  const sessionRef = useRef<AuthSession | null>(null);
  const refreshInFlight = useRef<Promise<AuthSession | null> | null>(null);

  const commitSession = useCallback((next: AuthSession | null) => {
    sessionRef.current = next;
    setSession(next);
    if (next) {
      window.sessionStorage.setItem(SESSION_KEY, JSON.stringify(next));
      setStatus("authenticated");
    } else {
      window.sessionStorage.removeItem(SESSION_KEY);
      window.sessionStorage.removeItem(LEGACY_ACCESS_TOKEN_KEY);
      setStatus("anonymous");
    }
  }, []);

  const requestWithToken = useCallback(
    (token: string, path: string, init?: RequestInit): Promise<Response> => {
      const headers = new Headers(init?.headers);
      headers.set("Authorization", `Bearer ${token}`);
      return fetch(`${apiBaseUrl()}${path}`, { ...init, headers });
    },
    []
  );

  const renewSession = useCallback(async (): Promise<AuthSession | null> => {
    if (refreshInFlight.current) return refreshInFlight.current;

    const current = sessionRef.current;
    if (!current) {
      commitSession(null);
      return null;
    }

    refreshInFlight.current = (async () => {
      try {
        const response = await fetch(`${apiBaseUrl()}/auth/refresh`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: "{}"
        });
        if (!response.ok) {
          commitSession(null);
          return null;
        }
        const renewed = normalizeLoginResponse((await response.json()) as LoginResponse);
        commitSession(renewed);
        return renewed;
      } catch {
        commitSession(null);
        return null;
      } finally {
        refreshInFlight.current = null;
      }
    })();

    return refreshInFlight.current;
  }, [commitSession]);

  const authorizedFetch = useCallback(
    async (path: string, init?: RequestInit): Promise<Response> => {
      const current = sessionRef.current;
      if (!current) throw new Error("La sesión administrativa no está activa.");

      let response = await requestWithToken(current.accessToken, path, init);
      if (response.status !== 401) return response;

      const renewed = await renewSession();
      if (!renewed) return response;
      response = await requestWithToken(renewed.accessToken, path, init);
      return response;
    },
    [renewSession, requestWithToken]
  );

  const login = useCallback(
    async (credentials: EfirmaCredentials, progress?: LoginProgress): Promise<void> => {
      progress?.(10, "Solicitando reto de seguridad al servidor...");
      const nonceResponse = await fetch(`${apiBaseUrl()}/auth/nonce`, { method: "POST" });
      if (!nonceResponse.ok) {
        throw new Error(await problemMessage(nonceResponse, "No fue posible solicitar el reto de seguridad."));
      }
      const challenge = (await nonceResponse.json()) as LoginChallenge;
      if (!challenge.nonceId || !challenge.nonce) {
        throw new Error("El servidor devolvió un reto de seguridad incompleto.");
      }

      progress?.(35, "Abriendo la e.firma y comprobando CER, KEY y contraseña...");
      await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
      const signedRequest = await createSignedLoginRequest(
        credentials,
        challenge,
        getOrCreateDeviceId()
      );

      progress?.(70, "Validando firma, vigencia y permisos administrativos...");
      const response = await fetch(`${apiBaseUrl()}/auth/login-signed`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(signedRequest)
      });
      if (!response.ok) {
        throw new Error(await problemMessage(response, "No fue posible iniciar sesión con la e.firma."));
      }

      const candidate = normalizeLoginResponse((await response.json()) as LoginResponse);
      const validation = await requestWithToken(candidate.accessToken, "/auth/session");
      if (!validation.ok) {
        throw new Error(await problemMessage(validation, "La sesión administrativa no pudo validarse."));
      }
      const identity = (await validation.json()) as SessionResponse;
      if (identity.authenticationMethod !== "efirma" || !hasPrivilegedRole(identity.roles)) {
        throw new Error("La sesión no acredita e.firma y permisos administrativos.");
      }

      progress?.(100, "Acceso administrativo autorizado.");
      commitSession({ ...candidate, rfc: identity.rfc, roles: identity.roles });
    },
    [commitSession, requestWithToken]
  );

  const logout = useCallback(async (): Promise<void> => {
    const current = sessionRef.current;
    commitSession(null);
    if (!current) return;
    try {
      await fetch(`${apiBaseUrl()}/auth/logout`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: "{}"
      });
    } catch {
      // El cierre local es inmediato; el servidor expirará el token si no hay conectividad.
    }
  }, [commitSession]);

  useEffect(() => {
    let disposed = false;
    const bootstrap = async () => {
      window.sessionStorage.removeItem(LEGACY_ACCESS_TOKEN_KEY);
      const stored = readStoredSession();
      if (!stored) {
        if (!disposed) commitSession(null);
        return;
      }

      sessionRef.current = stored;
      try {
        const response = await requestWithToken(stored.accessToken, "/auth/session");
        if (response.ok) {
          const identity = (await response.json()) as SessionResponse;
          if (
            identity.authenticationMethod === "efirma" &&
            hasPrivilegedRole(identity.roles) &&
            !disposed
          ) {
            commitSession({ ...stored, rfc: identity.rfc, roles: identity.roles });
            return;
          }
        }

        if (!disposed) await renewSession();
      } catch {
        if (!disposed) commitSession(null);
      }
    };

    void bootstrap();
    return () => {
      disposed = true;
    };
  }, [commitSession, renewSession, requestWithToken]);

  const value = useMemo<AuthContextValue>(
    () => ({ status, session, login, logout, authorizedFetch }),
    [status, session, login, logout, authorizedFetch]
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth debe ejecutarse dentro de AuthProvider.");
  return value;
}
