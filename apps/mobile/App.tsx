import { StyleSheet, Text, View } from 'react-native';

export default function App() {
  return (
    <View testID="home" style={styles.container}>
      <Text accessibilityRole="header">Cicclo</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#fff',
  },
});
