import { useQuery } from '@tanstack/react-query';
import { Tabs } from 'expo-router';
import { View } from 'react-native';

import { useCheckout } from '../../features/checkout/CheckoutProvider';
import { toBalanceState } from '../../features/wallet/balanceState';
import { walletOptions } from '../../features/wallet/queries';
import { readDisplayName } from '../../shared/config/env';
import { AppHeader } from '../../shared/layout/AppHeader';

export const unstable_settings = { initialRouteName: 'index' };

const TAB_COUNT = 3;
const CHECKOUT_POSITION = 3;

export default function TabsLayout() {
  const wallet = useQuery(walletOptions());
  const { quantity } = useCheckout().derived;

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
        <Tabs.Screen
          name="checkout"
          options={{
            title: 'Checkout',
            tabBarBadge: quantity > 0 ? quantity : undefined,
            tabBarAccessibilityLabel:
              quantity > 0
                ? `Checkout, ${quantity} ${quantity === 1 ? 'item' : 'itens'}, tab, ${CHECKOUT_POSITION} of ${TAB_COUNT}`
                : undefined,
          }}
        />
      </Tabs>
    </View>
  );
}
