import { useCallback } from 'react';
import { useTranslation as useStorefrontTranslation } from '@/i18n';

/** Use the storefront's language authority, not the admin's react-i18next instance. */
export function useChatTranslation() {
    const { t: translate, language } = useStorefrontTranslation();
    const t = useCallback((key: string, fallback?: string) => {
        const value = translate(key);
        return value === key ? fallback ?? key : value;
    }, [translate]);
    return { t, i18n: { language } };
}
