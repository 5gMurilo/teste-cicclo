import { act } from '@testing-library/react-native';
import { addNetworkStateListener, getNetworkStateAsync } from 'expo-network';
import { fireEvent, renderRouter, screen, waitFor } from 'expo-router/testing-library';
import { AppState } from 'react-native';

import { queryClient } from '../src/shared/api/queryClient';

jest.mock('../src/shared/api/capabilities', () => ({ capabilities: { purchase: true, keyRetentionMs: null } }));
jest.mock('expo-network', () => ({ getNetworkStateAsync: jest.fn(), addNetworkStateListener: jest.fn() }));
jest.mock('expo-crypto', () => ({ randomUUID: () => '00000000-0000-4000-8000-000000000001' }));

const APP_DIR = './src/app';
const nbsp = (text: string) => text.replace(/R\$ /g, 'R$ ');

const ONLINE = { isConnected: true, isInternetReachable: true };
const OFFLINE = { isConnected: false, isInternetReachable: false };

type NetworkListener = (state: typeof ONLINE) => void;
let networkListeners: Set<NetworkListener>;
let appListeners: Set<(state: string) => void>;
let initialNetwork: Promise<typeof ONLINE>;

type Gets = { wallet: number; services: number };
let gets: Gets;
let posts: number;
let walletReply: () => Promise<Response>;
let servicesReply: () => Promise<Response>;
let postReply: () => Promise<Response>;

const respond = (status: number, body: unknown) =>
  Promise.resolve({
    status,
    ok: status >= 200 && status < 300,
    headers: { get: () => null },
    text: async () => JSON.stringify(body),
  } as unknown as Response);
const okWallet = () => respond(200, { balanceCents: 5000, currency: 'BRL', updatedAt: '2026-10-21T12:00:00Z' });
const okServices = () =>
  respond(200, {
    services: [
      { code: 'wash', name: 'Lavagem', machineType: 'washer', unitPriceCents: 1890, currency: 'BRL', isActive: true, canBeBooked: true, unavailabilityReason: null },
    ],
  });
const networkDown = () => Promise.reject(new TypeError('Network request failed'));

beforeEach(() => {
  process.env.EXPO_PUBLIC_API_URL = 'http://api.test:5001';
  networkListeners = new Set();
  appListeners = new Set();
  initialNetwork = Promise.resolve(ONLINE);
  gets = { wallet: 0, services: 0 };
  posts = 0;
  walletReply = okWallet;
  servicesReply = okServices;
  postReply = networkDown;
  jest.mocked(getNetworkStateAsync).mockImplementation(() => initialNetwork as never);
  jest.mocked(addNetworkStateListener).mockImplementation(((listener: NetworkListener) => {
    networkListeners.add(listener);
    return { remove: () => networkListeners.delete(listener) };
  }) as never);
  jest.spyOn(AppState, 'addEventListener').mockImplementation(((_e: string, listener: (state: string) => void) => {
    appListeners.add(listener);
    return { remove: () => appListeners.delete(listener) };
  }) as never);
  globalThis.fetch = jest.fn((url: string | URL | Request, init?: RequestInit) => {
    if (init?.method === 'POST') {
      posts++;
      return postReply();
    }
    if (String(url).endsWith('/wallet')) {
      gets.wallet++;
      return walletReply();
    }
    gets.services++;
    return servicesReply();
  }) as unknown as typeof fetch;
});

afterEach(() => {
  jest.restoreAllMocks();
  queryClient.clear();
});

const emitNetwork = (state: typeof ONLINE) => act(async () => networkListeners.forEach((l) => l(state)));
const emitApp = (state: string) => act(async () => appListeners.forEach((l) => l(state)));
const addWash = () => fireEvent.press(screen.getByRole('button', { name: /^Adicionar Lavagem/ }));

async function startHome() {
  const app = await renderRouter(APP_DIR);
  await screen.findByText('Lavagem');
  await screen.findByText(nbsp('R$ 50,00'));
  return app;
}

