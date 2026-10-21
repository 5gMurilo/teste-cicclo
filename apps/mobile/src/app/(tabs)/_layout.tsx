import { Tabs } from 'expo-router';

export const unstable_settings = { initialRouteName: 'index' };

export default function TabsLayout() {
  return (
    <Tabs screenOptions={{ headerShown: false }}>
      <Tabs.Screen name="services" options={{ title: 'Serviços' }} />
      <Tabs.Screen name="index" options={{ title: 'Home' }} />
      <Tabs.Screen name="checkout" options={{ title: 'Checkout' }} />
    </Tabs>
  );
}
