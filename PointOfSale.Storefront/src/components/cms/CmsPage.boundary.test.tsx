import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { CmsPageRenderer } from './CmsPage';
import type { CmsPage } from '@/types';

describe('CMS block runtime admission', () => {
    it('refuses malformed array elements before rendering or deriving keys', () => {
        const page = { contentType: 'Blocks', content: [null, 42, 'text', [], { type: {} }, { type: 'text', data: null }] } as unknown as CmsPage;
        const html = renderToStaticMarkup(<CmsPageRenderer page={page} />);
        expect(html).toContain('This page contains invalid or empty content.');
    });

    it('preserves admitted text and escapes untrusted markup alongside malformed elements', () => {
        const page = { contentType: 'Blocks', content: [null, { type: 'text', data: { text: '<script>not executable</script>' } }] } as unknown as CmsPage;
        const html = renderToStaticMarkup(<CmsPageRenderer page={page} />);
        expect(html).toContain('&lt;script&gt;not executable&lt;/script&gt;');
        expect(html).not.toContain('<script>');
    });
});
