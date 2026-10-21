import { StyleSheet, Text } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

type Props = {
  title: string;
  message: string;
};

// Temporary honest state until each tab gets its domain screen; owns the top inset until the shared header does.
export function PlaceholderScreen({ title, message }: Props) {
  return (
    <SafeAreaView edges={['top']} style={styles.container}>
      <Text accessibilityRole="header" style={styles.title}>
        {title}
      </Text>
      <Text style={styles.message}>{message}</Text>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, padding: 16, gap: 8 },
  title: { fontSize: 22, fontWeight: '600' },
  message: { fontSize: 16 },
});
