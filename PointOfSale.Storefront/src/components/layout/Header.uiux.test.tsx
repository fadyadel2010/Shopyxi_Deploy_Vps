import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { HeaderPresentationModel, HeaderRuntimeSlots } from '@/storefront/presentation/models';

vi.mock('@/hooks/useScrollPosition', () => ({
    useScrollPosition: () => ({ isScrolled: false }), useBodyScrollLock: () => {}, useFocusTrap: () => {},
}));
vi.mock('@/components/common/AffiliateModeIndicator', () => ({ AffiliateModeIndicator: () => null }));
vi.mock('@/components/common/AffiliateLink', () => ({ AffiliateLink: ({ children }: { children: React.ReactNode }) => <a>{children}</a> }));
import { Header } from './Header';

const Empty = () => null;
const slots: HeaderRuntimeSlots = {
    AuthenticationSlot: Empty, CartSlot: Empty, LanguageSlot: Empty, NavigationSlot: Empty, WishlistSlot: Empty,
    SearchSlot: props => <button {...props} />,
};
describe('UIUX existing header variants use presentation copy', () => {
    it.each(['standard', 'centered', 'minimal'] as const)('localizes %s hamburger and search, with collapsed state', headerVariant => {
        const model: HeaderPresentationModel = {
            storeName: 'متجر', headerVariant, isHome: true, navigation: { links: [], isLoading: false, error: null },
            labels: { account: '', announcement: '', closeMenu: 'إغلاق القائمة', mainNavigation: 'التنقل الرئيسي',
                menu: 'القائمة', next: '', previous: '', search: 'بحث', searchProducts: 'البحث عن المنتجات', viewCart: '' },
        };
        const html = renderToStaticMarkup(<Header model={model} slots={slots} />);
        expect(html).toContain('aria-label="القائمة"');
        expect(html).toContain('aria-label="البحث عن المنتجات"');
        expect(html).toContain('aria-expanded="false"');
        expect(html).not.toContain('aria-label="Open menu"');
    });
});
