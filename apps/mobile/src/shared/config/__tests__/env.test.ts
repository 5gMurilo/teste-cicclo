import { ConfigError, parsePublicEnv } from '../env';

describe('parsePublicEnv', () => {
  it('accepts an http(s) origin and trims the configured name', () => {
    expect(
      parsePublicEnv({ apiUrl: 'http://192.168.0.10:5001', displayName: '  Ana  ' }),
    ).toEqual({ apiOrigin: 'http://192.168.0.10:5001', displayName: 'Ana' });
  });

  it('normalizes a bare trailing slash to the origin', () => {
    expect(parsePublicEnv({ apiUrl: 'https://api.cicclo.test/' }).apiOrigin).toBe(
      'https://api.cicclo.test',
    );
  });

  it.each([undefined, '', '   '])('falls back to Visitante when the name is %p', (name) => {
    expect(parsePublicEnv({ apiUrl: 'http://localhost:5001', displayName: name }).displayName).toBe(
      'Visitante',
    );
  });

  it.each([undefined, '', 'not a url', 'ftp://host', 'api.cicclo.test:5001'])(
    'rejects a missing or malformed API URL (%p) without a localhost fallback',
    (apiUrl) => {
      expect(() => parsePublicEnv({ apiUrl })).toThrow(ConfigError);
    },
  );

  it.each([
    'http://user:pass@host:5001',
    'http://host:5001/api/v1',
    'http://host:5001/?a=1',
    'http://host:5001/#frag',
  ])('rejects credentials, path, query or hash (%p)', (apiUrl) => {
    expect(() => parsePublicEnv({ apiUrl })).toThrow(ConfigError);
  });
});
