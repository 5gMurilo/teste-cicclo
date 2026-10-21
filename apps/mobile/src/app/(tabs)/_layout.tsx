import { useQuery } from '@tanstack/react-query';
import Ionicons from '@expo/vector-icons/Ionicons';
import { Tabs } from 'expo-router';
import { View } from 'react-native';

import { useCheckout } from '../../features/checkout/CheckoutProvider';
import { toBalanceState } from '../../features/wallet/balanceState';
import { walletOptions } from '../../features/wallet/queries';
import { readDisplayName } from '../../shared/config/env';
import { AppHeader } from '../../shared/layout/AppHeader';

export const unstable_settings = { anchor: 'index' };

const TAB_COUNT = 3;
const CHECKOUT_POSITION = 3;

export default function TabsLayout() {
  const wallet = useQuery(walletOptions());
  const checkout = useCheckout();
  const { quantity } = checkout.derived;

  return (
    <View style={{ flex: 1 }}>
      <AppHeader
        displayName={readDisplayName()}
        balance={toBalanceState({ ...wallet, retained: checkout.readsRetained })}
        onRetry={checkout.revalidate}
      />
      <Tabs screenOptions={{ headerShown: false }}>
        <Tabs.Screen
          name="services"
          options={{
            title: 'Serviços',
            tabBarIcon: ({ color, size }) => <Ionicons name="list" color={color as string} size={size} />,
          }}
        />
        <Tabs.Screen
          name="index"
          options={{
            title: 'Home',
            tabBarIcon: ({ color, size }) => <Ionicons name="home" color={color as string} size={size} />,
          }}
        />
        <Tabs.Screen
          name="checkout"
          options={{
            title: 'Checkout',
            tabBarIcon: ({ color, size }) => <Ionicons name="cart" color={color as string} size={size} />,
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
