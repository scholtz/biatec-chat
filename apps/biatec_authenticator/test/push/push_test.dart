import 'dart:async';
import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/biometric_gate.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/push/fcm_push_transport.dart';
import 'package:biatec_authenticator/push/push_controller.dart';
import 'package:biatec_authenticator/push/push_payload.dart';
import 'package:biatec_authenticator/push/push_transport.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../approvals/support.dart';

class FakeTransport implements PushTransport {
  final refreshes = StreamController<String>.broadcast();
  final receivedC = StreamController<Map<String, Object?>>.broadcast();
  final openedC = StreamController<Map<String, Object?>>.broadcast();
  String? current = 'push-1';
  Map<String, Object?>? launch;

  @override
  String get provider => 'fcm';
  @override
  Future<String?> token() async => current;
  @override
  Stream<String> get tokenRefreshes => refreshes.stream;
  @override
  Stream<Map<String, Object?>> get received => receivedC.stream;
  @override
  Stream<Map<String, Object?>> get opened => openedC.stream;
  @override
  Future<Map<String, Object?>?> launchPayload() async => launch;
}

class _Pass implements BiometricGate {
  @override
  Future<bool> confirm(String reason) async => true;
}

/// Records `PUT /devices/push` calls; [status] is what it answers.
class FakeRegistry {
  final calls = <Map<String, Object?>>[];
  int status = 200;
  http.Client get client => MockClient((req) async {
        calls.add({
          'method': req.method,
          'path': req.url.path,
          'auth': req.headers['Authorization'],
          'body': jsonDecode(req.body),
        });
        return http.Response('{}', status);
      });
}

EnrollmentRecord record(String token) => EnrollmentRecord(
    kid: 'alice-device', deviceToken: token, activeAt: DateTime.utc(2020));

