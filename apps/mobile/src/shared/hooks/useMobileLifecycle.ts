import { focusManager, onlineManager } from '@tanstack/react-query';
import { addNetworkStateListener, getNetworkStateAsync, type NetworkState } from 'expo-network';
import { useEffect, useRef, useState } from 'react';
import { AppState } from 'react-native';

/** `unknown` is the state before the first reading: it is not offline, and it is not permission to operate. */
export type NetworkStatus = 'unknown' | 'online' | 'offline';

function toStatus(state: NetworkState | undefined): NetworkStatus {
  if (!state || state.isConnected === undefined) return 'unknown';
  return state.isConnected === false || state.isInternetReachable === false ? 'offline' : 'online';
}

/**
 * Observes app foreground and connectivity, feeds TanStack Query's focus and online managers, and calls
 * `onResume` when the app returns to the foreground or the connection comes back. It only observes:
 * what to re-read, and what stays blocked meanwhile, is the caller's decision.
 */
export function useMobileLifecycle(onResume: () => void): { network: NetworkStatus } {
  const [network, setNetwork] = useState<NetworkStatus>('unknown');
  const onResumeRef = useRef(onResume);

  useEffect(() => {
    onResumeRef.current = onResume;
  });

  useEffect(() => {
    let mounted = true;
    let previous: NetworkStatus = 'unknown';
    let wasBackgrounded = AppState.currentState === 'background';

    const applyNetwork = (next: NetworkStatus) => {
      if (!mounted) return;
      const before = previous;
      previous = next;
      if (next !== 'unknown') onlineManager.setOnline(next === 'online');
      setNetwork(next);
      if (before === 'offline' && next === 'online') onResumeRef.current();
    };

    getNetworkStateAsync().then(
      (state) => applyNetwork(toStatus(state)),
      () => applyNetwork('unknown'),
    );
    const networkSubscription = addNetworkStateListener((state) => applyNetwork(toStatus(state)));
    const appSubscription = AppState.addEventListener('change', (state) => {
      const active = state === 'active';
      focusManager.setFocused(active);
      if (active && wasBackgrounded) onResumeRef.current();
      wasBackgrounded = !active;
    });

    return () => {
      mounted = false;
      networkSubscription.remove();
      appSubscription.remove();
    };
  }, []);

  return { network };
}
