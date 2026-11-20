import { describe, expect, it } from 'vitest';
import { t } from './languageStore';

describe('Storefront missing-key fallback policy', () => {
    it.each(['en', 'ar'] as const)('uses the caller default only when the key is missing (%s)', language => {
        expect(t('common.absentKey', { defaultValue: 'Fallback copy' }, language)).toBe('Fallback copy');
        expect(t('common.absentKey', { count: 3, defaultValue: 'Fallback {count}' }, language)).toBe('Fallback 3');
    });

    it('still returns the key when neither a translation nor a default exists', () => {
        expect(t('common.absentKey', undefined, 'en')).toBe('common.absentKey');
        expect(t('common.absentKey', { defaultValue: '   ' }, 'en')).toBe('common.absentKey');
    });

    it('never lets a default override a real translation', () => {
        expect(t('common.loading', { defaultValue: 'Wrong copy' }, 'en')).toBe('Loading...');
        expect(t('common.loading', { defaultValue: 'Wrong copy' }, 'ar')).not.toBe('Wrong copy');
    });

    it.each(['en', 'ar'] as const)('resolves the previously literal storefront keys in %s', language => {
        for (const key of ['common.store', 'common.backToStore', 'common.remove', 'filters.noResults', 'filters.tryDifferent']) {
            expect(t(key, undefined, language), key).not.toBe(key);
        }
    });
});
