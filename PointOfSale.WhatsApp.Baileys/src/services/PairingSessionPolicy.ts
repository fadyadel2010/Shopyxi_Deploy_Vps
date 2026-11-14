/** A QR is advertised for 60s; scanning and the subsequent handshake need time too. */
export const QR_PAIRING_GRACE_MS = 3 * 60 * 1000;

export function transitionalSessionTimeoutMs(status: string, connectingTimeoutMs: number): number {
  return status === 'qr_pending' ? QR_PAIRING_GRACE_MS : connectingTimeoutMs;
}
