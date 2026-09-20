// Host-only type declarations. Upstream runtime algorithms are unchanged.
declare module 'cortico/core/types.ts' {
 export type LogLevel = 'debug' | 'info' | 'warn' | 'error';
 export interface Logger {
  debug(message: string, fields?: Record<string, unknown>): void;
  info(message: string, fields?: Record<string, unknown>): void;
  warn(message: string, fields?: Record<string, unknown>): void;
  error(message: string, fields?: Record<string, unknown>): void;
 }
}
