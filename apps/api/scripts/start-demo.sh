#!/usr/bin/env bash
# Sobe a demonstração local do backend: PostgreSQL, bootstrap one-shot (migrations e seed.sql) e API.
# Nunca apaga volumes, nunca instala ferramentas e nunca executa o .env como código: a configuração é lida pelo Compose.
#
# Variáveis opcionais:
#   CICCLO_ENV_FILE        arquivo de ambiente do Compose (padrão: apps/api/.env)
#   CICCLO_READY_TIMEOUT   segundos de espera por /health/ready (padrão: 60)
#   COMPOSE_PROJECT_NAME   projeto Compose (padrão: cicclo, definido no docker-compose.yml)
#   CICCLO_CACHE_ENABLED   true sobe o Redis opcional (perfil cache) e liga Caching__Enabled na API (padrão: false).
#                          Lida só do ambiente de quem chama; o valor do .env é ignorado por este script.
#
# Códigos de saída: 0 sucesso; 1 falha de execução (Compose, bootstrap, readiness ou smoke); 2 pré-requisito ou configuração ausente.
set -euo pipefail

api_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
env_file="${CICCLO_ENV_FILE:-$api_dir/.env}"
ready_timeout="${CICCLO_READY_TIMEOUT:-60}"
cache_enabled="${CICCLO_CACHE_ENABLED:-false}"

step() { printf '==> %s\n' "$*"; }
fail() { printf 'ERRO: %s\n' "$2" >&2; exit "$1"; }

for tool in docker curl; do
  command -v "$tool" >/dev/null 2>&1 || fail 2 "pré-requisito ausente: '$tool' não encontrado no PATH. Instale-o e execute novamente."
done
docker compose version >/dev/null 2>&1 || fail 2 "pré-requisito ausente: Docker Compose v2 ('docker compose') indisponível."
docker info >/dev/null 2>&1 || fail 2 "Docker daemon inacessível. Inicie o Docker e execute novamente."
[[ -f "$env_file" ]] || fail 2 "arquivo de ambiente '$env_file' não encontrado. Copie apps/api/.env.example para apps/api/.env e defina POSTGRES_PASSWORD."
[[ "$ready_timeout" =~ ^[1-9][0-9]*$ ]] || fail 2 "CICCLO_READY_TIMEOUT deve ser um inteiro positivo de segundos."
[[ "$cache_enabled" == true || "$cache_enabled" == false ]] || fail 2 "CICCLO_CACHE_ENABLED deve ser true ou false."
# Exportado, prevalece sobre o .env na interpolação do Compose: o cache só liga por pedido explícito nesta chamada.
export CICCLO_CACHE_ENABLED="$cache_enabled"

compose() { docker compose --project-directory "$api_dir" --env-file "$env_file" "$@"; }

# Valida a interpolação (por exemplo POSTGRES_PASSWORD obrigatório) sem imprimir a configuração resolvida.
compose config --quiet || fail 2 "configuração do Compose inválida; confira as variáveis em '$env_file'."

step "Subindo PostgreSQL e aguardando o healthcheck"
compose up --detach --wait postgres || fail 1 "PostgreSQL não ficou saudável."

step "Construindo a imagem da API"
compose build api || fail 1 "falha ao construir a imagem da API."

step "Executando bootstrap one-shot (migrations e depois seed.sql)"
if ! compose run --rm --no-TTY bootstrap; then
  fail 1 "bootstrap falhou; a API não foi iniciada e nenhum dado foi apagado. Veja a saída acima."
fi

if [[ "$cache_enabled" == true ]]; then
  step "Subindo o Redis opcional (perfil cache)"
  # Redis não é pré-requisito: se não ficar saudável, a API sobe igual e lê o saldo do PostgreSQL.
  if ! compose --profile cache up --detach --wait redis; then
    printf 'AVISO: %s\n' "Redis não ficou saudável; a API sobe sem cache efetivo e lê o saldo do PostgreSQL. Confira REDIS_PASSWORD em '$env_file'." >&2
  fi
fi

step "Iniciando a API"
compose up --detach api || fail 1 "falha ao iniciar a API."
address="$(compose port api 8080)" || fail 1 "porta publicada da API não encontrada."
base_url="http://$address"

step "Aguardando $base_url/health/ready (até ${ready_timeout}s)"
deadline=$((SECONDS + ready_timeout))
until curl --silent --fail --max-time 2 --output /dev/null "$base_url/health/ready"; do
  if (( SECONDS >= deadline )); then
    fail 1 "a API não ficou pronta em ${ready_timeout}s. Diagnóstico: docker compose --project-directory '$api_dir' --env-file '$env_file' logs api"
  fi
  sleep 1
done

step "Smoke das consultas"
for check in '/api/v1/wallet "balanceCents"' '/api/v1/services "unitPriceCents"'; do
  path="${check%% *}"
  field="${check#* }"
  body="$(curl --silent --show-error --fail --max-time 5 "$base_url$path")" || fail 1 "GET $path falhou."
  [[ "$body" == *"$field"* ]] || fail 1 "GET $path respondeu sem o campo $field."
  printf '%s %s\n' "$path" "$body"
done

if [[ "$cache_enabled" == true ]]; then cache_state="ligado"; else cache_state="desligado"; fi
step "Demo pronta em $base_url (wallet, services, /health/ready e /openapi/v1.json; cache Redis $cache_state)"