void main() {
  group('payload', () {
    test('only a plausible auth_req_id is taken; everything else is ignored',
        () {
      expect(authReqIdFromPush({'auth_req_id': 'ciba_0a1b'}), 'ciba_0a1b');
      expect(
          authReqIdFromPush({
            'auth_req_id': 'ciba_0a1b',
            'title': 'Send 5000 EUR',
            'amount': '5000'
          }),
          'ciba_0a1b');
      expect(authReqIdFromPush({}), isNull);
      expect(authReqIdFromPush({'auth_req_id': 42}), isNull);
      expect(authReqIdFromPush({'auth_req_id': ''}), isNull);
      expect(authReqIdFromPush({'auth_req_id': '../devices'}), isNull);
      expect(authReqIdFromPush({'auth_req_id': 'a' * 129}), isNull);
    });
  });

  group('controller', () {
    late FakeTransport transport;
    late FakeRegistry registry;
    late MemoryEnrollmentStore store;
    late List<String> openedIds;
    late PushController push;
    late DateTime now;

    setUp(() {
      transport = FakeTransport();
      registry = FakeRegistry();
      store = MemoryEnrollmentStore();
      openedIds = [];
      now = DateTime.utc(2026, 10, 19, 12);
      push = PushController(
        enrollment: store,
        client: ApprovalClient(baseUrl: 'http://oidc', client: registry.client),
        onOpenRequest: openedIds.add,
        clock: () => now,
      );
    });
    tearDown(() => push.dispose());

    test('registers nothing before enrollment, then once per token', () async {
      await push.start(transport);
      expect(registry.calls, isEmpty);

      await store.write(record('dev-token'));
      await push.registerIfEnrolled();
      await push.registerIfEnrolled();
      expect(registry.calls, [
        {
          'method': 'PUT',
          'path': '/devices/push',
          'auth': 'Device dev-token',
          'body': {'provider': 'fcm', 'token': 'push-1'},
        }
      ]);

      transport.refreshes.add('push-2');
      await pumpEventQueue();
      expect(
          registry.calls.last['body'], {'provider': 'fcm', 'token': 'push-2'});

      // A new enrollment (new device token) registers again.
      await store.write(record('dev-token-2'));
      await push.registerIfEnrolled('push-2');
      expect(registry.calls.last['auth'], 'Device dev-token-2');
      expect(registry.calls, hasLength(3));

      // Sent again after the resend window, in case the server lost it.
      now = now.add(PushController.resendAfter);
      await push.registerIfEnrolled();
      expect(registry.calls, hasLength(4));
    });

    test('nothing happens before a transport is attached', () async {
      await store.write(record('dev-token'));
      await push.registerIfEnrolled();
      expect(registry.calls, isEmpty);
    });

    test('navigation registers a device enrolled after start', () async {
      await push.start(transport);
      expect(registry.calls, isEmpty);
      await store.write(record('dev-token'));
      final route = MaterialPageRoute<void>(builder: (_) => const SizedBox());
      push.didPop(route, null);
      await pumpEventQueue();
      expect(registry.calls, hasLength(1));
    });

    test('a refused registration is retried on the next call', () async {
      await store.write(record('dev-token'));
      registry.status = 503;
      await push.start(transport);
      registry.status = 200;
      await push.registerIfEnrolled();
      await push.registerIfEnrolled();
      expect(registry.calls, hasLength(2));
    });

    test('no token (declined notifications) registers nothing', () async {
      await store.write(record('dev-token'));
      transport.current = null;
      await push.start(transport);
      expect(registry.calls, isEmpty);
    });

    test('tap and launch payloads open the request; junk is ignored', () async {
      transport.launch = {'auth_req_id': 'ciba_launch'};
      await push.start(transport);
      transport.openedC.add({'auth_req_id': 'ciba_tap', 'body': 'Send 1 BTC'});
      transport.openedC.add({'title': 'no id'});
      await pumpEventQueue();
      expect(openedIds, ['ciba_launch', 'ciba_tap']);
    });

    test('a foreground push signals the pending list, without opening it',
        () async {
      await push.start(transport);
      var arrived = 0;
      push.requestArrived.listen((_) => arrived++);
      transport.receivedC.add({'auth_req_id': 'ciba_1'});
      transport.receivedC.add({'unrelated': 'x'});
      await pumpEventQueue();
      expect(arrived, 1);
      expect(openedIds, isEmpty);
    });
  });

  group('config', () {
    test('push is off unless every Firebase option is given', () {
      expect(
          FirebasePushConfig.resolve(
              apiKey: 'k', appId: 'a', senderId: '', projectId: 'p'),
          isNull);
      final o = FirebasePushConfig.resolve(
          apiKey: ' k ', appId: 'a', senderId: 's', projectId: 'p');
      expect(o?.apiKey, 'k');
      expect(o?.messagingSenderId, 's');
    });
  });

  group('screens', () {
    late List<String> fetched;
    late ApprovalServices services;

    setUp(() {
      fetched = [];
      final store = MemoryEnrollmentStore()..write(record('dev-token'));
      services = ApprovalServices(
        client: ApprovalClient(
            baseUrl: 'http://oidc',
            client: MockClient((req) async {
              fetched.add(req.url.path);
              if (req.url.path == '/devices/requests') {
                return http.Response('{"requests":[]}', 200);
              }
              return http.Response('{"error":"not_found"}', 404);
            })),
        keys: SoftwareKeyStore(deviceTestKey()),
        enrollment: store,
        gate: _Pass(),
        history: MemoryHistoryStore(),
      );
    });

    testWidgets('a tapped notification opens that request, fetched from OIDC',
        (tester) async {
      final navigator = GlobalKey<NavigatorState>();
      await tester.pumpWidget(AuthenticatorApp(
          config: AppConfig.resolve(''),
          approvals: services,
          navigatorKey: navigator));
      openApprovalRequest(navigator, services, 'ciba_tapped');
      await tester.pumpAndSettle();
      final screen = tester.widget<ApprovalScreen>(find.byType(ApprovalScreen));
      expect(screen.authReqId, 'ciba_tapped');
      expect(fetched, contains('/consent/requests/ciba_tapped'));
    });

    testWidgets('the pending list refreshes on a push', (tester) async {
      final pushes = StreamController<void>.broadcast();
      await tester.pumpWidget(MaterialApp(
          home: PendingScreen(
              services: services,
              pollEvery: const Duration(hours: 1),
              pushes: pushes.stream)));
      await tester.pumpAndSettle();
      final before = fetched.where((p) => p == '/devices/requests').length;
      pushes.add(null);
      await tester.pumpAndSettle();
      expect(fetched.where((p) => p == '/devices/requests').length, before + 1);
      await tester.pumpWidget(const SizedBox());
      await pushes.close();
    });

    testWidgets('a push during a running refresh refreshes again afterwards',
        (tester) async {
      final gate = Completer<void>();
      var calls = 0;
      final slow = ApprovalServices(
        client: ApprovalClient(
            baseUrl: 'http://oidc',
            client: MockClient((req) async {
              calls++;
              if (calls == 1) await gate.future;
              return http.Response('{"requests":[]}', 200);
            })),
        keys: services.keys,
        enrollment: services.enrollment,
        gate: services.gate,
        history: services.history,
      );
      final pushes = StreamController<void>.broadcast();
      await tester.pumpWidget(MaterialApp(
          home: PendingScreen(
              services: slow,
              pollEvery: const Duration(hours: 1),
              pushes: pushes.stream)));
      await tester.pump();
      expect(calls, 1);
      pushes.add(null);
      await tester.pump();
      expect(calls, 1, reason: 'no second request while one runs');
      gate.complete();
      await tester.pumpAndSettle();
      expect(calls, 2);
      await tester.pumpWidget(const SizedBox());
      await pushes.close();
    });
  });
}
