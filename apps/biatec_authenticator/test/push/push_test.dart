import 'dart:async';
import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/biometric_gate.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/push/device_event.dart';
import 'package:biatec_authenticator/push/device_event_controller.dart';
import 'package:biatec_authenticator/shell/shell_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../approvals/approval_test.dart' show vectorRequest;
import '../approvals/support.dart';
import '../lock/support.dart';
import 'support.dart';

class _Pass implements BiometricGate {
  @override
  Future<bool> confirm(String reason) async => true;
}

/// A protocol 1.1 request from the shared vectors, and its id.
Map<String, Object?> sampleRequest() =>
    vectorRequest('direct-transaction-approve-domain-proven');

/// Records the calls of the app to Biatec OIDC; [status] is what `PUT /devices/push` answers and [pending] is what `GET /devices/requests` returns.
class FakeRegistry {
  final calls = <Map<String, Object?>>[];
  int status = 200;
  List<Map<String, Object?>> pending = [];
  http.Client get client => MockClient((req) async {
        calls.add({
          'method': req.method,
          'path': req.url.path,
          'auth': req.headers['Authorization'],
          if (req.body.isNotEmpty) 'body': jsonDecode(req.body),
        });
        if (req.url.path == '/devices/requests') {
          return http.Response(jsonEncode({'requests': pending}), 200);
        }
        return http.Response('{}', status);
      });

  List<Map<String, Object?>> get registrations =>
      calls.where((c) => c['path'] == '/devices/push').toList();
}

EnrollmentRecord record(String token) => EnrollmentRecord(
    kid: 'alice-device',
    deviceToken: token,
    activeAt: DateTime.utc(2020),
    recoveryCodeShown: true);

