import { describe, expect, it, vi, afterEach } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { authScratchDirectory } from '../AuthScratchDirectory';

vi.mock('node:fs', () => ({ mkdirSync: vi.fn(), lstatSync: vi.fn(() => ({ isSymbolicLink: () => false })) }));

afterEach(() => vi.clearAllMocks());

describe('Authentication scratch directory', () => {
  it('uses the existing writable temporary mount in Production, not the read-only application tree', () => {
    expect(authScratchDirectory('cdbe20e0-f84a-4416-b20c-044e6bcf04d0', true))
      .toBe(path.join('/tmp/shopixy-whatsapp-auth', 'cdbe20e0-f84a-4416-b20c-044e6bcf04d0'));
    expect(fs.mkdirSync).toHaveBeenCalledWith('/tmp/shopixy-whatsapp-auth', { recursive: true, mode: 0o700 });
  });
  it('preserves the development scratch location', () => {
    expect(authScratchDirectory('test-tenant', false)).toBe(path.resolve('auth_states/test-tenant'));
  });
  it.each(['../another-tenant', '', '/absolute', 'a/b', 'a\\b'])('rejects unsafe tenant paths: %s', id => {
    expect(() => authScratchDirectory(id, true)).toThrow('Invalid tenant identifier');
    expect(fs.mkdirSync).not.toHaveBeenCalled();
  });
  it('rejects a redirected scratch root', () => {
    vi.mocked(fs.lstatSync).mockReturnValueOnce({ isSymbolicLink: () => true } as fs.Stats);
    expect(() => authScratchDirectory('test-tenant', true)).toThrow('must not be a symbolic link');
  });
});
