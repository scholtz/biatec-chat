import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:biatec_authenticator/channel/device_channel.dart';
import 'package:biatec_authenticator/push/device_event.dart';
import 'package:fake_async/fake_async.dart';
import 'package:flutter_test/flutter_test.dart';

const _rs = '\x1e';
String _frame(Map<String, Object?> m) => '${jsonEncode(m)}$_rs';
String _invoke(String target, Object? arg) =>
    _frame({'type': 1, 'target': target, 'arguments': [arg]});

/// A hub server on loopback that speaks the SignalR JSON framing, for the channel to connect to for real.
class TestHub {
  late final HttpServer _server;
  final tickets = <String>[];

  /// What each connection's client sent, text by text.
  final clientSent = <List<String>>[];
  final connections = <WebSocket>[];
  final _opened = StreamController<int>.broadcast();

  /// Run for each connection after the handshake answer went out.
  FutureOr<void> Function(WebSocket ws, int index)? afterHandshake;

  /// What to answer to the handshake; `{}` by default.
  String handshakeAnswer = '{}$_rs';

  Future<void> start() async {
    _server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    _server.listen((req) async {
      if (req.uri.path != '/hubs/device') {
        req.response.statusCode = 404;
        await req.response.close();
        return;
      }
      tickets.add(req.uri.queryParameters['access_token'] ?? '');
      final ws = await WebSocketTransformer.upgrade(req);
      final index = connections.length;
      connections.add(ws);
      clientSent.add([]);
      _opened.add(index);
      var handshaken = false;
      ws.listen((data) async {
        clientSent[index].add(data as String);
        if (!handshaken) {
          handshaken = true;
          try {
            ws.add(handshakeAnswer);
            await afterHandshake?.call(ws, index);
          } on StateError {
            // The client already went away.
          }
        }
      }, onError: (_) {}, onDone: () {});
    });
  }

  Uri get hubUri =>
      Uri.parse('ws://127.0.0.1:${_server.port}/mock-oidc/hubs/device')
          .replace(path: '/hubs/device');

  Future<void> connected(int count) async {
    while (connections.length < count) {
      await _opened.stream.first.timeout(const Duration(seconds: 5));
    }
  }

  Future<void> stop() async {
    for (final c in connections) {
      await c.close();
    }
    await _server.close(force: true);
  }
}

/// A channel against [hub]: a new ticket (`t1`, `t2`, ...) every time one is asked for.
DeviceChannel channelFor(TestHub hub,
    {Duration pingEvery = const Duration(seconds: 15),
    List<Duration> backoff = const [Duration(milliseconds: 30)],
    Future<bool> Function()? enrolled}) {
  var n = 0;
  return DeviceChannel(
    hubUri: hub.hubUri,
    ticket: () async => 't${++n}',
    enrolled: enrolled ?? () async => true,
    pingEvery: pingEvery,
    backoff: backoff,
    random: _Fixed(0),
  );
}

class _Fixed implements Random {
  _Fixed(this.v);
  final double v;
  @override
  bool nextBool() => v > 0.5;
  @override
  double nextDouble() => v;
  @override
  int nextInt(int max) => (v * max).floor();
}

Future<List<DeviceEvent>> collect(DeviceChannel c, int count) async {
  final out = <DeviceEvent>[];
  final done = Completer<void>();
  final sub = c.events.listen((e) {
    out.add(e);
    if (out.length >= count && !done.isCompleted) done.complete();
  });
  try {
    await done.future.timeout(const Duration(seconds: 5));
  } finally {
    await sub.cancel();
  }
  return out;
}