void main() {
  group('events', () {
    test('only a plausible id is taken; everything else is ignored', () {
      expect(
          parseDeviceEvent('consent_request', [
            {'auth_req_id': 'ciba_0a1b', 'title': 'Send 5000 EUR'}
          ]),
          const ConsentRequested('ciba_0a1b'));
      expect(
          parseDeviceEvent('consent_closed', [
            {'auth_req_id': 'ciba_0a1b'}
          ]),
          const ConsentClosed('ciba_0a1b'));
      expect(
          parseDeviceEvent('device_enrolled', [
            {'kid': 'phone-2'}
          ]),
          const DeviceEnrolled('phone-2'));
      expect(parseDeviceEvent('settings_changed', []), const SettingsChanged());
      expect(parseDeviceEvent('settings_changed', [<String, Object?>{}]),
          const SettingsChanged());
      for (final bad in <Object?>[
        <String, Object?>{},
        {'auth_req_id': 42},
        {'auth_req_id': ''},
        {'auth_req_id': '../devices'},
        {'auth_req_id': 'with space'},
        {'auth_req_id': 'a' * 129},
        'text',
        null,
      ]) {
        expect(parseDeviceEvent('consent_request', [bad]), isNull,
            reason: '$bad');
        expect(parseDeviceEvent('consent_closed', [bad]), isNull,
            reason: '$bad');
      }
      expect(parseDeviceEvent('consent_request', []), isNull);
      expect(
          parseDeviceEvent('device_enrolled', [
            {'kid': '<script>'}
          ]),
          isNull);
      expect(
          parseDeviceEvent('unknown', [
            {'auth_req_id': 'x'}
          ]),
          isNull);
    });
  });

  group('controller', () {
    late FakeSource source;
    late FakeNotifier notifier;
    late FakeRegistry registry;
    late ObservableEnrollmentStore store;
    late DateTime now;
    late bool foreground;
    late List<String> closed;
    late int devices;
    late int settings;
    late DeviceEventController controller;

    DeviceEventController make({FakeApple? apple, Duration? retryAfter}) =>
        DeviceEventController(
          enrollment: store,
          client:
              ApprovalClient(baseUrl: 'http://oidc', client: registry.client),
          notifier: notifier,
          isForeground: () => foreground,
          onRequestClosed: closed.add,
          onDevicesChanged: () => devices++,
          onSettingsChanged: () => settings++,
          apple: apple,
          watchLifecycle: false,
          clock: () => now,
          retryAfter: retryAfter ?? const Duration(minutes: 1),
        );

    setUp(() {
      source = FakeSource();
      notifier = FakeNotifier();
      registry = FakeRegistry();
      store = ObservableEnrollmentStore(MemoryEnrollmentStore());
      now = DateTime.utc(2026, 10, 19, 12);
      foreground = false;
      closed = [];
      devices = 0;
      settings = 0;
      controller = make();
    });
    tearDown(() => controller.dispose());

    test('starting starts the source, once', () async {
      await controller.start(source);
      await controller.start(source);
      expect(source.starts, 1);
    });

    test(
        'consent_request in the background: the pending list is fetched and a notification shown',
        () async {
      final request = sampleRequest();
      final id = request['auth_req_id'] as String;
      await store.write(record('dev-token'));
      registry.pending = [request];
      await controller.start(source);
      var arrived = 0;
      controller.requestArrived.listen((_) => arrived++);
      source.controller.add(ConsentRequested(id));
      await pumpEventQueue();
      expect(notifier.requests, [id]);
      expect(arrived, 1);
      final fetch =
          registry.calls.where((c) => c['path'] == '/devices/requests').single;
      expect(fetch['auth'], 'Device dev-token');
    });

    test(
        'consent_request in the foreground refreshes the list and shows no notification',
        () async {
      final request = sampleRequest();
      await store.write(record('dev-token'));
      registry.pending = [request];
      foreground = true;
      await controller.start(source);
      var arrived = 0;
      controller.requestArrived.listen((_) => arrived++);
      source.controller
          .add(ConsentRequested(request['auth_req_id'] as String));
      await pumpEventQueue();
      expect(arrived, 1);
      expect(notifier.requests, isEmpty);
    });

    test('a request that is no longer pending shows no notification',
        () async {
      await store.write(record('dev-token'));
      registry.pending = [];
      await controller.start(source);
      source.controller.add(const ConsentRequested('ciba_gone'));
      await pumpEventQueue();
      expect(notifier.requests, isEmpty);
    });

    test(
        'a pending list that cannot be fetched still shows the fixed notification',
        () async {
      await store.write(record('dev-token'));
      final failing = DeviceEventController(
        enrollment: store,
        client: ApprovalClient(
            baseUrl: 'http://oidc',
            client: MockClient((_) async => throw http.ClientException('x'))),
        notifier: notifier,
        isForeground: () => false,
        watchLifecycle: false,
      );
      addTearDown(failing.dispose);
      final other = FakeSource();
      await failing.start(other);
      other.controller.add(const ConsentRequested('ciba_2'));
      await pumpEventQueue();
      expect(notifier.requests, ['ciba_2']);
    });

    test('no notification when the device is not enrolled', () async {
      await controller.start(source);
      source.controller.add(const ConsentRequested('ciba_1'));
      await pumpEventQueue();
      expect(notifier.requests, isEmpty);
      expect(registry.calls, isEmpty);
    });

    test('consent_closed drops the card and the notification', () async {
      await controller.start(source);
      source.controller.add(const ConsentClosed('ciba_1'));
      await pumpEventQueue();
      expect(closed, ['ciba_1']);
      expect(notifier.cancelled, ['ciba_1']);
    });

    test('device_enrolled refreshes the device list and notifies in the background',
        () async {
      await controller.start(source);
      source.controller.add(const DeviceEnrolled('phone-2'));
      await pumpEventQueue();
      expect(devices, 1);
      expect(notifier.devices, ['phone-2']);
    });

    test('device_enrolled in the foreground refreshes but shows no notification',
        () async {
      foreground = true;
      await controller.start(source);
      source.controller.add(const DeviceEnrolled('phone-2'));
      await pumpEventQueue();
      expect(devices, 1);
      expect(notifier.devices, isEmpty);
    });

    test('settings_changed refreshes the settings', () async {
      await controller.start(source);
      source.controller.add(const SettingsChanged());
      await pumpEventQueue();
      expect(settings, 1);
      expect(notifier.requests, isEmpty);
    });

    test('with no Apple wake, nothing is registered', () async {
      await store.write(record('dev-token'));
      await controller.start(source);
      await controller.registerIfEnrolled();
      expect(registry.registrations, isEmpty);
    });

    group('the optional Apple wake', () {
      late FakeApple apple;
      setUp(() async {
        apple = FakeApple();
        await controller.dispose();
        controller = make(apple: apple);
      });

      test('registers nothing before enrollment, then once per token',
          () async {
        await controller.start(source);
        expect(registry.registrations, isEmpty);

        await store.write(record('dev-token'));
        await controller.registerIfEnrolled();
        await controller.registerIfEnrolled();
        expect(registry.registrations, [
          {
            'method': 'PUT',
            'path': '/devices/push',
            'auth': 'Device dev-token',
            'body': {'provider': 'apns', 'token': 'apns-1'},
          }
        ]);

        apple.refreshes.add('apns-2');
        await pumpEventQueue();
        expect(registry.registrations.last['body'],
            {'provider': 'apns', 'token': 'apns-2'});

        // A new enrollment (new device token) registers again.
        await store.write(record('dev-token-2'));
        await pumpEventQueue();
        expect(registry.registrations.last['auth'], 'Device dev-token-2');
        expect(registry.registrations, hasLength(3));

        // Sent again after the resend window, in case the server lost it.
        now = now.add(DeviceEventController.resendAfter);
        await controller.registerIfEnrolled();
        expect(registry.registrations, hasLength(4));
      });

      test('a sign-out registers nothing, and stopping listening is clean',
          () async {
        await store.write(record('dev-token'));
        await controller.start(source);
        await store.clear();
        await pumpEventQueue();
        expect(registry.registrations, hasLength(1));
        await controller.dispose();
        await store.write(record('dev-token-2'));
        await pumpEventQueue();
        expect(registry.registrations, hasLength(1),
            reason: 'no longer listening');
      });

      test('a failed registration is tried again by itself', () async {
        await controller.dispose();
        controller =
            make(apple: apple, retryAfter: const Duration(milliseconds: 20));
        await store.write(record('dev-token'));
        registry.status = 503;
        await controller.start(source);
        expect(registry.registrations, hasLength(1));
        registry.status = 200;
        await Future<void>.delayed(const Duration(milliseconds: 80));
        await pumpEventQueue();
        expect(registry.registrations.length, greaterThanOrEqualTo(2));
        final settled = registry.registrations.length;
        await Future<void>.delayed(const Duration(milliseconds: 80));
        expect(registry.registrations, hasLength(settled),
            reason: 'stops once it works');
      });

      test('no token (declined notifications) registers nothing', () async {
        await store.write(record('dev-token'));
        apple.current = null;
        await controller.start(source);
        expect(registry.registrations, isEmpty);
      });
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
        enrollment: ObservableEnrollmentStore(store),
        gate: _Pass(),
        history: MemoryHistoryStore(),
      );
    });

    testWidgets('a notice for a request that arrived opens the list',
        (tester) async {
      final shell = ShellController();
      final messenger = GlobalKey<ScaffoldMessengerState>();
      await tester.pumpWidget(AuthenticatorApp(
          config: AppConfig.resolve(''),
          lock: await unlockedLock(),
          approvals: services,
          shell: shell,
          messengerKey: messenger));
      await tester.pump();
      shell.select(AppTab.history);
      await tester.pump();
      showRequestArrived(messenger, shell);
      await tester.pump();
      expect(find.text('New approval request'), findsOneWidget);
      await tester.pump(const Duration(seconds: 1));
      await tester.tap(find.text('Open'));
      await tester.pumpAndSettle();
      expect(find.byType(PendingScreen), findsOneWidget);

      // From the list itself, Open does not stack a second list.
      showRequestArrived(messenger, shell);
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      await tester.tap(find.text('Open'));
      await tester.pumpAndSettle();
      expect(find.byType(PendingScreen), findsOneWidget);
      expect(shell.selected, AppTab.approvals);
    });

    testWidgets('the pending list refreshes when a request arrives',
        (tester) async {
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

    testWidgets(
        'an event that arrives during a running refresh refreshes again afterwards',
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

    testWidgets('the pending list still polls every 5 seconds while it is open',
        (tester) async {
      await tester
          .pumpWidget(MaterialApp(home: PendingScreen(services: services)));
      await tester.pumpAndSettle();
      final before = fetched.where((p) => p == '/devices/requests').length;
      await tester.pump(const Duration(seconds: 5));
      await tester.pump();
      expect(fetched.where((p) => p == '/devices/requests').length,
          greaterThan(before));
      await tester.pumpWidget(const SizedBox());
    });
  });
}
