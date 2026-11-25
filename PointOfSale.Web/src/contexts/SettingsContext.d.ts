import type { ReactElement, ReactNode } from 'react';
export interface PosSettings { currencyCode: string; currencySymbol: string; enableTax: boolean; taxRate: number; companyName: string; companyLogo: string; companyAddress: string; companyPhone: string; companyEmail: string; receiptHeader: string; receiptFooter: string; showLogoOnReceipt: boolean; storefrontUrl: string; printFormat: string }
export type SettingsStatus = 'idle' | 'loading' | 'ready' | 'error';
export type SettingsErrorKind = 'unauthorized' | 'failed' | 'malformed';
export type SettingsProvenance = 'confirmed' | 'stale' | 'unconfirmed';
export const SETTINGS_STATUS: { IDLE: 'idle'; LOADING: 'loading'; READY: 'ready'; ERROR: 'error' };
export const SETTINGS_ERROR: { UNAUTHORIZED: 'unauthorized'; FAILED: 'failed'; MALFORMED: 'malformed' };
export const SETTINGS_PROVENANCE: { CONFIRMED: 'confirmed'; STALE: 'stale'; UNCONFIRMED: 'unconfirmed' };
export function SettingsProvider(props: { children: ReactNode }): ReactElement;
export function useSettings(): {
    settings: PosSettings;
    loading: boolean;
    status: SettingsStatus;
    error: SettingsErrorKind | null;
    provenance: SettingsProvenance;
    currencyProvenance: SettingsProvenance;
    currencyConfirmed: boolean;
    updateSettings(settings: Partial<PosSettings>): Promise<boolean>;
    updateLocalSettings<K extends keyof PosSettings>(key: K, value: PosSettings[K]): void;
    formatCurrency(amount: number | string | null | undefined, currency?: string): string;
    reloadSettings(): Promise<boolean>;
};
