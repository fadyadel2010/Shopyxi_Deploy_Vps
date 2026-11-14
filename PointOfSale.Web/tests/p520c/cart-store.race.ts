import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { CommerceHubCartState, CartItemInput } from '../../../PointOfSale.CommerceHub/src/types/cart';

const service = vi.hoisted(() => ({ getCart: vi.fn(), saveCart: vi.fn(), clearCart: vi.fn() }));
vi.mock('@/services', () => ({ cartService: service }));
import { useCartStore } from '../../../PointOfSale.CommerceHub/src/store/cart-store';
import { completeCheckoutCart } from '../../../PointOfSale.CommerceHub/src/pages/completeCheckoutCart';

const item: CartItemInput = {
  storeId: 'vendor-1', storeName: 'P520C', productId: 'product-1', variantId: 'variant-1',
  listingId: 'listing-1', name: 'P520C race fixture', quantity: 1, priceSnapshot: 100,
  shippingFee: 0, currency: 'EGP', maxQuantity: 10, isInStock: true
};
const empty = (version = 0): CommerceHubCartState => ({ items: [], version, isServerBacked: false, isLocked: false, notices: [], isAuthenticatedOwner: false, updatedAtUtc: '2026-10-28T00:00:00Z', warnings: [] });
const deferred = <T,>() => {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
};
const store = () => useCartStore.getState();
beforeEach(() => {
  vi.resetAllMocks();
  useCartStore.setState(useCartStore.getInitialState(), true);
  store().addItem(item);
});

describe('newer scoped cart intent wins over asynchronous snapshots', () => {
  it('clears an authenticated cart after committed checkout releases its submission lock', async () => {
    useCartStore.setState({ isServerBacked: true, isCheckoutLocked: true });
    const clear = deferred<CommerceHubCartState>();
    service.clearCart.mockReturnValue(clear.promise);
    const pending = completeCheckoutCart(store().setCheckoutLock, store().clearCart);
    expect(store().isCheckoutLocked).toBe(false);
    expect(store().stores).toEqual({});
    expect(service.clearCart).toHaveBeenCalledOnce();
    clear.resolve({ ...empty(2), isServerBacked: true });
    await pending;
    expect(store().stores).toEqual({});
    expect(store().version).toBe(2);
  });
  it('checkout completion never overrides a backend-owned cart lock', async () => {
    useCartStore.setState({ isServerBacked: true, isCheckoutLocked: true, isLocked: true });
    await completeCheckoutCart(store().setCheckoutLock, store().clearCart);
    expect(service.clearCart).not.toHaveBeenCalled();
    expect(store().isLocked).toBe(true);
    expect(store().stores[item.storeId].items).toHaveLength(1);
  });
  it('does not resurrect a line removed during bootstrap GET', async () => {
    const read = deferred<CommerceHubCartState>();
    service.getCart.mockReturnValue(read.promise);
    const bootstrap = store().bootstrapCart();
    store().removeItem(item.storeId, item.variantId);
    read.resolve(empty());
    await bootstrap;
    expect(store().stores).toEqual({});
    expect(store().hasBootstrapped).toBe(true);
    expect(service.saveCart).not.toHaveBeenCalled();
  });
  it('does not resurrect a line removed during guest sanitization', async () => {
    service.getCart.mockResolvedValue(empty());
    const save = deferred<CommerceHubCartState>();
    service.saveCart.mockReturnValue(save.promise);
    const bootstrap = store().bootstrapCart();
    await vi.waitFor(() => expect(service.saveCart).toHaveBeenCalledOnce());
    store().removeItem(item.storeId, item.variantId);
    save.resolve({ ...empty(), items: [{ ...item, unitPrice: 100 }] } as unknown as CommerceHubCartState);
    await bootstrap;
    expect(store().stores).toEqual({});
    expect(store().isSyncing).toBe(false);
  });
  it('preserves quantity updates and carries forward server version', async () => {
    const save = deferred<CommerceHubCartState>();
    service.saveCart.mockReturnValue(save.promise);
    const pending = store().saveCart({ persistIfAvailableOnly: false });
    store().updateQuantity(item.storeId, item.variantId, 3);
    save.resolve(empty(4));
    await pending;
    expect(store().stores[item.storeId].items[0].quantity).toBe(3);
    expect(store().version).toBe(4);
  });
  it('rejects out-of-order saves and foreign-scope responses', async () => {
    const old = deferred<CommerceHubCartState>();
    const recent = deferred<CommerceHubCartState>();
    service.saveCart.mockReturnValueOnce(old.promise).mockReturnValueOnce(recent.promise);
    const first = store().saveCart({ persistIfAvailableOnly: false });
    const second = store().saveCart({ persistIfAvailableOnly: false });
    recent.resolve(empty(5)); await second;
    old.resolve(empty(1)); await first;
    expect(store().version).toBe(5);
    const foreign = deferred<CommerceHubCartState>();
    service.saveCart.mockReturnValueOnce(foreign.promise);
    const pending = store().saveCart({ persistIfAvailableOnly: false });
    store().synchronizeAuthScope('customer-other');
    foreign.resolve({ ...empty(99), isLocked: true }); await pending;
    expect(store().version).toBe(0);
    expect(store().isLocked).toBe(false);
  });
  it('clear invalidates an older save, and repeated adds do not duplicate identity', async () => {
    store().addItem(item);
    expect(store().stores[item.storeId].items).toHaveLength(1);
    const save = deferred<CommerceHubCartState>();
    service.saveCart.mockReturnValue(save.promise);
    const pending = store().saveCart({ persistIfAvailableOnly: false });
    await store().clearCart();
    save.resolve(empty(8)); await pending;
    expect(store().stores).toEqual({});
    expect(store().version).toBe(0);
    expect(store().isSyncing).toBe(false);
  });
  it('remote clear keeps only additions made after clear intent', async () => {
    useCartStore.setState({ isServerBacked: true });
    const clear = deferred<CommerceHubCartState>();
    service.clearCart.mockReturnValue(clear.promise);
    const pending = store().clearCart();
    expect(store().stores).toEqual({});
    store().addItem({ ...item, variantId: 'new-variant' });
    clear.resolve({ ...empty(6), isServerBacked: true });
    await pending;
    expect(store().stores[item.storeId].items.map(x => x.variantId)).toEqual(['new-variant']);
    expect(store().version).toBe(6);
  });
  it('keeps server locks authoritative when a stale save finishes after removal', async () => {
    const save = deferred<CommerceHubCartState>();
    service.saveCart.mockReturnValue(save.promise);
    const pending = store().saveCart({ persistIfAvailableOnly: false });
    store().removeItem(item.storeId, item.variantId);
    save.resolve({ ...empty(7), isLocked: true }); await pending;
    expect(store().stores).toEqual({});
    expect(store().isLocked).toBe(true);
    store().addItem(item);
    expect(store().stores).toEqual({});
  });
});
