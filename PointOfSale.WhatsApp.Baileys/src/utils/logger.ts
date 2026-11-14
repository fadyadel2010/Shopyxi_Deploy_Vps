import pino, { type LogFn } from 'pino';
import { config } from '../config';

/**
 * Create logger instance
 */
export const logger = pino({
  level: config.logging.level,
  // Production is a fail-closed diagnostic boundary: arbitrary SDK error text,
  // bindings, messages and state can contain keys/QR/auth data. Preserve time and
  // level only; Development/Test retain detailed local diagnostics.
  ...(config.nodeEnv === 'production' ? {
    formatters: {
      bindings: () => ({}),
      log: () => ({})
    },
    hooks: {
      logMethod(_args: unknown[], method: LogFn) {
        method.call(this, 'Production event; diagnostic payload omitted.');
      }
    }
  } : {}),
  transport: config.nodeEnv === 'development' ? {
    target: 'pino-pretty',
    options: {
      colorize: true,
      translateTime: 'SYS:standard',
      ignore: 'pid,hostname'
    }
  } : undefined
});

if (config.nodeEnv === 'production') {
  // Pino does not run formatters.bindings for every child binding. Do not allow
  // child/setBindings to bypass the payload-free Production boundary.
  Object.defineProperties(logger, {
    child: { value: () => logger, writable: false, configurable: false },
    setBindings: { value: () => {}, writable: false, configurable: false }
  });
}

/**
 * Create child logger with context
 */
export function createLogger(context: string) {
  return logger.child({ context });
}
