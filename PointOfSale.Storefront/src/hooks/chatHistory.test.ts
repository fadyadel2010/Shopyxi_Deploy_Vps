import { describe, expect, it } from 'vitest';
import { parseStoredChatHistory } from './chatHistory';
describe('stored chat history', () => {
    const message = { id: '1', role: 'assistant', content: 'Draft', timestamp: '2026-10-26T00:00:00Z',
        orderDraft: { intent: 'create', items: [{ productId: 'product', quantity: 1 }], shippingMethodId: 'shipping' }, missingInfo: ['address'], isFallback: false };
    it('preserves valid optional display/draft fields and restores Dates', () => {
        expect(parseStoredChatHistory([message])).toEqual([{ ...message, timestamp: new Date(message.timestamp),
            orderDraft: { ...message.orderDraft, items: [{ ...message.orderDraft.items[0], variantId: undefined, productName: undefined, productImage: undefined }] } }]);
    });
    it('rejects invalid messages and malformed drafts without trusting storage shape', () => {
        expect(parseStoredChatHistory({})).toEqual([]);
        expect(parseStoredChatHistory([null, { ...message, timestamp: 'invalid' }, { ...message, content: {} }])).toEqual([]);
        expect(parseStoredChatHistory([{ ...message, orderDraft: { intent: 'create', items: [{}] } }])[0].orderDraft).toBeUndefined();
    });
});
