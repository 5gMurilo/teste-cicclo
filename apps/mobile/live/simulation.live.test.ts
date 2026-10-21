/**
 * Demo-only machine simulation against the REAL API (needs Development + Laundry__DemoMode=true). Not part of
 * `pnpm test`. It COMPLETES the machine that is running right now (the item becomes completed).
 *
 *   EXPO_PUBLIC_API_URL=http://127.0.0.1:5001 pnpm --filter @cicclo/mobile exec jest --ci --runInBand --verbose \
 *     --testMatch='**\/live/simulation.live.test.ts' | tee /tmp/live-simulation.log
 */
import { simulationResponseSchema } from '../src/features/bookings/completeSimulation';
import { ApiError } from '../src/shared/api/apiError';
import { requestJson } from '../src/shared/api/client';
import { installNodeFetch } from './nodeFetch';

jest.setTimeout(60_000);
installNodeFetch();

type Machine = { machineId: string; name: string; state: string; activeItemId: string | null };
const machines = async () => ((await requestJson({ method: 'GET', path: '/machines' })) as { machines: Machine[] }).machines;

describe('simulation: complete a running machine', () => {
  it('completes the item, frees or re-offers the machine, and refuses a repeat', async () => {
    const running = (await machines()).find((m) => m.state === 'running' && m.activeItemId !== null);
    if (!running) throw new Error('precondition: a machine must be running with a real item');

    const response = simulationResponseSchema.parse(
      await requestJson({ method: 'POST', path: `/simulation/machines/${running.machineId}/complete` }),
    );
    console.log('complete answered:', JSON.stringify({ machine: response.machine, item: response.item }));
    expect(response.machine.machineId).toBe(running.machineId);
    expect(response.item).toMatchObject({ itemId: running.activeItemId, status: 'completed' });
    expect(['available', 'reserved']).toContain(response.machine.state);

    const after = (await machines()).find((m) => m.machineId === running.machineId);
    expect(after?.state).toBe(response.machine.state);

    let repeat: unknown;
    try {
      await requestJson({ method: 'POST', path: `/simulation/machines/${running.machineId}/complete` });
    } catch (error) {
      repeat = error;
    }
    if (after?.state === 'running') throw new Error('machine unexpectedly running again');
    expect(repeat).toBeInstanceOf(ApiError);
    expect(repeat).toMatchObject({ status: 409, errorCode: 'INVALID_STATE_TRANSITION' });
  });
});
