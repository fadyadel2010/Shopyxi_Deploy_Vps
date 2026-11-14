// Install before application/SDK modules evaluate. Console arguments can contain
// Axios headers, cookies, tenant data, QR codes and arbitrary provider messages.
// Keep severity, a bounded event family and argument count; never inspect objects.
export function installProductionDiagnostics(target, production) {
  if (!production) return;
  const families = ['[Bootstrap]', '[Startup]', '[App]', '[TenantResolver]', '[RequestContext]', '[Homepage]', '[CommerceHub Checkout]'];
  for (const method of ['debug', 'log', 'info', 'warn', 'error', 'trace', 'dir', 'dirxml', 'table', 'group', 'groupCollapsed']) {
    const original = target[method];
    if (typeof original !== 'function') continue;
    const safe = (...args) => {
      const family = typeof args[0] === 'string' ? families.find(value => args[0].startsWith(value)) : undefined;
      original.call(target, 'Production client diagnostic', {
        event: family ?? 'CLIENT',
        argumentCount: args.length,
        objectCount: args.filter(value => value !== null && typeof value === 'object').length,
      });
    };
    Object.defineProperty(target, method, { value: safe, writable: false, configurable: false });
  }
}

installProductionDiagnostics(console, import.meta.env.PROD);
