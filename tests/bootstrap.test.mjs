import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import test from 'node:test';
import { parse } from 'yaml';

const text = (path) => readFileSync(path, 'utf8');
const json = (path) => JSON.parse(text(path));
const exact = /^\d+\.\d+\.\d+$/;

test('workspace graph contains exactly the two applications', () => {
  const graph = JSON.parse(execFileSync('pnpm', ['exec', 'turbo', 'run', 'build', '--dry=json'], { encoding: 'utf8' }));
  assert.deepEqual(graph.packages.sort(), ['@cicclo/api', '@cicclo/mobile']);
  assert.deepEqual(parse(text('pnpm-workspace.yaml')).packages, ['apps/*', 'packages/*']);
  for (const app of ['api', 'mobile']) assert.equal(json(`apps/${app}/package.json`).private, true);
});

test('pinned toolchain has one workspace lockfile', () => {
  const root = json('package.json');
  assert.match(root.packageManager, /^pnpm@\d+\.\d+\.\d+$/);
  assert.match(text('.nvmrc').trim(), /^22\.\d+\.\d+$/);
  assert.match(root.devDependencies.turbo, exact);
  assert.equal(existsSync('pnpm-lock.yaml'), true);
  for (const path of ['.', 'apps/api', 'apps/mobile']) {
    assert.equal(existsSync(`${path}/package-lock.json`), false);
    assert.equal(existsSync(`${path}/yarn.lock`), false);
    if (path !== '.') assert.equal(existsSync(`${path}/pnpm-lock.yaml`), false);
  }
});

test('persistent development tasks are never cached', () => {
  const task = json('turbo.json').tasks.dev;
  assert.equal(task.persistent, true);
  assert.equal(task.cache, false);
  assert.equal(json('package.json').scripts.dev, 'turbo run dev');
});

test('stable mobile versions follow the Expo matrix', () => {
  const mobile = json('apps/mobile/package.json');
  assert.match(mobile.dependencies.expo, /^58\.0\.\d+$/);
  assert.equal(mobile.dependencies['react-native'], '0.88.0');
  assert.equal(mobile.dependencies.react, '19.3.0');
  assert.match(mobile.devDependencies.typescript, exact);
  assert.equal(json('apps/mobile/tsconfig.json').compilerOptions.strict, true);
  for (const version of Object.values({ ...mobile.dependencies, ...mobile.devDependencies })) assert.match(version, exact);
});

test('pinned dotnet targets net10 without preview selection', () => {
  const sdk = json('global.json').sdk;
  assert.match(sdk.version, /^10\.0\.\d+$/);
  assert.equal(sdk.allowPrerelease, false);
  assert.match(text('apps/api/src/Cicclo.Api.csproj'), /<TargetFramework>net10\.0<\/TargetFramework>/);
});

test('build artifacts include Android iOS and API Release', () => {
  const metadata = json('apps/mobile/dist/metadata.json');
  for (const platform of ['android', 'ios']) {
    const bundle = metadata.fileMetadata[platform].bundle;
    assert.ok(bundle);
    assert.equal(existsSync(`apps/mobile/dist/${bundle}`), true);
  }
  assert.equal(existsSync('apps/api/src/bin/Release/net10.0/Cicclo.Api.dll'), true);
});

test('dotnet cache disabled for every API task', () => {
  const config = json('apps/api/turbo.json');
  assert.deepEqual(config.extends, ['//']);
  for (const task of ['dev', 'build', 'lint', 'test']) assert.equal(config.tasks[task].cache, false);
});

test('onboarding documentation covers the local workflow', () => {
  const readme = text('README.md');
  for (const term of ['Pré-requisitos', 'Node', 'pnpm', '.NET', 'NuGet', 'Android', 'iOS', 'APK', 'IPA', 'Docker', 'PostgreSQL', 'seed.sql', 'volume']) {
    assert.ok(readme.includes(term), `README missing ${term}`);
  }
  for (const command of ['pnpm install --frozen-lockfile', 'dotnet restore', 'pnpm dev', 'pnpm build', 'pnpm lint', 'pnpm typecheck', 'pnpm test', '--filter @cicclo/mobile', '--filter @cicclo/api']) {
    assert.ok(readme.includes(command), `README missing ${command}`);
  }
});

