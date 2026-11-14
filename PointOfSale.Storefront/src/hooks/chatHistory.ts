import type { ChatMessage, OrderDraft } from '@/types/chat';

function record(value: unknown): Record<string, unknown> | null {
    return typeof value === 'object' && value !== null ? value as Record<string, unknown> : null;
}

function readDraft(value: unknown): OrderDraft | undefined {
    const draft = record(value);
    if (!draft || !['none', 'create', 'update', 'confirm'].includes(String(draft.intent)) || !Array.isArray(draft.items)) return undefined;
    const items: OrderDraft['items'] = [];
    for (const value of draft.items as unknown[]) {
        const item = record(value);
        if (!item || typeof item.productId !== 'string' || typeof item.quantity !== 'number' || !Number.isFinite(item.quantity)) return undefined;
        items.push({ productId: item.productId, quantity: item.quantity,
            variantId: typeof item.variantId === 'string' ? item.variantId : undefined,
            productName: typeof item.productName === 'string' ? item.productName : undefined,
            productImage: typeof item.productImage === 'string' ? item.productImage : undefined });
    }
    return { intent: draft.intent as OrderDraft['intent'], items,
        shippingMethodId: typeof draft.shippingMethodId === 'string' ? draft.shippingMethodId : undefined };
}

/** Browser storage is untrusted display history; it never authorizes checkout or prices. */
export function parseStoredChatHistory(value: unknown): ChatMessage[] {
    if (!Array.isArray(value)) return [];
    return (value as unknown[]).flatMap((value): ChatMessage[] => {
        const message = record(value);
        if (!message || typeof message.id !== 'string' || (message.role !== 'user' && message.role !== 'assistant') ||
            typeof message.content !== 'string' || typeof message.timestamp !== 'string') return [];
        const timestamp = new Date(message.timestamp);
        if (!Number.isFinite(timestamp.getTime())) return [];
        return [{ id: message.id, role: message.role, content: message.content, timestamp,
            orderDraft: readDraft(message.orderDraft),
            missingInfo: Array.isArray(message.missingInfo) ? message.missingInfo.filter((item): item is string => typeof item === 'string') : undefined,
            isFallback: typeof message.isFallback === 'boolean' ? message.isFallback : undefined }];
    });
}
