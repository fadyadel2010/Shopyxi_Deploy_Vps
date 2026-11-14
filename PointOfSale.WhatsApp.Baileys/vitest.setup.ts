import { vi } from 'vitest';

// Preserve the existing assertion/test identities and mock-call API while using
// Vitest's maintained implementation. Module interception is explicitly vi.mock
// in each test, so it is correctly hoisted before application modules load.
(globalThis as unknown as { jest: typeof vi }).jest = vi;
