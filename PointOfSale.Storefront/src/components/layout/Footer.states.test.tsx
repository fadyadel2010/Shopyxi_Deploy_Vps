import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { Footer } from './Footer';
import type { FooterPresentationModel } from '@/storefront/presentation/models';

const model: FooterPresentationModel = {
    storeName: 'QA storefront', currentYear: 2026, copyright: 'QA',
    navigation: { links: [], isLoading: false, error: null },
    sections: [], socialLinks: [], isLoading: false, error: null,
    labels: {
        contact: 'Contact', customerService: 'Customer service', followUs: 'Follow us',
        loading: 'Loading...', empty: 'No links published.', loadError: 'Links unavailable.',
        paymentMethods: 'Payment methods', quickLinks: 'Quick links',
    },
};

describe('footer terminal states', () => {
    it('renders the configured copyright exactly once without rewriting Owner content', () => {
        const copyright = 'OWNER COPYRIGHT VERBATIM';
        const html = renderToStaticMarkup(<Footer model={{ ...model, copyright }} />);
        expect(html.split(copyright)).toHaveLength(2);
    });
    it('shows loading only while a request is pending', () => {
        expect(renderToStaticMarkup(<Footer model={{ ...model, isLoading: true }} />)).toContain('Loading...');
    });
    it('a successful empty result is not perpetual loading', () => {
        const html = renderToStaticMarkup(<Footer model={model} />);
        expect(html).toContain('No links published.');
        expect(html).not.toContain('Loading...');
    });
    it('a failed request reaches a localized error state without leaking diagnostic text', () => {
        const html = renderToStaticMarkup(<Footer model={{ ...model, error: 'internal diagnostic' }} />);
        expect(html).toContain('Links unavailable.');
        expect(html).not.toContain('internal diagnostic');
        expect(html).not.toContain('Loading...');
    });
});
