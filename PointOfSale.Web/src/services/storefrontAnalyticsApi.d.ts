/** Transport data is untrusted until the consuming runtime validator accepts it. */
export function getStorefrontAnalyticsDashboard(range?: string): Promise<unknown>
export function getStorefrontLiveMonitoring(): Promise<unknown>
declare const api: {
    getStorefrontAnalyticsDashboard: typeof getStorefrontAnalyticsDashboard
    getStorefrontLiveMonitoring: typeof getStorefrontLiveMonitoring
}
export default api
