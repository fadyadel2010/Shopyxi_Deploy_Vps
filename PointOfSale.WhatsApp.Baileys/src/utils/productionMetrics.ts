interface Metric {
  name: string;
  help: string;
  type: string | number;
  values: { metricName?: string; value: number; labels?: Partial<Record<string, string | number>> }[];
}

/** Aggregate rather than expose tenant IDs or SDK-controlled reason strings. */
export function renderProductionMetrics(metrics: Metric[]): string {
  const lines: string[] = [];
  for (const metric of metrics) {
    lines.push(`# HELP ${metric.name} ${metric.help}`, `# TYPE ${metric.name} ${metric.type}`);
    const sums = new Map<string, number>();
    for (const sample of metric.values) {
      const labels: Record<string, string> = {};
      for (const [key, value] of Object.entries(sample.labels ?? {})) {
        const text = String(value);
        if (key === 'le' && (text === '+Inf' || /^-?\d+(?:\.\d+)?(?:e[+-]?\d+)?$/i.test(text))) labels[key] = text;
        if (key === 'result') labels[key] = ['success', 'failure', 'timeout'].includes(text) ? text : 'other';
        if (key === 'state') labels[key] = ['DISCONNECTED', 'CONNECTING', 'QR_PENDING', 'CONNECTED', 'RECONNECTING', 'FAILED', 'POISONED'].includes(text) ? text : 'other';
        if (key === 'status_code') labels[key] = /^[1-5]\d\d$/.test(text) ? text : 'other';
      }
      const rendered = Object.entries(labels).sort(([a], [b]) => a.localeCompare(b))
        .map(([key, value]) => `${key}=${JSON.stringify(value)}`).join(',');
      const key = (sample.metricName ?? metric.name) + (rendered ? `{${rendered}}` : '');
      sums.set(key, (sums.get(key) ?? 0) + sample.value);
    }
    for (const [key, value] of sums) lines.push(`${key} ${Number.isNaN(value) ? 'NaN' : value === Infinity ? '+Inf' : value === -Infinity ? '-Inf' : value}`);
  }
  return lines.join('\n') + '\n';
}
