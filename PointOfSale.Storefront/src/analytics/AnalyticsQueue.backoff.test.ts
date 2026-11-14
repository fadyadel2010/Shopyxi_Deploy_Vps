import { afterEach, expect, it, vi } from 'vitest';
import type { AnalyticsEvent } from './eventTypes';

const runtime = vi.hoisted(() => ({ send: vi.fn() }));
vi.mock('./AnalyticsClient', () => ({ sendAnalyticsBatch: runtime.send }));
vi.mock('@/bootstrap/tenantScopedStorage', () => ({ tryGetTenantScopedStorageKey: () => 'qa:tenant:analytics' }));

afterEach(() => { vi.clearAllTimers(); vi.useRealTimers(); vi.unstubAllGlobals(); vi.resetModules(); vi.clearAllMocks(); });

async function queueWithEvent() {
    vi.useFakeTimers();
    vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn() });
    vi.stubGlobal('window', { addEventListener: vi.fn() });
    vi.stubGlobal('document', { addEventListener: vi.fn() });
    const { analyticsQueue } = await import('./AnalyticsQueue');
    analyticsQueue.enqueue({ eventId: 'same-id-on-retry' } as AnalyticsEvent);
    return analyticsQueue;
}

it('honors Retry-After across periodic, online and enqueue-triggered flush attempts', async () => {
    runtime.send.mockRejectedValue(Object.assign(new Error('429'), { retryAfterMs: 60000 }));
    const queue = await queueWithEvent();
    await queue.flush();
    await queue.flush();
    await vi.advanceTimersByTimeAsync(59000);
    expect(runtime.send).toHaveBeenCalledTimes(1);
    runtime.send.mockResolvedValue({ accepted: 1, rejected: 0, acceptedEventIds: ['same-id-on-retry'], errors: [] });
    await vi.advanceTimersByTimeAsync(1000);
    expect(runtime.send).toHaveBeenCalledTimes(2);
    expect(queue.getQueueLength()).toBe(0);
});

it('does not spin synchronously on an ambiguous success response', async () => {
    runtime.send.mockResolvedValue({ accepted: 0, rejected: 0, acceptedEventIds: [], errors: [] });
    const queue = await queueWithEvent();
    await queue.flush();
    expect(runtime.send).toHaveBeenCalledTimes(1);
    expect(queue.getQueueLength()).toBe(1);
});

it.each(['accepted', 'ambiguous', 'failed'])('preserves new events when a full queue evicts an in-flight batch (%s)', async outcome => {
    let complete!: () => void;
    runtime.send.mockImplementationOnce(() => new Promise((resolve, reject) => {
        complete = () => outcome === 'failed'
            ? reject(new Error('offline'))
            : resolve({ accepted: outcome === 'accepted' ? 1 : 0, rejected: 0,
                acceptedEventIds: outcome === 'accepted' ? ['same-id-on-retry'] : [], errors: [] });
    }));
    runtime.send.mockResolvedValue({ accepted: 0, rejected: 0, acceptedEventIds: [], errors: [] });
    const queue = await queueWithEvent();
    const pending = queue.flush();
    for (let i = 0; i < 500; i++) {
        queue.enqueue({ eventId: `new-${i}` } as AnalyticsEvent);
    }
    complete();
    await pending;

    const persisted = JSON.parse(vi.mocked(localStorage.setItem).mock.lastCall![1]) as { event: AnalyticsEvent }[];
    expect(persisted).toHaveLength(500);
    expect(new Set(persisted.map(item => item.event.eventId)))
        .toEqual(new Set(Array.from({ length: 500 }, (_, i) => `new-${i}`)));
});
