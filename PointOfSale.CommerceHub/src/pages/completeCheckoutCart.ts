/** Called only after canonical checkout has committed. Server cart locks remain authoritative. */
export async function completeCheckoutCart<T>(
  releaseSubmissionLock: (locked: boolean) => void,
  clearCart: () => Promise<T>
): Promise<T> {
  releaseSubmissionLock(false);
  return await clearCart();
}
