import type { ReactElement, ReactNode } from 'react';
export function FooterConfigScreen(props: { scope: string; title: string; description?: string; note?: string; guide?: ReactNode; localized?: boolean; allowCmsSlugEntry?: boolean; loadCmsPages?(): Promise<{ slug: string; title?: string }[]>; onTranslateContent?(content: string, from: string, to: string): Promise<string> }): ReactElement;
export default FooterConfigScreen;
