import { useLocalSearchParams } from 'expo-router';

import { BookingDetailsScreen } from '../../features/bookings/BookingDetailsScreen';

export default function BookingDetailsRoute() {
  const { id } = useLocalSearchParams<{ id: string }>();
  return <BookingDetailsScreen bookingId={Array.isArray(id) ? id[0] : id} />;
}
