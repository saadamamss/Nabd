import { REALTIME_CHANNELS } from './realtime.channels';

/**
 * P5 spec 03, B10 — one vocabulary. The server's `RealtimeChannels` constants and
 * this table must be the same two strings; anything else is a channel nobody
 * hears, and it would look green because a handler for a typo never fires.
 */
describe('REALTIME_CHANNELS', () => {
  it('is exactly the server’s two method names', () => {
    expect(REALTIME_CHANNELS.assetStatus).toBe('asset-status');
    expect(REALTIME_CHANNELS.alerts).toBe('alerts');
    expect(Object.keys(REALTIME_CHANNELS).sort()).toEqual(['alerts', 'assetStatus']);
  });
});
