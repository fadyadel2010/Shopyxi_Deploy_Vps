import { describe, expect, it } from 'vitest';
import { normalizeError, getApiErrorDetails, isRequestCanceled } from './httpClient';

describe('unknown error boundary', () => {
    it.each([null, undefined, 42, 'failure', { response: { data: { message: 42, code: [] } } }])('never treats malformed data as a renderable error: %j', value => {
        expect(normalizeError(value).message).toBeUndefined();
        expect(normalizeError(value).code).toBeUndefined();
    });
    it('preserves typed API status, message, code and structured details', () => {
        const error = { response: { status: 409, data: { code: 'STOCK', message: 'Changed', data: { availableStock: 2 } } } };
        expect(normalizeError(error)).toMatchObject({ status: 409, code: 'STOCK', message: 'Changed', isClientError: true, retryable: false });
        expect(getApiErrorDetails(error)?.data).toEqual({ availableStock: 2 });
    });
    it('preserves Error, network failure and legacy casing without trusting arbitrary values', () => {
        expect(normalizeError(new Error('Network Error'))).toMatchObject({ isNetworkError: true, retryable: true });
        expect(normalizeError({ response: { data: { Code: 'OLD', Message: 'Legacy' } } })).toMatchObject({ code: 'OLD', message: 'Legacy' });
    });
    it.each([{}, [], 42, true])('rejects a malformed cancellation message without throwing: %j', message => {
        const error = { message };
        expect(isRequestCanceled(error)).toBe(false);
        expect(normalizeError(error).message).toBeUndefined();
    });
    it.each([NaN, Infinity, -Infinity])('does not promote nonfinite transport status into retry or auth authority: %s', status => {
        expect(normalizeError({ response: { status } })).toMatchObject({
            status: undefined, isServerError: false, isClientError: false, isAuthError: false, retryable: false,
        });
    });
    it('retains cancellation identity and never retries canceled requests', () => {
        expect(isRequestCanceled({ message: ' CANCELED ' })).toBe(true);
        expect(normalizeError({ code: 'ERR_CANCELED', message: {}, response: { status: 503 } }).retryable).toBe(false);
    });
});
