/**
 * The server→client method names — they ARE the server's `RealtimeChannels`
 * constants (`src/Modules/Realtime/Nabd.Realtime.Contracts`), not a second set of
 * English verbs (P5 spec 03, B10). A typo anywhere is a silently dead channel, so
 * both sides pin the literals in a test.
 */
export const REALTIME_CHANNELS = {
  assetStatus: 'asset-status',
  alerts: 'alerts',
} as const;

export type RealtimeChannel = (typeof REALTIME_CHANNELS)[keyof typeof REALTIME_CHANNELS];
