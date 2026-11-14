import type { ReactNode } from 'react'
export function LanguageProvider(props: { children: ReactNode }): JSX.Element;
export function useLanguage(): { lang: string; setLang: (lang: string) => void; toggleLang: () => void; t: (path: string, params?: Record<string, string | number>) => string; dir: string; isRTL: boolean; locale: string; formatNumber: (value: number, options?: Intl.NumberFormatOptions) => string; formatDate: (value: string, options?: Intl.DateTimeFormatOptions) => string; formatMoney: (value: number, currency: string, options?: Intl.NumberFormatOptions) => string };
