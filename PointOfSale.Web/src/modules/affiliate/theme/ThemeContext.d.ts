import type { ReactNode } from 'react'
export function ThemeProvider(props: { children: ReactNode }): JSX.Element;
export function useTheme(): { theme: string; toggleTheme: () => void };
