import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { API_BASE } from '../api/api.config';
import { AuthService } from '../auth/auth.service';
import { DashboardSocket, DashboardSocketHandlers } from './dashboard.socket';

/**
 * The reconnect policy is the thing under test (2026-11-24): a dropped socket
 * must not become a per-second `negotiate` storm, and a `stop()`ed connection's
 * late `onclose` must never resurrect it. The fake below reproduces the two
 * SignalR behaviours that matter: `stop()` invokes `onclose`, and a failed
 * `start()` rejects with an HttpError carrying a status code.
 */

const hub = vi.hoisted(() => ({
  builds: [] as unknown[],
  build: (): unknown => {
    throw new Error('no connection factory installed');
  },
}));

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: class {
    withUrl(): this {
      return this;
    }
    withAutomaticReconnect(): this {
      return this;
    }
    build(): unknown {
      const connection = hub.build();
      hub.builds.push(connection);
      return connection;
    }
  },
  HubConnectionState: {
    Disconnected: 'Disconnected',
    Connecting: 'Connecting',
    Connected: 'Connected',
    Disconnecting: 'Disconnecting',
    Reconnecting: 'Reconnecting',
  },
}));

interface FakeConnection {
  state: string;
  start: ReturnType<typeof vi.fn>;
  stop: ReturnType<typeof vi.fn>;
  on: ReturnType<typeof vi.fn>;
  onreconnecting: ReturnType<typeof vi.fn>;
  onreconnected: ReturnType<typeof vi.fn>;
  onclose: ReturnType<typeof vi.fn>;
  succeed: () => void;
  fail: (error: unknown) => void;
  closed: (error?: unknown) => void;
}

function fakeConnection(): FakeConnection {
  let resolve: () => void = () => undefined;
  let reject: (error: unknown) => void = () => undefined;

  const connection: FakeConnection = {
    state: 'Disconnected',
    start: vi.fn(
      () =>
        new Promise<void>((done, failed) => {
          resolve = () => {
            connection.state = 'Connected';
            done();
          };
          reject = failed;
        }),
    ),
    // Real SignalR: stop() closes the connection and invokes onclose.
    stop: vi.fn(() => {
      connection.state = 'Disconnected';
      connection.closed();
      return Promise.resolve();
    }),
    on: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    onclose: vi.fn(),
    succeed: () => resolve(),
    fail: (error) => reject(error),
    closed: (error) => {
      const callback = connection.onclose.mock.calls[0]?.[0] as
        | ((error?: unknown) => void)
        | undefined;
      callback?.(error);
    },
  };

  return connection;
}

function authRefusal(): Error & { statusCode: number } {
  return Object.assign(new Error('Failed to complete negotiation with the server: Unauthorized'), {
    statusCode: 401,
  });
}

/** The handler the socket registered for one channel (a typo'd name throws here). */
function channel(connection: FakeConnection, name: string): (payload: unknown) => void {
  const call = connection.on.mock.calls.find(([registered]) => registered === name);
  if (!call) throw new Error(`no handler registered for "${name}"`);
  return call[1] as (payload: unknown) => void;
}

const HANDLERS: DashboardSocketHandlers = {
  onStatus: vi.fn(),
  onAlert: vi.fn(),
  onResync: vi.fn(),
};

function setup(): {
  socket: DashboardSocket;
  auth: { accessToken: ReturnType<typeof vi.fn>; refresh: ReturnType<typeof vi.fn> };
  latest: () => FakeConnection;
} {
  hub.builds.length = 0;

  const auth = {
    accessToken: vi.fn(() => 'access-token'),
    refresh: vi.fn(() => of(undefined)),
  };

  TestBed.configureTestingModule({
    providers: [
      { provide: API_BASE, useValue: '/api' },
      { provide: AuthService, useValue: auth },
    ],
  });

  const socket = TestBed.inject(DashboardSocket);
  return {
    socket,
    auth,
    latest: () => hub.builds[hub.builds.length - 1] as FakeConnection,
  };
}

