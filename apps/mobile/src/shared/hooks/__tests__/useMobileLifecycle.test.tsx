import { focusManager, onlineManager } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react-native';
import { addNetworkStateListener, getNetworkStateAsync } from 'expo-network';
import { AppState } from 'react-native';

import { useMobileLifecycle } from '../useMobileLifecycle';

jest.mock('expo-network', () => ({ getNetworkStateAsync: jest.fn(), addNetworkStateListener: jest.fn() }));

type NetworkListener = (state: { isConnected?: boolean; isInternetReachable?: boolean }) => void;
type AppListener = (state: string) => void;

let networkListeners: Set<NetworkListener>;
let appListeners: Set<AppListener>;
let removed: { network: number; app: number };
let initial: Promise<{ isConnected?: boolean; isInternetReachable?: boolean } | undefined>;

beforeEach(() => {
  networkListeners = new Set();
  appListeners = new Set();
  removed = { network: 0, app: 0 };
  initial = Promise.resolve({ isConnected: true, isInternetReachable: true });
  jest.mocked(getNetworkStateAsync).mockImplementation(() => initial as never);
  jest.mocked(addNetworkStateListener).mockImplementation(((listener: NetworkListener) => {
    networkListeners.add(listener);
    return {
      remove: () => {
        removed.network++;
        networkListeners.delete(listener);
      },
    };
  }) as never);
  jest.spyOn(AppState, 'addEventListener').mockImplementation(((_event: string, listener: AppListener) => {
    appListeners.add(listener);
    return {
      remove: () => {
        removed.app++;
        appListeners.delete(listener);
      },
    };
  }) as never);
  onlineManager.setOnline(true);
  focusManager.setFocused(true);
});

afterEach(() => jest.restoreAllMocks());

const emitNetwork = (state: Parameters<NetworkListener>[0]) => networkListeners.forEach((l) => l(state));
const emitApp = (state: string) => appListeners.forEach((l) => l(state));

describe('useMobileLifecycle', () => {
  it('stays unknown, neither online nor offline, until the first reading arrives', async () => {
    let answer: (state: { isConnected: boolean; isInternetReachable: boolean }) => void = () => undefined;
    initial = new Promise((resolve) => (answer = resolve));
    const onResume = jest.fn();
    const { result } = await renderHook(() => useMobileLifecycle(onResume));

    expect(result.current.network).toBe('unknown');
    expect(onlineManager.isOnline()).toBe(true);

    await act(async () => answer({ isConnected: true, isInternetReachable: true }));
    expect(result.current.network).toBe('online');
    expect(onResume).not.toHaveBeenCalled();
  });

  it('goes offline, informs Query, and resumes exactly once when the connection returns', async () => {
    const onResume = jest.fn();
    const { result } = await renderHook(() => useMobileLifecycle(onResume));
    await act(async () => undefined);

    await act(async () => emitNetwork({ isConnected: false, isInternetReachable: false }));
    expect(result.current.network).toBe('offline');
    expect(onlineManager.isOnline()).toBe(false);
    expect(onResume).not.toHaveBeenCalled();

    await act(async () => emitNetwork({ isConnected: true, isInternetReachable: true }));
    expect(result.current.network).toBe('online');
    expect(onlineManager.isOnline()).toBe(true);
    expect(onResume).toHaveBeenCalledTimes(1);
  });

  it('treats an unreachable internet as offline even when connected to a network', async () => {
    const { result } = await renderHook(() => useMobileLifecycle(jest.fn()));
    await act(async () => undefined);

    await act(async () => emitNetwork({ isConnected: true, isInternetReachable: false }));

    expect(result.current.network).toBe('offline');
  });

  it('resumes only when returning from background, and keeps Query focus in sync', async () => {
    const onResume = jest.fn();
    await renderHook(() => useMobileLifecycle(onResume));
    await act(async () => undefined);

    await act(async () => emitApp('active'));
    expect(onResume).not.toHaveBeenCalled();

    await act(async () => emitApp('background'));
    expect(focusManager.isFocused()).toBe(false);
    expect(onResume).not.toHaveBeenCalled();

    await act(async () => emitApp('active'));
    expect(focusManager.isFocused()).toBe(true);
    expect(onResume).toHaveBeenCalledTimes(1);
  });

  it('subscribes once, survives rerenders without duplicating, and cleans up on unmount', async () => {
    const onResume = jest.fn();
    const { rerender, unmount } = await renderHook(() => useMobileLifecycle(onResume));
    await rerender(undefined);
    await rerender(undefined);

    expect(networkListeners.size).toBe(1);
    expect(appListeners.size).toBe(1);

    await unmount();
    expect(removed).toEqual({ network: 1, app: 1 });
    expect(networkListeners.size).toBe(0);
    expect(appListeners.size).toBe(0);
  });

  it('ignores a network reading that arrives after unmount', async () => {
    let answer: (state: { isConnected: boolean; isInternetReachable: boolean }) => void = () => undefined;
    initial = new Promise((resolve) => (answer = resolve));
    const onResume = jest.fn();
    const { unmount } = await renderHook(() => useMobileLifecycle(onResume));

    await unmount();
    await act(async () => answer({ isConnected: false, isInternetReachable: false }));

    expect(onlineManager.isOnline()).toBe(true);
  });
});
