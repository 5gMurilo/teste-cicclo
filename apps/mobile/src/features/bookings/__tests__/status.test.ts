import {
  bookingPhase,
  hasUnrecognizedStatus,
  isKnownStatus,
  isTerminalStatus,
  statusLabel,
  UNRECOGNIZED_STATUS_LABEL,
} from '../status';

const items = (...statuses: string[]) => statuses.map((status) => ({ status }));

describe('bookingPhase', () => {
  it.each(['pending_activation', 'queued', 'to_be_confirmed', 'in_progress'])(
    'keeps a booking active while an item is %s',
    (status) => {
      expect(bookingPhase(items(status))).toBe('active');
      expect(isTerminalStatus(status)).toBe(false);
    },
  );

  it('sends a booking to history only when every item is completed, cancelled or failed', () => {
    expect(bookingPhase(items('completed', 'cancelled', 'failed'))).toBe('history');
    expect(bookingPhase(items('completed'))).toBe('history');
  });

  it('keeps a mixed batch active so its terminal items stay in the summary', () => {
    expect(bookingPhase(items('completed', 'failed', 'queued'))).toBe('active');
  });

  it('never treats an unrecognized status as terminal or as an actionable state', () => {
    expect(isTerminalStatus('teleported')).toBe(false);
    expect(isKnownStatus('teleported')).toBe(false);
    expect(bookingPhase(items('completed', 'teleported'))).toBe('active');
    expect(hasUnrecognizedStatus(items('completed', 'teleported'))).toBe(true);
    expect(hasUnrecognizedStatus(items('completed', 'queued'))).toBe(false);
    expect(statusLabel('teleported')).toBe(UNRECOGNIZED_STATUS_LABEL);
  });

  it('classifies a booking without items as neither active nor history', () => {
    expect(bookingPhase([])).toBe('empty');
  });
});
