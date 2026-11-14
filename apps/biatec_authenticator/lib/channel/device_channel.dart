import 'dart:async';
import 'dart:convert';
import 'dart:math';

import 'package:flutter/foundation.dart' show kDebugMode;
import 'package:web_socket_channel/io.dart';

import '../config.dart';
import '../push/device_event.dart';
import '../push/device_event_source.dart';
import '../security/pinning/pinned_http.dart';

/// One WebSocket, as text messages. The seam the channel is tested through; [connectHubSocket] is the real one.
abstract class HubSocket {
  /// Completes when the socket is open; throws when it could not be opened.
  Future<void> get ready;

  /// Text messages. Binary messages are dropped. Ends when the socket closes; an error ends it too.
  Stream<String> get messages;
  void send(String text);
  /// Closes the socket. The channel closes through its own guard, so an implementation need not time out or swallow errors.
  Future<void> close();
}

typedef HubConnector = HubSocket Function(Uri uri);

class _IoHubSocket implements HubSocket {
  _IoHubSocket(this._channel);
  final IOWebSocketChannel _channel;

  @override
  Future<void> get ready => _channel.ready;
  @override
  Stream<String> get messages =>
      _channel.stream.where((m) => m is String).cast<String>();
  @override
  void send(String text) => _channel.sink.add(text);
  @override
  // Never throws or waits for ever is guaranteed by the one caller, DeviceChannel._closeQuietly.
  Future<void> close() => _channel.sink.close();
}

/// Opens the real WebSocket. `wss` goes through the pinned client (no system authorities, a certificate must match a pin); plain `ws` is for a developer's machine in a debug build only, like the HTTPS calls.
HubSocket connectHubSocket(Uri uri) {
  if (uri.scheme == 'ws') {
    if (!kDebugMode || !AppConfig.localHosts.contains(uri.host)) {
      throw StateError('plain ws is for a local debug build only');
    }
  } else if (uri.scheme != 'wss') {
    throw StateError('not a WebSocket URL');
  }
  return _IoHubSocket(IOWebSocketChannel.connect(uri,
      customClient: PinnedHttp.webSocketClient(),
      connectTimeout: const Duration(seconds: 15)));
}

/// Thrown by the ticket function when Biatec OIDC refuses this device for good (401 or 403: the device was revoked or cancelled). The channel stops
/// and makes no attempts until the enrollment or the network changes, the app starts again, or [DeviceChannel.stoppedRetry] has passed (a refusal can be a wrong clock, which a user fixes).
class ChannelRefused implements Exception {
  const ChannelRefused([this.status]);

  /// The HTTP status that refused the ticket, when there was one.
  final int? status;
}

/// Whether the phone has a network, so no attempt is made while it has none.
abstract class NetworkStatus {
  Future<bool> get online;

  /// Emits each change of [online].
  Stream<bool> get changes;
}

/// The device channel to Biatec OIDC: a minimal client of the SignalR JSON hub protocol over a WebSocket, server to client only.
///
/// 1. A single-use ticket is fetched (`POST /devices/hub-ticket`, signed with the device token and the device proof) for every connection.
/// 2. `wss://<oidc>/hubs/device?access_token=<ticket>` is opened with negotiation skipped. The ticket is in that URL, which is never logged and never put in an error.
/// 3. The handshake `{"protocol":"json","version":1}` goes out and `{}` must come back; every later message is JSON ended by the record separator 0x1e, and a frame can hold several.
/// 4. Invocations (type 1) become [DeviceEvent]s; pings (type 6) are answered and sent every [pingEvery]; a close (type 7) ends the connection, and the channel reconnects unless the server said `allowReconnect: false`. Unknown types and targets are ignored. The client never sends an invocation.
/// 5. After a connection ends it reconnects after 1, 2, 5, 10, then 30 seconds (plus up to 20 percent jitter), starting again at 1 second after a connection that lived [stableAfter]. There are no attempts while the phone has no network or the device is not enrolled.
///
/// Nothing can wake an app that is not running: this channel only exists while the process does (the app in front, or the Android service the user turned on).
class DeviceChannel implements DeviceEventSource {
  DeviceChannel({
    required this.hubUri,
    required this.ticket,
    required this.enrolled,
    this.network,
    this.wake,
    HubConnector? connector,
    Random? random,
    DateTime Function()? now,
    this.pingEvery = const Duration(seconds: 15),
    this.silenceLimit = const Duration(seconds: 45),
    this.handshakeTimeout = const Duration(seconds: 15),
    this.stableAfter = const Duration(seconds: 60),
    this.backoff = defaultBackoff,
    this.stoppedRetry = const Duration(minutes: 15),
  })  : _connector = connector ?? connectHubSocket,
        _random = random ?? Random(),
        _now = now ?? DateTime.now;

  /// The delays before the next attempt, by consecutive failures; the last one repeats. 1, 2, 5, 10 and 30 seconds.
  static const defaultBackoff = [
    Duration(seconds: 1),
    Duration(seconds: 2),
    Duration(seconds: 5),
    Duration(seconds: 10),
    Duration(seconds: 30),
  ];

