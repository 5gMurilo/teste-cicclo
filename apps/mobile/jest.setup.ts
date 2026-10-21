// Default for every test: the device is online. Lifecycle tests replace this mock with a controllable one.
jest.mock('expo-network', () => ({
  getNetworkStateAsync: async () => ({ isConnected: true, isInternetReachable: true }),
  addNetworkStateListener: () => ({ remove: () => undefined }),
}));

// Icon fonts cannot load under Jest: render the icon name as text so tests can tell which icon a tab uses.
jest.mock('@expo/vector-icons/Ionicons', () => {
  const { createElement } = jest.requireActual('react');
  const { Text } = jest.requireActual('react-native');
  return { __esModule: true, default: ({ name }: { name: string }) => createElement(Text, null, name) };
});
