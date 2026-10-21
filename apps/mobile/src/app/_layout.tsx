import { QueryClientProvider } from '@tanstack/react-query';
import { Stack } from 'expo-router';
import { StatusBar } from 'expo-status-bar';

import { CheckoutProvider } from '../features/checkout/CheckoutProvider';
import { queryClient } from '../shared/api/queryClient';

export default function RootLayout() {
  return (
    <QueryClientProvider client={queryClient}>
      <CheckoutProvider>
        <StatusBar style="auto" />
        <Stack screenOptions={{ headerShown: false }} />
      </CheckoutProvider>
    </QueryClientProvider>
  );
}
