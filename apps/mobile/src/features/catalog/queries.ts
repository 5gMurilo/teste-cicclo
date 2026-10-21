import { queryOptions } from '@tanstack/react-query';
import { z } from 'zod';

import { requestJson } from '../../shared/api/client';

// Contract: GET /api/v1/services (ServicesResponse in apps/api, see docs/api-contract.md).
const serviceSchema = z.object({
  code: z.string().min(1),
  name: z.string().min(1),
  machineType: z.enum(['washer', 'dryer']),
  unitPriceCents: z.number().int().min(1).max(999_999_999_999),
  currency: z.literal('BRL'),
  isActive: z.boolean(),
  canBeBooked: z.boolean(),
  // Kept as an open string so a new server-side reason does not hide the whole catalog.
  unavailabilityReason: z.string().min(1).nullable(),
});

const catalogSchema = z
  .object({ services: z.array(serviceSchema) })
  .refine(({ services }) => new Set(services.map((s) => s.code)).size === services.length, {
    message: 'duplicate service code',
  })
  .refine(({ services }) => services.every((s) => !(s.canBeBooked && s.unavailabilityReason !== null)), {
    message: 'bookable service with an unavailability reason',
  });

export type Service = z.infer<typeof serviceSchema>;
export type Catalog = z.infer<typeof catalogSchema>;

export const catalogKey = ['services'] as const;

const CATALOG_STALE_TIME_MS = 60_000;

async function fetchCatalog(signal: AbortSignal): Promise<Catalog> {
  return catalogSchema.parse(await requestJson({ method: 'GET', path: '/services', signal }));
}

export function catalogOptions() {
  return queryOptions({
    queryKey: catalogKey,
    queryFn: ({ signal }) => fetchCatalog(signal),
    staleTime: CATALOG_STALE_TIME_MS,
  });
}