void main() {
  // The test binding blocks real sockets; the hub is on loopback.
  TestWidgetsFlutterBinding.ensureInitialized();
  HttpOverrides.global = null;

  group('against a local hub', () {
    late TestHub hub;
    setUp(() async {
      hub = TestHub();
      await hub.start();
    });
    tearDown(() => hub.stop());

    test('the handshake is sent, and an invocation reaches the listener',
        () async {
      hub.afterHandshake = (ws, _) =>
          ws.add(_invoke('consent_request', {'auth_req_id': 'ciba_1'}));
      final channel = channelFor(hub);
      final events = collect(channel, 1);
      await channel.start();
      expect(await events, [const ConsentRequested('ciba_1')]);
      expect(hub.clientSent.single.first, '{"protocol":"json","version":1}$_rs');
      // The ticket is in the URL, and only there.
      expect(hub.tickets, ['t1']);
      await channel.dispose();
    });

    test('all four events are delivered', () async {
      hub.afterHandshake = (ws, _) {
        ws.add(_invoke('consent_request', {'auth_req_id': 'a'}));
        ws.add(_invoke('consent_closed', {'auth_req_id': 'a'}));
        ws.add(_invoke('device_enrolled', {'kid': 'phone-2'}));
        ws.add(_invoke('settings_changed', <String, Object?>{}));
      };
      final channel = channelFor(hub);
      final events = collect(channel, 4);
      await channel.start();
      expect(await events, const [
        ConsentRequested('a'),
        ConsentClosed('a'),
        DeviceEnrolled('phone-2'),
        SettingsChanged(),
      ]);
      await channel.dispose();
    });

    test('two messages in one frame are both delivered, in order', () async {
      hub.afterHandshake = (ws, _) => ws.add(
          _invoke('consent_request', {'auth_req_id': 'a1'}) +
              _invoke('consent_request', {'auth_req_id': 'a2'}));
      final channel = channelFor(hub);
      final events = collect(channel, 2);
      await channel.start();
      expect(await events,
          [const ConsentRequested('a1'), const ConsentRequested('a2')]);
      await channel.dispose();
    });

    test('the handshake answer and an invocation may share a frame', () async {
      hub.handshakeAnswer =
          '{}$_rs${_invoke('consent_request', {'auth_req_id': 'a1'})}';
      final channel = channelFor(hub);
      final events = collect(channel, 1);
      await channel.start();
      expect(await events, [const ConsentRequested('a1')]);
      await channel.dispose();
    });

    test('a message cut across two frames is put together', () async {
      final whole = _invoke('consent_request', {'auth_req_id': 'a1'});
      hub.afterHandshake = (ws, _) async {
        ws.add(whole.substring(0, 20));
        await Future<void>.delayed(const Duration(milliseconds: 30));
        ws.add(whole.substring(20));
      };
      final channel = channelFor(hub);
      final events = collect(channel, 1);
      await channel.start();
      expect(await events, [const ConsentRequested('a1')]);
      await channel.dispose();
    });

    test('a ping is answered with a ping', () async {
      final answered = Completer<void>();
      hub.afterHandshake = (ws, _) => ws.add(_frame({'type': 6}));
      final channel = channelFor(hub);
      await channel.start();
      await hub.connected(1);
      // Wait for the client's answer.
      for (var i = 0; i < 100 && !answered.isCompleted; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 20));
        if (hub.clientSent.single.contains(_frame({'type': 6}))) {
          answered.complete();
        }
      }
      expect(answered.isCompleted, isTrue);
      await channel.dispose();
    });

    test('the client pings on its own every interval', () async {
      final channel = channelFor(hub, pingEvery: const Duration(milliseconds: 60));
      await channel.start();
      await hub.connected(1);
      await Future<void>.delayed(const Duration(milliseconds: 400));
      final pings =
          hub.clientSent.single.where((m) => m == _frame({'type': 6})).length;
      expect(pings, greaterThanOrEqualTo(3));
      await channel.dispose();
    });

    test('the client never sends an invocation', () async {
      hub.afterHandshake = (ws, _) {
        ws.add(_invoke('consent_request', {'auth_req_id': 'a1'}));
        ws.add(_frame({'type': 6}));
      };
      final channel = channelFor(hub, pingEvery: const Duration(milliseconds: 40));
      await channel.start();
      await Future<void>.delayed(const Duration(milliseconds: 300));
      for (final text in hub.clientSent.single.skip(1)) {
        for (final part in text.split(_rs).where((p) => p.isNotEmpty)) {
          expect((jsonDecode(part) as Map)['type'], 6, reason: part);
        }
      }
      await channel.dispose();
    });

    test('a close that allows it reconnects, with a new ticket', () async {
      hub.afterHandshake = (ws, i) async {
        if (i == 0) {
          ws.add(_frame({'type': 7, 'allowReconnect': true}));
          await ws.close();
        } else {
          ws.add(_invoke('consent_request', {'auth_req_id': 'second'}));
        }
      };
      final channel = channelFor(hub);
      final events = collect(channel, 1);
      await channel.start();
      expect(await events, [const ConsentRequested('second')]);
      expect(hub.tickets, ['t1', 't2'],
          reason: 'a ticket is single use: one for every connection');
      await channel.dispose();
    });

    test('a dropped socket reconnects, with a new ticket', () async {
      hub.afterHandshake = (ws, i) async {
        if (i == 0) await ws.close();
      };
      final channel = channelFor(hub);
      await channel.start();
      await hub.connected(2);
      expect(hub.tickets, ['t1', 't2']);
      await channel.dispose();
    });

    test('a close that does not allow it ends the connection for good',
        () async {
      hub.afterHandshake = (ws, i) async {
        ws.add(_frame({'type': 7, 'allowReconnect': false}));
      };
      final channel = channelFor(hub);
      await channel.start();
      await hub.connected(1);
      await Future<void>.delayed(const Duration(milliseconds: 400));
      expect(hub.connections, hasLength(1));
      expect(hub.tickets, ['t1']);
      await channel.dispose();
    });

    test('a handshake the server refuses is tried again later', () async {
      hub.handshakeAnswer = '{"error":"no"}$_rs';
      final channel = channelFor(hub);
      await channel.start();
      await hub.connected(2);
      expect(hub.tickets.length, greaterThanOrEqualTo(2));
      await channel.dispose();
    });

    test('unknown targets and message types are ignored', () async {
      hub.afterHandshake = (ws, _) {
        ws.add(_invoke('wipe_everything', {'auth_req_id': 'x'}));
        ws.add(_frame({'type': 3, 'invocationId': '1', 'result': 1}));
        ws.add(_frame({'type': 99}));
        ws.add('not json$_rs');
        ws.add(_frame({'type': 1, 'arguments': []}));
        ws.add(_invoke('consent_request', {'auth_req_id': 'real'}));
      };
      final channel = channelFor(hub);
      final events = collect(channel, 1);
      await channel.start();
      expect(await events, [const ConsentRequested('real')]);
      await channel.dispose();
    });

    test('a malformed auth_req_id is ignored', () async {
      hub.afterHandshake = (ws, _) {
        ws.add(_invoke('consent_request', {'auth_req_id': '../../etc'}));
        ws.add(_invoke('consent_request', {'auth_req_id': 'a' * 200}));
        ws.add(_invoke('consent_request', {'auth_req_id': 7}));
        ws.add(_invoke('consent_closed', {'other': 'x'}));
        ws.add(_invoke('consent_request', {'auth_req_id': 'fine_1'}));
      };
      final channel = channelFor(hub);
      final events = collect(channel, 1);
      await channel.start();
      expect(await events, [const ConsentRequested('fine_1')]);
      await channel.dispose();
    });

    test('no connection is made while the device is not enrolled', () async {
      var enrolled = false;
      final wake = StreamController<void>.broadcast();
      var asked = 0;
      final channel = DeviceChannel(
        hubUri: hub.hubUri,
        ticket: () async => 't${++asked}',
        enrolled: () async => enrolled,
        wake: wake.stream,
        random: _Fixed(0),
      );
      await channel.start();
      await Future<void>.delayed(const Duration(milliseconds: 200));
      expect(asked, 0);
      expect(hub.connections, isEmpty);
      enrolled = true;
      wake.add(null);
      await hub.connected(1);
      expect(asked, 1);
      await channel.dispose();
      await wake.close();
    });

    test('a failed ticket is not a connection, and the URL is never in an error',
        () async {
      final channel = DeviceChannel(
        hubUri: hub.hubUri,
        ticket: () async => throw StateError('no ticket'),
        enrolled: () async => true,
        backoff: const [Duration(milliseconds: 20)],
        random: _Fixed(0),
      );
      await channel.start();
      await Future<void>.delayed(const Duration(milliseconds: 150));
      expect(hub.connections, isEmpty);
      await channel.dispose();
    });
  });

  group('hubUriFor', () {
    test('turns https into wss and http into ws, under the base path', () {
      expect(DeviceChannel.hubUriFor('https://oidc.example').toString(),
          'wss://oidc.example/hubs/device');
      expect(DeviceChannel.hubUriFor('https://oidc.example/').toString(),
          'wss://oidc.example/hubs/device');
      expect(
          DeviceChannel.hubUriFor('http://10.0.2.2:5080/mock-oidc').toString(),
          'ws://10.0.2.2:5080/mock-oidc/hubs/device');
    });
  });

  group('timing, with a fake clock', () {
    final epoch = DateTime.utc(2026, 11, 14);

    /// A socket that is already open and answers the handshake, then lives for [lives] (forever when null).
    FakeHubSocket socketThatLives(FakeAsync fa, Duration? lives) {
      final s = FakeHubSocket();
      if (lives != null) fa.run((_) => Timer(lives, s.closeFromServer));
      return s;
    }

    test('the delay between attempts is 1, 2, 5, 10, then 30 seconds',
        () {
      fakeAsync((fa) {
        final asked = <int>[];
        final channel = DeviceChannel(
          hubUri: Uri.parse('wss://oidc.example/hubs/device'),
          ticket: () async {
            asked.add(fa.elapsed.inSeconds);
            throw StateError('no network');
          },
          enrolled: () async => true,
          random: _Fixed(0),
          now: () => epoch.add(fa.elapsed),
        );
        channel.start();
        fa.elapse(const Duration(seconds: 100));
        expect(asked, [0, 1, 3, 8, 18, 48, 78]);
        channel.stop();
        fa.flushMicrotasks();
      });
    });

    test('the jitter adds at most 20 percent', () {
      fakeAsync((fa) {
        final asked = <Duration>[];
        final channel = DeviceChannel(
          hubUri: Uri.parse('wss://oidc.example/hubs/device'),
          ticket: () async {
            asked.add(fa.elapsed);
            throw StateError('no network');
          },
          enrolled: () async => true,
          random: _Fixed(0.999999),
          now: () => epoch.add(fa.elapsed),
        );
        channel.start();
        fa.elapse(const Duration(seconds: 40));
        for (var i = 1; i < asked.length; i++) {
          final gap = asked[i] - asked[i - 1];
          final base = DeviceChannel.defaultBackoff[
              min(i - 1, DeviceChannel.defaultBackoff.length - 1)];
          expect(gap, greaterThanOrEqualTo(base));
          expect(gap.inMilliseconds, lessThanOrEqualTo(base.inMilliseconds * 1.2 + 1));
        }
        channel.stop();
        fa.flushMicrotasks();
      });
    });

    test('a connection that lived 60 seconds starts the backoff again',
        () {
      fakeAsync((fa) {
        final asked = <int>[];
        var n = 0;
        final channel = DeviceChannel(
          hubUri: Uri.parse('wss://oidc.example/hubs/device'),
          ticket: () async {
            asked.add(fa.elapsed.inSeconds);
            return 't${++n}';
          },
          enrolled: () async => true,
          random: _Fixed(0),
          now: () => epoch.add(fa.elapsed),
          silenceLimit: const Duration(minutes: 10),
          connector: (uri) {
            // Two attempts fail, the third connects and lives 61 seconds.
            if (n <= 2) return FakeHubSocket(failToOpen: true);
            return socketThatLives(fa, const Duration(seconds: 61));
          },
        );
        channel.start();
        fa.elapse(const Duration(seconds: 3)); // attempts at 0 and 1; the third at 3
        fa.flushMicrotasks();
        expect(asked, [0, 1, 3]);
        // Connected at 3, closed by the server at 64, and asked again 1 second later (not 5).
        fa.elapse(const Duration(seconds: 62));
        expect(asked, [0, 1, 3, 65]);
        channel.stop();
        fa.flushMicrotasks();
      });
    });

    test('a connection that did not last keeps the backoff growing', () {
      fakeAsync((fa) {
        final asked = <int>[];
        final channel = DeviceChannel(
          hubUri: Uri.parse('wss://oidc.example/hubs/device'),
          ticket: () async {
            asked.add(fa.elapsed.inSeconds);
            return 't';
          },
          enrolled: () async => true,
          random: _Fixed(0),
          now: () => epoch.add(fa.elapsed),
          connector: (uri) => socketThatLives(fa, const Duration(seconds: 10)),
        );
        channel.start();
        fa.elapse(const Duration(seconds: 60));
        // 0: connect, closed at 10, then 1 s; 11: closed at 21, then 2 s; 23: closed at 33, then 5 s; 38: ...
        expect(asked.take(4).toList(), [0, 11, 23, 38]);
        channel.stop();
        fa.flushMicrotasks();
      });
    });

    test('no attempt is made while there is no network, and one when it returns',
        () {
      fakeAsync((fa) {
        final net = FakeNetwork(online: false);
        var asked = 0;
        final channel = DeviceChannel(
          hubUri: Uri.parse('wss://oidc.example/hubs/device'),
          ticket: () async {
            asked++;
            throw StateError('stop here');
          },
          enrolled: () async => true,
          network: net,
          random: _Fixed(0),
          now: () => epoch.add(fa.elapsed),
        );
        channel.start();
        fa.elapse(const Duration(minutes: 30));
        expect(asked, 0);
        net.set(true);
        fa.elapse(const Duration(milliseconds: 10));
        expect(asked, 1);
        channel.stop();
        fa.flushMicrotasks();
      });
    });

    test('a server that goes silent is given up on', () {
      fakeAsync((fa) {
        var n = 0;
        final sockets = <FakeHubSocket>[];
        final channel = DeviceChannel(
          hubUri: Uri.parse('wss://oidc.example/hubs/device'),
          ticket: () async => 't${++n}',
          enrolled: () async => true,
          random: _Fixed(0),
          now: () => epoch.add(fa.elapsed),
          silenceLimit: const Duration(seconds: 40),
          connector: (uri) {
            final s = FakeHubSocket();
            sockets.add(s);
            return s;
          },
        );
        channel.start();
        fa.elapse(const Duration(seconds: 39));
        expect(sockets, hasLength(1));
        fa.elapse(const Duration(seconds: 3));
        expect(sockets.first.closedByClient, isTrue);
        fa.elapse(const Duration(seconds: 2));
        expect(sockets, hasLength(2));
        channel.stop();
        fa.flushMicrotasks();
      });
    });

    test('stopping closes the socket and makes no further attempts', () {
      fakeAsync((fa) {
        var n = 0;
        final sockets = <FakeHubSocket>[];
        final channel = DeviceChannel(
          hubUri: Uri.parse('wss://oidc.example/hubs/device'),
          ticket: () async => 't${++n}',
          enrolled: () async => true,
          random: _Fixed(0),
          now: () => epoch.add(fa.elapsed),
          connector: (uri) {
            final s = FakeHubSocket();
            sockets.add(s);
            return s;
          },
        );
        channel.start();
        fa.elapse(const Duration(seconds: 1));
        channel.stop();
        fa.elapse(const Duration(minutes: 5));
        expect(sockets, hasLength(1));
        expect(sockets.single.closedByClient, isTrue);
        expect(n, 1);
      });
    });

    test('the ticket goes in the URL of the connection, and only there', () {
      fakeAsync((fa) {
        final urls = <Uri>[];
        var n = 0;
        final channel = DeviceChannel(
          hubUri: Uri.parse('wss://oidc.example/hubs/device'),
          ticket: () async => 'secret-${++n}',
          enrolled: () async => true,
          random: _Fixed(0),
          now: () => epoch.add(fa.elapsed),
          connector: (uri) {
            urls.add(uri);
            return FakeHubSocket(failToOpen: true);
          },
        );
        channel.start();
        fa.elapse(const Duration(seconds: 4));
        expect(urls.map((u) => u.queryParameters['access_token']),
            ['secret-1', 'secret-2', 'secret-3']);
        expect(urls.first.path, '/hubs/device');
        channel.stop();
        fa.flushMicrotasks();
      });
    });
  });
}

class FakeNetwork implements NetworkStatus {
  FakeNetwork({required bool online}) : _online = online;
  bool _online;
  final _changes = StreamController<bool>.broadcast();

  void set(bool v) {
    _online = v;
    _changes.add(v);
  }

  @override
  Future<bool> get online async => _online;
  @override
  Stream<bool> get changes => _changes.stream;
}

/// An in-memory [HubSocket] that answers the handshake with `{}`.
class FakeHubSocket implements HubSocket {
  FakeHubSocket({this.failToOpen = false});
  final bool failToOpen;
  final _in = StreamController<String>();
  final sent = <String>[];
  bool closedByClient = false;

  void closeFromServer() => _in.close();
  void serverSends(String text) => _in.add(text);

  @override
  Future<void> get ready =>
      failToOpen ? Future.error(StateError('refused')) : Future.value();
  @override
  Stream<String> get messages => _in.stream;
  @override
  void send(String text) {
    sent.add(text);
    if (text.startsWith('{"protocol"')) _in.add('{}$_rs');
  }

  @override
  Future<void> close() async {
    closedByClient = true;
    if (!_in.isClosed) unawaited(_in.close());
  }
}
