import { describe, expect, it } from 'vitest';
import { isAtCartStockLimit } from './cartStockLimit';

describe('server-admitted cart stock cap', () => {
    it.each([null, undefined])('does not coerce an unlimited cap %s into zero', cap => {
        expect(isAtCartStockLimit(1, cap)).toBe(false);
        expect(isAtCartStockLimit(99, cap)).toBe(false);
    });
    it('retains zero, available and insufficient tracked-stock limits', () => {
        expect(isAtCartStockLimit(1, 0)).toBe(true);
        expect(isAtCartStockLimit(1, 2)).toBe(false);
        expect(isAtCartStockLimit(2, 2)).toBe(true);
        expect(isAtCartStockLimit(3, 2)).toBe(true);
    });
});
