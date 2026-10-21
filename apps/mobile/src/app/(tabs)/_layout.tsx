import { useQuery } from '@tanstack/react-query';
import { Tabs } from 'expo-router';
import { View } from 'react-native';

import { toBalanceState } from '../../features/wallet/balanceState';
import { walletOptions } from '../../features/wallet/queries';
import { readDisplayName } from '../../shared/config/env';
import { AppHeader } from '../../shared/layout/AppHeader';

export const unstable_settings = { initialRouteName: 'index' };

export default function TabsLayout() {
  const wallet = useQuery(walletOptions());

  return (
    <View style={{ flex: 1 }}>
      <AppHeader
        displayName={readDisplayName()}
        balance={toBalanceState(wallet)}
        onRetry={() => void wallet.refetch()}
      />
      <Tabs screenOptions={{ headerShown: false }}>
        <Tabs.Screen name="services" options={{ title: 'Serviços' }} />
        <Tabs.Screen name="index" options={{ title: 'Home' }} />
        <Tabs.Screen name="checkout" options={{ title: 'Checkout' }} />
      </Tabs>
    </View>
  );
}