/** Lets the start() promise settle without moving the clock. */
const flush = async (): Promise<void> => {
  await vi.advanceTimersByTimeAsync(0);
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.useFakeTimers();
  vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  hub.build = fakeConnection;
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('DashboardSocket', () => {
  it('opens one connection and reports live', async () => {
    const { socket, latest } = setup();

    socket.start(HANDLERS);
    expect(hub.builds).toHaveLength(1);
    expect(socket.state()).toBe('connecting');

    latest().succeed();
    await flush();

    expect(socket.state()).toBe('live');
  });

  it('does not open a second connection while one is starting', () => {
    const { socket } = setup();

    socket.start(HANDLERS);
    socket.start(HANDLERS);

    expect(hub.builds).toHaveLength(1);
  });

  it('subscribes to the two shared channel literals and keeps the alerts envelope whole', async () => {
    const { socket, latest } = setup();
    socket.start(HANDLERS);
    const connection = latest();

    // B10: one vocabulary — the shared constants are the wire, not a second set of verbs.
    expect(connection.on.mock.calls.map(([name]) => name)).toEqual(['asset-status', 'alerts']);

    const row = {
      assetId: 'a1',
      status: 'warning',
      since: '2026-11-24T11:50:00Z',
      alertId: 'alert-1',
      severity: 'warn',
      lastSeenAt: null,
      isActive: true,
    };
    channel(connection, 'asset-status')(row);
    expect(HANDLERS.onStatus).toHaveBeenCalledWith(row);

    // The server sends `{kind, alert}`; the socket must not erase `kind` (spec 05
    // discriminates on it). Unwrapping belongs to the consumer.
    const change = {
      kind: 'changed' as const,
      alert: {
        alertId: 'alert-1',
        assetId: 'a1',
        severity: 'warn',
        status: 'acknowledged',
        since: '2026-11-24T11:50:00Z',
      },
    };
    channel(connection, 'alerts')(change);
    expect(HANDLERS.onAlert).toHaveBeenCalledWith(change);
    expect(HANDLERS.onAlert).toHaveBeenCalledWith(
      expect.objectContaining({ kind: 'changed' }),
    );
  });

  it('marks the connection reconnecting, then live + resync once (the B5 bridge)', async () => {
    const { socket, latest } = setup();
    socket.start(HANDLERS);
    const connection = latest();
    connection.succeed();
    await flush();
    expect(socket.state()).toBe('live');

    connection.onreconnecting.mock.calls[0][0]();
    expect(socket.state()).toBe('reconnecting');
    expect(HANDLERS.onResync).not.toHaveBeenCalled();

    connection.onreconnected.mock.calls[0][0]();
    expect(socket.state()).toBe('live');
    expect(HANDLERS.onResync).toHaveBeenCalledTimes(1);
  });

  it('ignores a stopped connection’s late reconnect callbacks', async () => {
    const { socket, latest } = setup();
    socket.start(HANDLERS);
    const connection = latest();
    socket.stop();

    connection.onreconnected.mock.calls[0][0]();
    expect(HANDLERS.onResync).not.toHaveBeenCalled();
    expect(socket.state()).toBe('stopped');
  });

  it('a fresh mount after stop() starts a fresh backoff streak', async () => {
    const { socket, latest } = setup();
    socket.start(HANDLERS);
    latest().fail(new Error('server unreachable'));
    await flush();
    socket.stop();

    // First failure of the new streak: 1 s ±20 %, not the old streak's next step.
    socket.start(HANDLERS);
    latest().fail(new Error('server unreachable'));
    await flush();

    await vi.advanceTimersByTimeAsync(700);
    expect(hub.builds).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(600);
    expect(hub.builds).toHaveLength(3);
  });

  it('backs off: never retries faster than 1 s, and grows the delay each time', async () => {
    const { socket, latest } = setup();

    socket.start(HANDLERS);
    latest().fail(new Error('server unreachable'));
    await flush();

    // First delay is 1 s ±20 %: nothing before 700 ms, something by 1300 ms.
    await vi.advanceTimersByTimeAsync(700);
    expect(hub.builds).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(600);
    expect(hub.builds).toHaveLength(2);

    latest().fail(new Error('server unreachable'));
    await flush();

    // Second delay is 2 s ±20 %: nothing by 1500 ms.
    await vi.advanceTimersByTimeAsync(1_500);
    expect(hub.builds).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(hub.builds).toHaveLength(3);
  });

  it('bounds a dead server to a handful of attempts per minute (the storm pin)', async () => {
    hub.build = () => {
      const connection = fakeConnection();
      queueMicrotask(() => connection.fail(new Error('server unreachable')));
      return connection;
    };

    const { socket } = setup();
    socket.start(HANDLERS);

    await vi.advanceTimersByTimeAsync(120_000);

    // 1 + 2 + 4 + 8 + 16 + 30 + 30 … — a two-minute outage stays in single digits,
    // where the old fixed 2 s timer produced ~60 negotiate requests a minute.
    expect(hub.builds.length).toBeLessThanOrEqual(10);
    expect(socket.state()).toBe('reconnecting');
  });

  it('stop() ends the retry chain, and the stopped connection\u2019s onclose is inert', async () => {
    const { socket, latest } = setup();

    socket.start(HANDLERS);
    const first = latest();
    first.fail(new Error('server unreachable'));
    await flush();

    socket.stop();
    expect(socket.state()).toBe('stopped');

    // A pending retry must not fire…
    await vi.advanceTimersByTimeAsync(60_000);
    expect(hub.builds).toHaveLength(1);

    // …and neither must the closed connection's own onclose.
    first.closed();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(hub.builds).toHaveLength(1);

    // A fresh page mount still works.
    socket.start(HANDLERS);
    expect(hub.builds).toHaveLength(2);
  });

  it('answers a refused token with one refresh, then pauses instead of hammering', async () => {
    const { socket, auth, latest } = setup();

    socket.start(HANDLERS);
    latest().fail(authRefusal());
    await flush();

    expect(auth.refresh).toHaveBeenCalledTimes(1);
    expect(hub.builds).toHaveLength(2);

    // The dropped connection's own onclose must not add a second attempt.
    await vi.advanceTimersByTimeAsync(5_000);
    expect(hub.builds).toHaveLength(2);

    latest().fail(authRefusal());
    await flush();

    // One refresh per failure streak, never a refresh loop.
    expect(auth.refresh).toHaveBeenCalledTimes(1);
    expect(socket.state()).toBe('stopped');

    await vi.advanceTimersByTimeAsync(120_000);
    expect(hub.builds).toHaveLength(2);
  });

  it('pauses when the refresh itself fails', async () => {
    const { socket, auth, latest } = setup();
    auth.refresh.mockReturnValue(throwError(() => authRefusal()));

    socket.start(HANDLERS);
    latest().fail(authRefusal());
    await flush();

    expect(socket.state()).toBe('stopped');
    await vi.advanceTimersByTimeAsync(60_000);
    expect(hub.builds).toHaveLength(1);
  });
});
