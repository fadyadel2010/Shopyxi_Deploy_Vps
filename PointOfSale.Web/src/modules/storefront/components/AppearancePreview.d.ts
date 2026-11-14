import type { ReactElement } from 'react';

export interface AppearancePreviewProps {
    colors: { primary: string; secondary: string; background: string; text: string; muted: string };
    font: string;
    baseSize: number;
    borderRadius: number;
    config?: Record<string, unknown>;
    tenantId?: string;
    language?: string;
}

export function AppearancePreview(props: AppearancePreviewProps): ReactElement;
