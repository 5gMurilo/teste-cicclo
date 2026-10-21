import { useCallback } from 'react';
import { z } from 'zod';

import { capabilities } from '../../shared/api/capabilities';
import { postItemCommand, useItemCommand } from './itemCommand';

// Contract: ReportIssueResponse (202). A report is only a statement by the user: it changes no item, no
// machine and no balance, and never implies a refund; confirming a defect belongs to the simulation.
const reportResponseSchema = z.object({
  incidentId: z.uuid(),
  status: z.literal('reported'),
});

export type ReportResult = z.infer<typeof reportResponseSchema>;

export const parseReportResponse = (json: unknown): ReportResult => reportResponseSchema.parse(json);

/** Only an execution that is running can be reported. */
export function canReportIssue(status: string): boolean {
  return capabilities.itemCommands.reportIssue && status === 'in_progress';
}

export function useReportIssue(bookingId: string) {
  const send = useCallback(
    async (itemId: string): Promise<ReportResult> =>
      parseReportResponse(await postItemCommand({ bookingId, itemId, action: 'report-issue' })),
    [bookingId],
  );
  return useItemCommand(send);
}
