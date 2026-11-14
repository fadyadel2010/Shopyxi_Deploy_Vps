/**
 * The single low-level Axios boundary. Session-specific clients must remain
 * separate: sharing interceptors would mix POS, affiliate and platform authority.
 */
export { default } from 'axios'
export type { AxiosError, AxiosInstance, AxiosRequestConfig, InternalAxiosRequestConfig } from 'axios'
