import { describe, expect, it } from 'vitest';
import layoutSource from './ThemeLayoutResolver.ts?raw';
import sectionSource from './ThemeSectionResolver.ts?raw';

describe('production theme discovery', () => {
    it('excludes colocated tests from every production glob', () => {
        expect(layoutSource).toContain('!./*/layout/*.test.tsx');
        expect(layoutSource).toContain('!./*/layout/*.spec.tsx');
        expect(sectionSource).toContain('!./*/sections/*.test.tsx');
        expect(sectionSource).toContain('!./*/sections/*.spec.tsx');
        expect(sectionSource).toContain('!../../components/sections/defaults/*.test.tsx');
        expect(sectionSource).toContain('!../../components/sections/defaults/*.spec.tsx');
    });
});
