import { act, render, screen } from '@testing-library/react-native';
import { AppState } from 'react-native';

import { OfferCountdown, secondsUntil } from '../OfferCountdown';

const T0 = Date.parse('2026-10-21T12:00:00Z');
const at = (offsetSeconds: number) => new Date(T0 + offsetSeconds * 1000).toISOString();

let appListeners: Set<(state: string) => void>;
let removed: number;

beforeEach(() => {
  jest.useFakeTimers({ now: T0 });
  appListeners = new Set();
  removed = 0;
  jest.spyOn(AppState, 'addEventListener').mockImplementation(((_e: string, listener: (state: string) => void) => {
    appListeners.add(listener);
    return { remove: () => (removed++, appListeners.delete(listener)) };
  }) as never);
});

afterEach(() => {
  jest.useRealTimers();
  jest.restoreAllMocks();
});

const tick = (ms: number) => act(async () => jest.advanceTimersByTimeAsync(ms));

describe('secondsUntil', () => {
  it('rounds up the remaining time and never goes below zero', () => {
    expect(secondsUntil(at(60), T0)).toBe(60);
    expect(secondsUntil(at(0.2), T0)).toBe(1);
    expect(secondsUntil(at(0), T0)).toBe(0);
    expect(secondsUntil(at(-30), T0)).toBe(0);
  });
});

describe('OfferCountdown', () => {
  it('counts down from the server deadline: 60, 1 and zero', async () => {
    const onExpired = jest.fn();
    await render(<OfferCountdown expiresAt={at(60)} onExpired={onExpired} />);
    expect(screen.getByText('Tempo restante: 60 s')).toBeOnTheScreen();

    await tick(59_000);
    expect(screen.getByText('Tempo restante: 1 s')).toBeOnTheScreen();
    expect(onExpired).not.toHaveBeenCalled();

    await tick(1000);
    expect(screen.getByText('Oferta vencida')).toBeOnTheScreen();
    expect(onExpired).toHaveBeenCalledTimes(1);
  });

  it('treats an already expired timestamp as expired at once and reports it a single time', async () => {
    const onExpired = jest.fn();
    const view = await render(<OfferCountdown expiresAt={at(-5)} onExpired={onExpired} />);

    expect(screen.getByText('Oferta vencida')).toBeOnTheScreen();
    await view.rerender(<OfferCountdown expiresAt={at(-5)} onExpired={onExpired} />);
    await tick(5000);

    expect(onExpired).toHaveBeenCalledTimes(1);
  });

  it('recalculates from timestamps when the app returns to the foreground', async () => {
    const onExpired = jest.fn();
    await render(<OfferCountdown expiresAt={at(60)} onExpired={onExpired} />);

    // The clock moves while the app is suspended and no timer fires.
    jest.setSystemTime(T0 + 45_000);
    await act(async () => appListeners.forEach((listener) => listener('active')));
    expect(screen.getByText('Tempo restante: 15 s')).toBeOnTheScreen();

    jest.setSystemTime(T0 + 90_000);
    await act(async () => appListeners.forEach((listener) => listener('active')));
    expect(screen.getByText('Oferta vencida')).toBeOnTheScreen();
    expect(onExpired).toHaveBeenCalledTimes(1);
  });

  it('keeps a fixed spoken label instead of announcing every second, then says it expired', async () => {
    await render(<OfferCountdown expiresAt={at(60)} onExpired={jest.fn()} />);
    const spoken = screen.getByRole('timer').props.accessibilityLabel as string;
    expect(spoken).toMatch(/^Oferta de máquina válida até /);

    await tick(30_000);
    expect(screen.getByRole('timer').props.accessibilityLabel).toBe(spoken);

    await tick(30_000);
    expect(screen.getByRole('timer').props.accessibilityLabel).toBe('Oferta de máquina vencida');
  });

  it('removes its timer and its app-state listener on unmount, and reports a new deadline again', async () => {
    const onExpired = jest.fn();
    const started = jest.spyOn(globalThis, 'setInterval');
    const cleared = jest.spyOn(globalThis, 'clearInterval');
    const view = await render(<OfferCountdown expiresAt={at(2)} onExpired={onExpired} />);
    expect(started).toHaveBeenCalled();
    await tick(2000);
    expect(onExpired).toHaveBeenCalledTimes(1);

    await view.rerender(<OfferCountdown expiresAt={at(62)} onExpired={onExpired} />);
    expect(screen.getByText('Tempo restante: 60 s')).toBeOnTheScreen();
    await tick(60_000);
    expect(onExpired).toHaveBeenCalledTimes(2);

    await view.unmount();
    expect(removed).toBeGreaterThanOrEqual(1);
    expect(appListeners.size).toBe(0);
    // Every interval the component started was cleared; nothing keeps ticking after unmount.
    for (const call of started.mock.results) expect(cleared).toHaveBeenCalledWith(call.value);
    await tick(10_000);
    expect(onExpired).toHaveBeenCalledTimes(2);
  });
});