  /// The hub address for Biatec OIDC at [baseUrl] (`https` becomes `wss`, `http` becomes `ws`).
  static Uri hubUriFor(String baseUrl) {
    final base = Uri.parse(baseUrl.replaceAll(RegExp(r'/+$'), ''));
    return base.replace(
        scheme: base.scheme == 'https' ? 'wss' : 'ws',
        path: '${base.path}/hubs/device');
  }

  /// The most that is kept of a message that has not ended yet.
  static const maxBuffered = 16 * 1024;

  /// Without a query: the ticket is added per connection.
  final Uri hubUri;

  /// A fresh ticket, asked for before every connection. Throws when none can be had.
  final Future<String> Function() ticket;

  /// False while this device is not enrolled: nothing is asked for or opened then.
  final Future<bool> Function() enrolled;
  final NetworkStatus? network;

  /// Anything that should make a waiting channel look again (the enrollment changed).
  final Stream<void>? wake;
  final HubConnector _connector;
  final Random _random;
  final DateTime Function() _now;
  final Duration pingEvery;
  final Duration silenceLimit;
  final Duration handshakeTimeout;
  final Duration stableAfter;

  /// The delay before the next attempt after n consecutive failures; the last one repeats.
  final List<Duration> backoff;

  /// How long the channel stays stopped after a refused ticket or a close that forbids reconnecting, before it tries again by itself.
  final Duration stoppedRetry;

  final _events = StreamController<DeviceEvent>.broadcast();
  final _subs = <StreamSubscription<Object?>>[];
  bool _running = false;
  int _generation = 0;
  HubSocket? _socket;

  /// Counts wake-ups. A sleep started with an older count than this returns at once, so a wake that came in before the sleep began is not lost.
  int _wakes = 0;

  /// Counts changes of the enrollment: a session or ticket that began before one is never used.
  int _enrollmentChanges = 0;
  _Sleep? _sleeping;
  DateTime? _lastSkip;

  @override
  Stream<DeviceEvent> get events => _events.stream;

  @override
  Future<void> start() async {
    if (_running) return;
    _running = true;
    final generation = ++_generation;
    final w = wake;
    if (w != null) {
      _subs.add(w.listen((_) {
        // The enrollment changed (unenrolled, erased, enrolled again): a live connection was authorised for the old one, so it ends now.
        _enrollmentChanges++;
        unawaited(_closeSocket());
        _wake();
      }));
    }
    final n = network;
    if (n != null) {
      _subs.add(n.changes.listen((online) {
        // Going offline ends the connection now; there is no attempt until the network is back.
        if (!online) _closeSocket();
        _wake();
      }));
    }
    unawaited(_run(generation));
  }

  @override
  Future<void> stop() async {
    if (!_running) return;
    _running = false;
    _generation++;
    for (final s in _subs) {
      unawaited(s.cancel());
    }
    _subs.clear();
    _wake();
    await _closeSocket();
  }

  /// Ends the channel for good.
  Future<void> dispose() async {
    await stop();
    await _events.close();
  }

  bool _alive(int generation) => _running && generation == _generation;

  Future<void> _closeSocket() async {
    final s = _socket;
    _socket = null;
    await _closeQuietly(s);
  }

  /// The one place where closing never throws and never waits for ever: a socket that will not close is left behind.
  static Future<void> _closeQuietly(HubSocket? socket) async {
    try {
      await socket?.close().timeout(const Duration(seconds: 2));
    } on Object {
      // Nothing more to do with it.
    }
  }

  void _wake() {
    _wakes++;
    _sleeping?.end();
  }

  /// Sleeps for [d], or until woken; with a null [d] until woken. [seen] is the wake count the caller saw before it decided to sleep.
  Future<void> _sleep(int seen, Duration? d) {
    if (seen != _wakes) {
      // A wake came in after the caller looked: do not wait. At most once a second, so wakes that keep coming cannot make a loop that never rests.
      const gap = Duration(seconds: 1);
      final now = _now();
      final last = _lastSkip;
      if (last == null || now.difference(last) >= gap) {
        _lastSkip = now;
        return Future.value();
      }
      // Throttled: the wake is not dropped. Wait only what is left of the second, then the caller looks again.
      d = d == null || d > gap - now.difference(last) ? gap - now.difference(last) : d;
    }
    final sleep = _Sleep();
    _sleeping = sleep;
    if (d != null) sleep.timer = Timer(d, sleep.end);
    // Only this sleep's own record is cleared: an older sleep finishing late cannot remove a newer one.
    return sleep.done.future.whenComplete(() {
      if (identical(_sleeping, sleep)) _sleeping = null;
    });
  }

  Future<bool> _ready() async {
    try {
      if (!await enrolled()) return false;
      return await (network?.online ?? Future.value(true));
    } on Object {
      return false;
    }
  }