describe('connectivity and foreground', () => {
  it('does not authorize anything while the first network reading is pending', async () => {
    let answer: (state: typeof ONLINE) => void = () => undefined;
    initialNetwork = new Promise((resolve) => (answer = resolve));
    await renderRouter(APP_DIR);
    await screen.findByText('Lavagem');

    await addWash();
    expect(screen.getByRole('alert')).toHaveTextContent(/^Aguarde o saldo e o catálogo/);
    expect(screen.queryByRole('button', { name: 'Revisar seleção' })).toBeNull();

    await act(async () => answer(ONLINE));
    await addWash();
    expect(screen.getByText(`Selecionado: ${nbsp('R$ 18,90')}`)).toBeOnTheScreen();
  });

  it('blocks additions and labels the balance as the last update while offline', async () => {
    await startHome();

    await emitNetwork(OFFLINE);

    expect(await screen.findByText(/^Desatualizado · atualizado às /)).toBeOnTheScreen();
    await addWash();
    expect(screen.getByRole('alert')).toHaveTextContent(/^Aguarde o saldo e o catálogo/);
  });

  it('on reconnection re-reads wallet and catalog and keeps additions blocked until both succeed', async () => {
    await startHome();
    await emitNetwork(OFFLINE);
    const before = { ...gets };
    let release: () => void = () => undefined;
    const held = new Promise<void>((resolve) => (release = resolve));
    walletReply = () => held.then(okWallet);
    servicesReply = () => held.then(okServices);

    await emitNetwork(ONLINE);
    await addWash();
    expect(screen.getByRole('alert')).toHaveTextContent(/^Aguarde o saldo e o catálogo/);
    expect(gets.wallet).toBe(before.wallet + 1);
    expect(gets.services).toBe(before.services + 1);

    release();
    await waitFor(() => expect(screen.queryByText(/^Desatualizado/)).toBeNull());
    await addWash();
    expect(screen.getByText(`Selecionado: ${nbsp('R$ 18,90')}`)).toBeOnTheScreen();
    expect(posts).toBe(0);
  });

  it('keeps purchases blocked when the re-read fails and recovers through the header retry', async () => {
    await startHome();
    await emitNetwork(OFFLINE);
    walletReply = () => respond(404, { errorCode: 'NOT_FOUND' });

    await emitNetwork(ONLINE);
    expect(await screen.findByRole('button', { name: 'Atualizar saldo' })).toBeOnTheScreen();
    await addWash();
    expect(screen.getByRole('alert')).toHaveTextContent(/^Aguarde o saldo e o catálogo/);

    walletReply = okWallet;
    await fireEvent.press(screen.getByRole('button', { name: 'Atualizar saldo' }));
    await waitFor(() => expect(screen.queryByText(/^Desatualizado/)).toBeNull());
    await addWash();
    expect(screen.getByText(`Selecionado: ${nbsp('R$ 18,90')}`)).toBeOnTheScreen();
  });

  it('re-reads by REST when the app returns to the foreground, with no POST', async () => {
    await startHome();
    const before = { ...gets };

    await emitApp('background');
    expect(gets).toEqual(before);
    await emitApp('active');

    await waitFor(() => expect(gets.wallet).toBe(before.wallet + 1));
    expect(gets.services).toBe(before.services + 1);
    expect(posts).toBe(0);
  });

  it('never re-sends an unknown purchase by itself; only the explicit verification does', async () => {
    postReply = networkDown;
    await startHome();
    await addWash();
    await fireEvent.press(screen.getByRole('button', { name: 'Revisar seleção' }));
    await fireEvent.press(screen.getByRole('button', { name: /^Confirmar contratação/ }));
    expect(await screen.findByText(/Não foi possível confirmar se a contratação foi concluída/)).toBeOnTheScreen();
    expect(posts).toBe(1);

    await emitNetwork(OFFLINE);
    await emitNetwork(ONLINE);
    await emitApp('background');
    await emitApp('active');
    await waitFor(() => expect(screen.queryByText(/^Desatualizado/)).toBeNull());

    expect(posts).toBe(1);
    expect(screen.getByText(/Não foi possível confirmar se a contratação foi concluída/)).toBeOnTheScreen();

    postReply = () =>
      respond(200, {
        bookingId: '11111111-1111-4111-8111-111111111111',
        currency: 'BRL',
        chargedAmountCents: 1890,
        remainingBalanceCents: 3110,
        createdAt: '2026-10-21T12:00:00Z',
        items: [],
      });
    await fireEvent.press(screen.getByRole('button', { name: 'Verificar contratação' }));
    expect(await screen.findByText('Contratação confirmada')).toBeOnTheScreen();
    expect(posts).toBe(2);
  });
});
