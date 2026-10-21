-- Dados iniciais da demonstração Cicclo.
-- Executado somente pelo modo --bootstrap-demo, depois das migrations, em uma única transação.
-- Não cria estrutura nem altera registros existentes: identidades estáveis com DO NOTHING.

INSERT INTO wallets (id, currency, balance, created_at, updated_at)
VALUES ('00000000-0000-4000-8000-000000000001', 'BRL', 50.00, now(), now())
ON CONFLICT (id) DO NOTHING;

INSERT INTO services (code, name, machine_type, current_price, is_active, created_at)
VALUES
    ('wash', 'Lavagem', 'washer', 18.90, true, now()),
    ('dry', 'Secagem', 'dryer', 20.90, true, now())
ON CONFLICT (code) DO NOTHING;

INSERT INTO machines (id, name, machine_type, state, updated_at)
VALUES
    ('00000000-0000-4000-8000-000000000101', 'washer-01', 'washer', 'available', now()),
    ('00000000-0000-4000-8000-000000000102', 'dryer-01', 'dryer', 'available', now())
ON CONFLICT (id) DO NOTHING;