  Future<void> _run(int generation) async {
    var failures = 0;
    while (_alive(generation)) {
      // The wake count before anything of this round starts: a wake that arrives during the ticket request or the connection is not lost.
      final seen = _wakes;
      if (!await _ready()) {
        // Not enrolled or offline: wait for a change, make no attempt, and do not count it as a failure.
        failures = 0;
        await _sleep(seen, const Duration(minutes: 5));
        continue;
      }
      final end = await _session(generation);
      if (!_alive(generation)) return;
      if (end.connectedFor >= stableAfter) failures = 0;
      if (!end.allowReconnect) {
        // The server asked for the end: wait for the enrollment or the network to change (or a new start) instead of asking again.
        failures = 0;
        await _sleep(seen, stoppedRetry);
        continue;
      }
      final base = backoff[min(failures, backoff.length - 1)];
      failures++;
      final jitter = base.inMilliseconds * 0.2 * _random.nextDouble();
      await _sleep(seen, base + Duration(milliseconds: jitter.round()));
    }
  }

  /// One connection, from ticket to close. Never throws and never reports the URL.
  Future<_End> _session(int generation) async {
    final end = Completer<_End>();
    DateTime? connectedAt;
    Timer? pingTimer;
    Timer? silenceTimer;
    Timer? handshakeTimer;
    StreamSubscription<String>? sub;
    HubSocket? socket;
    var buffer = '';
    var handshaken = false;
    final epoch = _enrollmentChanges;

    void finish({bool allowReconnect = true}) {
      if (end.isCompleted) return;
      final at = connectedAt;
      end.complete(_End(at == null ? Duration.zero : _now().difference(at),
          allowReconnect));
    }

    void sendFrame(Map<String, Object?> message) {
      try {
        socket?.send('${jsonEncode(message)}\x1e');
      } on Object {
        finish();
      }
    }

    void touch() {
      silenceTimer?.cancel();
      silenceTimer = Timer(silenceLimit, finish);
    }

    void handle(String text) {
      Object? decoded;
      try {
        decoded = jsonDecode(text);
      } on Object {
        return; // not JSON: nothing to do with it
      }
      if (decoded is! Map) return;
      if (!handshaken) {
        // The answer to the handshake: `{}`, or `{"error": ...}` when the server refuses the protocol.
        if (decoded['error'] != null) return finish();
        handshaken = true;
        handshakeTimer?.cancel();
        connectedAt = _now();
        pingTimer = Timer.periodic(pingEvery, (_) => sendFrame({'type': 6}));
        touch();
        return;
      }
      switch (decoded['type']) {
        case 1:
          final target = decoded['target'];
          final args = decoded['arguments'];
          if (target is! String || args is! List) return;
          final event = parseDeviceEvent(target, args.cast<Object?>());
          if (event != null && !_events.isClosed) _events.add(event);
        case 6:
          sendFrame({'type': 6});
        case 7:
          // Reconnect unless the server said, in so many words, that it must not be done.
          finish(allowReconnect: decoded['allowReconnect'] != false);
        // Unknown types (completions, stream items, acknowledgements) are ignored: this client only listens.
      }
    }

    try {
      final t = await ticket();
      // The enrollment changed while the ticket was asked for: it belongs to the old one.
      if (!_alive(generation) || epoch != _enrollmentChanges) {
        return const _End(Duration.zero, true);
      }
      socket = _connector(hubUri.replace(queryParameters: {'access_token': t}));
      _socket = socket;
      await socket.ready;
      if (!_alive(generation)) {
        await _closeQuietly(socket);
        return const _End(Duration.zero, true);
      }
      sub = socket.messages.listen((text) {
        touch();
        buffer += text;
        final parts = buffer.split('\x1e');
        buffer = parts.removeLast(); // a message cut short waits for its end
        // A server that never ends a record is not one to listen to.
        if (buffer.length > maxBuffered) finish();
        for (final part in parts) {
          if (part.isEmpty || end.isCompleted) continue;
          handle(part);
        }
      }, onError: (Object _) => finish(), onDone: finish);
      handshakeTimer = Timer(handshakeTimeout, finish);
      socket.send('{"protocol":"json","version":1}\x1e');
      return await end.future;
    } on ChannelRefused {
      // The device is revoked or cancelled: retrying cannot help.
      return const _End(Duration.zero, false);
    } on Object {
      // Only the kind of failure is kept: the exception text of a failed connect can hold the URL, and the URL holds the ticket.
      return _End(
          connectedAt == null ? Duration.zero : _now().difference(connectedAt!),
          true);
    } finally {
      pingTimer?.cancel();
      silenceTimer?.cancel();
      handshakeTimer?.cancel();
      unawaited(sub?.cancel());
      if (identical(_socket, socket)) _socket = null;
      await _closeQuietly(socket);
    }
  }
}

class _Sleep {
  final done = Completer<void>();
  Timer? timer;

  void end() {
    timer?.cancel();
    if (!done.isCompleted) done.complete();
  }
}

class _End {
  const _End(this.connectedFor, this.allowReconnect);
  final Duration connectedFor;
  final bool allowReconnect;
}
