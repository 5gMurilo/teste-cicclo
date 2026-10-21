import { StyleSheet, Text, View } from 'react-native';

type Props = {
  title: string;
  message: string;
};

// Temporary honest state until each tab gets its domain screen; the shared header owns the top inset.
export function PlaceholderScreen({ title, message }: Props) {
  return (
    <View style={styles.container}>
      <Text accessibilityRole="header" style={styles.title}>
        {title}
      </Text>
      <Text style={styles.message}>{message}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, padding: 16, gap: 8 },
  title: { fontSize: 22, fontWeight: '600' },
  message: { fontSize: 16 },
});
