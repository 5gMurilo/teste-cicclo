import { useCallback } from 'react';
import { z } from 'zod';

import { capabilities } from '../../shared/api/capabilities';
import { requestJson } from '../../shared/api/client';
import { isDemoSimulationEnabled } from '../../shared/config/env';
import { InvalidBookingIdError, isBookingId } from './detailQuery';
import { useItemCommand } from './itemCommand';
import { bookingItemSchema, type BookingItem } from './schema';

// Contract: POST /api/v1/simulation/machines/{machineId}/complete (SimulationResponse). Demo only: the route
// exists when the API runs in Development with Laundry__DemoMode=true and answers 404 otherwise.
export const simulationResponseSchema = z.object({
  machine: z.object({
    machineId: z.uuid(),
    name: z.string(),
    machineType: z.string(),
    state: z.string(),
    activeItemId: z.string().nullable(),
    isDemoOccupied: z.boolean(),
  }),
  item: bookingItemSchema.nullable(),
});

export type SimulationResult = z.infer<typeof simulationResponseSchema>;

/** Offered only for a running item, on an opted-in demo build, once the capability is proven. */
export function canSimulateComplete(item: BookingItem): boolean {
  return (
    capabilities.simulation.complete &&
    isDemoSimulationEnabled() &&
    item.status === 'in_progress' &&
    item.machineId !== null
  );
}

export function useSimulateComplete(machineId: string | null) {
  const send = useCallback(async (): Promise<SimulationResult> => {
    if (machineId === null || !isBookingId(machineId)) throw new InvalidBookingIdError();
    return simulationResponseSchema.parse(
      await requestJson({ method: 'POST', path: `/simulation/machines/${machineId}/complete` }),
    );
  }, [machineId]);
  return useItemCommand(send);
}
