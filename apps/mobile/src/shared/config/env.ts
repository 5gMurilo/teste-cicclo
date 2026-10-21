export class ConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ConfigError';
  }
}

export type PublicEnv = {
  apiOrigin: string;
  displayName: string;
};

type RawPublicEnv = {
  apiUrl?: string;
  displayName?: string;
};

const FALLBACK_DISPLAY_NAME = 'Visitante';

function parseApiOrigin(value: string | undefined): string {
  const raw = value?.trim();
  if (!raw) {
    throw new ConfigError('EXPO_PUBLIC_API_URL is not set');
  }

  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    throw new ConfigError('EXPO_PUBLIC_API_URL is not a valid URL');
  }

  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw new ConfigError('EXPO_PUBLIC_API_URL must use http or https');
  }
  if (url.username || url.password) {
    throw new ConfigError('EXPO_PUBLIC_API_URL must not contain credentials');
  }
  if (url.pathname !== '/' || url.search || url.hash || /[?#]/.test(raw)) {
    throw new ConfigError('EXPO_PUBLIC_API_URL must be an origin without path, query or hash');
  }

  return url.origin;
}

function parseDisplayName(value: string | undefined): string {
  return value?.trim() || FALLBACK_DISPLAY_NAME;
}

export function parsePublicEnv(raw: RawPublicEnv): PublicEnv {
  return {
    apiOrigin: parseApiOrigin(raw.apiUrl),
    displayName: parseDisplayName(raw.displayName),
  };
}

// Expo inlines EXPO_PUBLIC_* only for static `process.env.NAME` accesses.
export function readPublicEnv(): PublicEnv {
  return parsePublicEnv({
    apiUrl: process.env.EXPO_PUBLIC_API_URL,
    displayName: process.env.EXPO_PUBLIC_DEMO_DISPLAY_NAME,
  });
}

/** The greeting must render even when the API URL is misconfigured, so it is read on its own. */
export function readDisplayName(): string {
  return parseDisplayName(process.env.EXPO_PUBLIC_DEMO_DISPLAY_NAME);
}