test('backend guide documents the verified demo, explicit reset and real limits', () => {
  const readme = text('README.md');
  for (const term of ['Início rápido do backend', 'Roteiro de demonstração', 'Reset da demonstração', '--reset-demo --confirm-reset', 'Modelo de ameaça', 'Limitações conhecidas', 'Uso de IA', 'Redis', 'SignalR']) {
    assert.ok(readme.includes(term), `README missing ${term}`);
  }
  assert.doesNotMatch(readme, /smoke\.mjs/);
  const referenced = [...readme.matchAll(/`((?:apps|tests)\/[^`\s]+\.(?:sh|cs|sql|yml|json|csproj|slnx|mjs))`/g)].map((match) => match[1]);
  assert.ok(referenced.includes('apps/api/scripts/start-demo.sh'));
  for (const path of referenced) assert.equal(existsSync(path), true, `README references missing ${path}`);
});

test('git hygiene excludes generated files and local secrets', () => {
  const paths = ['node_modules/example', '.turbo/cache/example', 'apps/mobile/.expo/example', 'apps/mobile/dist/example', 'apps/mobile/ios/example', 'apps/mobile/android/example', 'apps/api/src/bin/example', 'apps/api/src/obj/example', 'apps/api/.env'];
  for (const path of paths) {
    execFileSync('git', ['check-ignore', '--no-index', path]);
  }
  assert.equal(execFileSync('git', ['ls-files', 'untitled.md'], { encoding: 'utf8' }).trim(), '');
  // The pre-existing empty file disappeared during planning; never recreate it.
  if (existsSync('untitled.md')) assert.equal(text('untitled.md'), '');
  assert.equal(existsSync('apps/api/.env.example'), true);
});

test('container configuration uses pinned stages and non-root runtime', () => {
  const dockerfile = text('apps/api/Dockerfile');
  assert.match(dockerfile, /FROM mcr\.microsoft\.com\/dotnet\/sdk:10\.0\.402 AS build/);
  assert.match(dockerfile, /FROM mcr\.microsoft\.com\/dotnet\/aspnet:10\.0\.13 AS runtime/);
  assert.match(dockerfile, /COPY --from=build/);
  assert.match(dockerfile, /USER \$APP_UID/);
  assert.match(dockerfile, /EXPOSE 8080/);
  assert.match(dockerfile, /ENTRYPOINT \["dotnet", "Cicclo.Api.dll"\]/);
});

test('postgres configuration persists data and leaves the seed to the post-migration bootstrap', () => {
  const compose = parse(text('apps/api/docker-compose.yml'));
  const postgres = compose.services.postgres;
  assert.equal(postgres.image, 'postgres:18.1');
  assert.match(postgres.ports[0], /^127\.0\.0\.1:/);
  assert.ok(postgres.volumes.includes('postgres-data:/var/lib/postgresql'));
  assert.ok(postgres.volumes.every((volume) => !volume.includes('docker-entrypoint-initdb.d')));
  assert.match(postgres.environment.POSTGRES_PASSWORD, /\$\{POSTGRES_PASSWORD:\?/);
  assert.ok(postgres.healthcheck.test.join(' ').includes('pg_isready'));
  assert.ok(Object.hasOwn(compose.volumes, 'postgres-data'));
  const seed = text('apps/api/init/seed.sql');
  assert.ok(seed.length > 0);
  assert.doesNotMatch(seed, /\b(CREATE|ALTER|DROP|TRUNCATE|DELETE|UPDATE)\b/i);
  assert.equal(seed.match(/ON CONFLICT \(\w+\) DO NOTHING;/g)?.length, 3);
  assert.deepEqual(readdirSync('apps/api/init'), ['seed.sql']);
  assert.match(text('apps/api/src/Cicclo.Api.csproj'), /<Content Include="\.\.\/init\/seed\.sql" Link="Seed\/seed\.sql"/);
  assert.doesNotMatch(text('apps/api/.dockerignore'), /^init$/m);
  assert.match(text('apps/api/Dockerfile'), /COPY init\/ \.\/init\//);
});

test('local demo services stay on loopback and receive the password only as data', () => {
  const services = parse(text('apps/api/docker-compose.yml')).services;
  assert.deepEqual(services.bootstrap.command, ['--bootstrap-demo']);
  assert.equal(services.bootstrap.image, services.api.image);
  for (const name of ['bootstrap', 'api']) {
    const service = services[name];
    assert.deepEqual(service.profiles, ['app']);
    assert.equal(service.depends_on.postgres.condition, 'service_healthy');
    assert.doesNotMatch(service.environment.ConnectionStrings__Cicclo, /password/i);
    assert.match(service.environment.PGPASSWORD, /^\$\{POSTGRES_PASSWORD:\?/);
  }
  assert.ok(services.api.ports.every((port) => port.startsWith('127.0.0.1:')));
  assert.ok(!Object.values(services.api.environment).some((value) => /cors/i.test(String(value))));
  const script = text('apps/api/scripts/start-demo.sh');
  assert.match(script, /run --rm --no-TTY bootstrap/);
  assert.doesNotMatch(script, /^\s*(source|\.)\s|\beval\b|down\b.*--volumes|volume rm/m);
});

test('optional redis cache stays opt-in, pinned, loopback-only and documented as eventual', () => {
  const services = parse(text('apps/api/docker-compose.yml')).services;
  const redis = services.redis;
  assert.deepEqual(redis.profiles, ['cache']);
  assert.match(redis.image, /^redis:8\.2\.10-bookworm@sha256:[0-9a-f]{64}$/);
  assert.ok(redis.ports.every((port) => port.startsWith('127.0.0.1:')));
  assert.equal(redis.volumes, undefined);
  for (const name of ['postgres', 'bootstrap', 'api']) {
    assert.equal(services[name].profiles?.includes('cache') ?? false, false);
    assert.equal(Object.hasOwn(services[name].depends_on ?? {}, 'redis'), false);
  }
  for (const name of ['bootstrap', 'api']) assert.equal(services[name].environment.Caching__Enabled, '${CICCLO_CACHE_ENABLED:-false}');
  const script = text('apps/api/scripts/start-demo.sh');
  assert.match(script, /cache_enabled="\$\{CICCLO_CACHE_ENABLED:-false\}"/);
  assert.match(script, /if \[\[ "\$cache_enabled" == true \]\]; then\n\s+step "Subindo o Redis opcional/);
  const readme = text('README.md');
  for (const term of ['Cache Redis opcional', 'CICCLO_CACHE_ENABLED=true apps/api/scripts/start-demo.sh', 'wallet:demo:v1', 'read-your-writes', 'TTL', 'cicclo_cache_operations_total']) {
    assert.ok(readme.includes(term), `README missing ${term}`);
  }
  assert.ok(readme.includes('sem consistência forte'));
  assert.doesNotMatch(readme, /(garante|oferece|com) consistência forte/);
});
