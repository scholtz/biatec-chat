import 'dart:convert';
import 'dart:typed_data';

import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/enrollment/widgets/enrollment_problem.dart';
import 'package:biatec_authenticator/enrollment/widgets/key_protection_card.dart';
import 'package:biatec_authenticator/enrollment/widgets/onboarding_step.dart';
import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:biatec_authenticator/keys/key_security_level.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

final _t0 = DateTime.utc(2026, 11, 1, 9);

class _Keys implements DeviceKeyStore {
  _Keys(this.level);
  KeySecurityLevel level;
  DeviceKey? key;

  @override
  Future<DeviceKey> generate(String alias) async =>
      key = DeviceKey(kid: alias, x: 'xx', y: 'yy', securityLevel: level);

  @override
  Future<DeviceKey?> load(String alias) async => key;

  @override
  Future<Uint8List> sign(String alias, Uint8List data) async => data;

  @override
  Future<void> delete(String alias) async => key = null;
}

/// A real controller on a scripted Biatec OIDC, so each step of the onboarding is reached the way a user reaches it.
class _Rig {
  _Rig({KeySecurityLevel level = KeySecurityLevel.trustedEnvironment})
      : keys = _Keys(level) {
    controller = EnrollmentController(
      keys: keys,
      store: ObservableEnrollmentStore(store, clock: () => clock),
      client: OidcEnrollmentClient(
          baseUrl: 'http://mock', client: MockClient(_handle)),
      clock: () => clock,
    );
  }

  DateTime clock = _t0;
  final _Keys keys;
  final store = MemoryEnrollmentStore();
  late final EnrollmentController controller;

  /// Thrown by POST /devices (a network failure) when set.
  Object? networkError;

  /// Answers POST /devices when set; otherwise it succeeds with a 24 hour delay.
  http.Response Function(http.Request)? devices;
  int recoveryStatus = 200;
  int ackStatus = 200;
  final List<String> tokens = [];

  Future<http.Response> _handle(http.Request r) async {
    switch (r.url.path) {
      case '/devices':
        tokens.add(r.headers['Authorization'] ?? '');
        if (networkError != null) throw networkError!;
        if (devices != null) return devices!(r);
        return http.Response(
            jsonEncode({
              'kid': (jsonDecode(r.body) as Map)['kid'],
              'active': false,
              'active_at':
                  _t0.add(const Duration(hours: 24)).millisecondsSinceEpoch ~/
                      1000,
              'device_token': 'tok',
            }),
            201);
      case '/devices/recovery-code':
        return recoveryStatus == 200
            ? http.Response('{"recovery_code":"AAAA-BBBB-CCCC"}', 200)
            : http.Response('{"error":"unavailable"}', recoveryStatus);
      case '/devices/recovery-code/ack':
        return http.Response('{}', ackStatus);
    }
    return http.Response('{}', 404);
  }
}

Future<void> _pump(WidgetTester tester, _Rig rig,
    {Size size = const Size(400, 900),
    Brightness brightness = Brightness.light,
    double scale = 1}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  tester.platformDispatcher.textScaleFactorTestValue = scale;
  addTearDown(tester.platformDispatcher.clearAllTestValues);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(brightness),
    builder: largeTypeBuilder,
    home: EnrollmentScreen(
        config: AppConfig.resolve(''), controller: rig.controller),
  ));
  await tester.pump();
}

/// Lets the scripted server answer (real async work, outside the fake clock) and redraws.
Future<void> _settle(WidgetTester tester) async {
  await tester
      .runAsync(() => Future<void>.delayed(const Duration(milliseconds: 60)));
  await tester.pump();
}

Future<void> _start(WidgetTester tester) async {
  // At large text the button is below the fold: it is reached by scrolling.
  await tester.ensureVisible(find.byKey(const Key('onboarding-start')));
  await tester.tap(find.byKey(const Key('onboarding-start')));
  await tester.pump();
}

Future<void> _enroll(WidgetTester tester) async {
  await tester.ensureVisible(find.byKey(const Key('enroll-button')));
  await tester.tap(find.byKey(const Key('enroll-button')));
  await _settle(tester);
}

/// Active, with the recovery code fetched.
Future<void> _activate(WidgetTester tester, _Rig rig) async {
  rig.clock = _t0.add(const Duration(hours: 24));
  await tester.runAsync(rig.controller.refresh);
  await tester.pump();
}

Future<void> _end(WidgetTester tester) => tester.pumpWidget(const SizedBox());

String _stepText(OnboardingStep s) => 'Step ${s.index + 1} of 5 · ${s.label}';

void main() {
  group('classifying a failure', () {
    EnrollmentProblemKind kind(String e) => classifyEnrollmentError(e).kind;

    test('each cause has its own kind, title and next action', () {
      expect(kind('Could not reach Biatec OIDC: SocketException'),
          EnrollmentProblemKind.network);
      expect(
          kind(
              'The connection to oidc.example could not be verified, so nothing was sent.'),
          EnrollmentProblemKind.unverified);
      expect(kind('Enrollment refused (401): invalid_token'),
          EnrollmentProblemKind.refusedLogin);
      expect(kind('Enrollment refused (403): access_denied'),
          EnrollmentProblemKind.refusedLogin);
      expect(kind('A fresh login is required to enroll.'),
          EnrollmentProblemKind.refusedLogin);
      expect(kind('Enrollment refused (409): conflict'),
          EnrollmentProblemKind.alreadyEnrolled);
      expect(kind('Enrollment refused (400): kid already enrolled'),
          EnrollmentProblemKind.alreadyEnrolled);
      expect(
          kind(
              'This device keeps the key in software only, not in secure hardware. Enrollment is refused.'),
          EnrollmentProblemKind.keyNotHardware);
      expect(
          kind('Enrollment refused (500): boom'), EnrollmentProblemKind.other);
      expect(
          kind('Malformed enrollment response.'), EnrollmentProblemKind.other);
      final titles = {
        for (final e in [
          'Could not reach Biatec OIDC: x',
          'Enrollment refused (401): x',
          'Enrollment refused (409): x',
        ])
          classifyEnrollmentError(e).title,
      };
      expect(titles, hasLength(3));
    });

    test('the status code decides before the servers own wording', () {
      // "already" in the detail of a refused login must not read as an enrolled device.
      expect(kind('Enrollment refused (401): token already used'),
          EnrollmentProblemKind.refusedLogin);
      expect(kind('Enrollment refused (409): kid already enrolled'),
          EnrollmentProblemKind.alreadyEnrolled);
      expect(kind('Enrollment refused (500): already busy'),
          EnrollmentProblemKind.other);
    });

    test('the real client messages map to the kinds they are for', () {
      String message(int status) {
        try {
          OidcEnrollmentClient.parseResponse(status, '{"error":"x"}');
        } on EnrollmentException catch (e) {
          return e.message;
        }
        throw StateError('no refusal');
      }

      expect(kind(message(401)), EnrollmentProblemKind.refusedLogin);
      expect(kind(message(403)), EnrollmentProblemKind.refusedLogin);
      expect(kind(message(409)), EnrollmentProblemKind.alreadyEnrolled);
      expect(kind(message(500)), EnrollmentProblemKind.other);
      expect(
          kind(const EnrollmentException('Could not reach Biatec OIDC: x')
              .message),
          EnrollmentProblemKind.network);
    });

    test('only an already-enrolled device is told to check again', () {
      expect(classifyEnrollmentError('Enrollment refused (409): x').checksAgain,
          isTrue);
      expect(
          classifyEnrollmentError('Could not reach Biatec OIDC: x').checksAgain,
          isFalse);
    });

    test('a message never repeats a login token it was never given', () {
      for (final e in [
        'Could not reach Biatec OIDC: x',
        'Enrollment refused (401): x',
        'A fresh login is required to enroll.',
      ]) {
        expect(
            classifyEnrollmentError(e).message, isNot(contains('login-alice')));
      }
    });
  });

  group('the steps', () {
    testWidgets(
        'start with a welcome that says what the app holds and does not',
        (tester) async {
      final rig = _Rig();
      await _pump(tester, rig);
      expect(find.byKey(const Key('step-welcome')), findsOneWidget);
      expect(find.text(_stepText(OnboardingStep.welcome)), findsOneWidget);
      expect(find.text('What it holds'), findsOneWidget);
      expect(find.text('What it never holds'), findsOneWidget);
      expect(find.textContaining('recovery code'), findsWidgets);
      // One primary action, and nothing to type yet.
      expect(find.byKey(const Key('login-token')), findsNothing);
      expect(find.byKey(const Key('onboarding-start')), findsOneWidget);
      await _end(tester);
    });

    testWidgets(
        'then connect, protect the key, recovery code and done, each with its progress',
        (tester) async {
      final rig = _Rig();
      await _pump(tester, rig);
      await _start(tester);
      expect(find.text(_stepText(OnboardingStep.connect)), findsOneWidget);
      expect(find.byKey(const Key('step-connect')), findsOneWidget);
      expect(find.byKey(const Key('login-token')), findsOneWidget);

      await _enroll(tester);
      // The delay: the key level in words with an icon, and the time left.
      expect(find.text(_stepText(OnboardingStep.protect)), findsOneWidget);
      expect(find.text('Device inactive'), findsOneWidget);
      expect(find.text('Time left: 24:00:00'), findsOneWidget);
      expect(find.byType(KeyProtectionCard), findsOneWidget);
      expect(find.text('Hardware-protected key'), findsOneWidget);
      expect(
          find.textContaining('Key protection: trusted execution environment'),
          findsOneWidget);
      expect(find.byIcon(Icons.verified_user_outlined), findsOneWidget);
      expect(find.text('Recovery code'), findsNothing);

      await _activate(tester, rig);
      expect(find.text(_stepText(OnboardingStep.recovery)), findsOneWidget);
      expect(find.text('AAAA-BBBB-CCCC'), findsOneWidget);

      await tester.tap(find.byKey(const Key('recovery-confirm')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('recovery-saved')));
      await _settle(tester);
      expect(find.text(_stepText(OnboardingStep.done)), findsOneWidget);
      expect(find.byKey(const Key('step-done')), findsOneWidget);
      expect(find.text('This device is enrolled and active.'), findsOneWidget);
      expect(find.text('AAAA-BBBB-CCCC'), findsNothing);
      await _end(tester);
    });

    testWidgets('show the progress as segments and in words, in order',
        (tester) async {
      for (final step in OnboardingStep.values) {
        await tester.pumpWidget(MaterialApp(
            theme: buildTheme(Brightness.light),
            home: Scaffold(body: StepIndicator(step: step))));
        expect(find.text(_stepText(step)), findsOneWidget);
      }
      expect(OnboardingStep.values.map((s) => s.label), [
        'Welcome',
        'Connect',
        'Protect the key',
        'Recovery code',
        'Done',
      ]);
    });

    testWidgets(
        'count down the delay, and say it only by the minute to a screen reader',
        (tester) async {
      final handle = tester.ensureSemantics();
      final rig = _Rig();
      await _pump(tester, rig);
      await _start(tester);
      await _enroll(tester);
      rig.clock = _t0.add(const Duration(hours: 1, minutes: 30, seconds: 5));
      await tester.runAsync(rig.controller.refresh);
      await tester.pump();
      expect(find.text('Time left: 22:29:55'), findsOneWidget);
      expect(find.bySemanticsLabel('Activates in 22 hours and 29 minutes'),
          findsOneWidget);
      handle.dispose();
      await _end(tester);
    });

    testWidgets('the login token is hidden unless the user asks to see it',
        (tester) async {
      final rig = _Rig();
      await _pump(tester, rig);
      await _start(tester);
      TextField field() =>
          tester.widget<TextField>(find.byKey(const Key('login-token')));
      expect(field().obscureText, isTrue);
      expect(field().enableSuggestions, isFalse);
      expect(field().autocorrect, isFalse);
      await tester.tap(find.byKey(const Key('login-token-toggle')));
      await tester.pump();
      expect(field().obscureText, isFalse);
      expect(find.byTooltip('Hide the token'), findsOneWidget);
      await tester.tap(find.byKey(const Key('login-token-toggle')));
      await tester.pump();
      expect(field().obscureText, isTrue);
      await _end(tester);
    });

    testWidgets(
        'under a minute left is spoken as such, not as 0 hours 0 minutes',
        (tester) async {
      final handle = tester.ensureSemantics();
      final rig = _Rig();
      await _pump(tester, rig);
      await _start(tester);
      await _enroll(tester);
      rig.clock = _t0.add(const Duration(hours: 23, minutes: 59, seconds: 30));
      await tester.runAsync(rig.controller.refresh);
      await tester.pump();
      expect(find.text('Time left: 0:00:30'), findsOneWidget);
      expect(find.bySemanticsLabel('Activates in less than a minute'),
          findsOneWidget);
      handle.dispose();
      await _end(tester);
    });

    testWidgets('a software key is named as such and is not accepted',
        (tester) async {
      await tester.pumpWidget(MaterialApp(
          theme: buildTheme(Brightness.light),
          home: const Scaffold(
              body: Column(children: [
            KeyProtectionCard(level: KeySecurityLevel.software),
            KeyProtectionCard(level: KeySecurityLevel.unknown),
          ]))));
      expect(find.text('Software-only key'), findsOneWidget);
      expect(find.textContaining('not accepted'), findsOneWidget);
      expect(find.byIcon(Icons.gpp_bad_outlined), findsOneWidget);
      expect(find.text('Key protection unknown'), findsOneWidget);
      expect(find.byIcon(Icons.help_outline), findsOneWidget);
    });
  });

  group('failures', () {
    testWidgets('no network says so, keeps the token and offers to try again',
        (tester) async {
      final rig = _Rig()..networkError = http.ClientException('offline');
      await _pump(tester, rig);
      await _start(tester);
      await _enroll(tester);
      expect(
          find.byKey(const Key('enrollment-problem-network')), findsOneWidget);
      expect(find.text('Cannot reach Biatec OIDC'), findsOneWidget);
      expect(find.textContaining('Nothing was enrolled'), findsOneWidget);
      expect(find.text(_stepText(OnboardingStep.connect)), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Try again'), findsOneWidget);
      expect(
          tester
              .widget<TextField>(find.byKey(const Key('login-token')))
              .controller!
              .text,
          'login-alice');

      rig.networkError = null;
      await tester.tap(find.widgetWithText(FilledButton, 'Try again'));
      await _settle(tester);
      expect(find.text('Device inactive'), findsOneWidget);
      await _end(tester);
    });

    testWidgets('a refused login token says to get a fresh one',
        (tester) async {
      final rig = _Rig()
        ..devices = (_) => http.Response('{"error":"invalid_token"}', 401);
      await _pump(tester, rig);
      await _start(tester);
      await _enroll(tester);
      expect(find.byKey(const Key('enrollment-problem-refusedLogin')),
          findsOneWidget);
      expect(find.text('Login token not accepted'), findsOneWidget);
      expect(find.textContaining('fresh one'), findsOneWidget);
      // The field is still there to replace the token, and the action enrolls again.
      expect(find.byKey(const Key('login-token')), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Connect and create the key'),
          findsOneWidget);
      await _end(tester);
    });

    testWidgets('an empty token is asked for, not sent', (tester) async {
      final rig = _Rig();
      await _pump(tester, rig);
      await _start(tester);
      await tester.enterText(find.byKey(const Key('login-token')), '   ');
      await _enroll(tester);
      expect(find.text('Login token needed'), findsOneWidget);
      expect(rig.tokens, isEmpty, reason: 'nothing was sent');
      await _end(tester);
    });

    testWidgets('an already enrolled device says so and checks again',
        (tester) async {
      final rig = _Rig()
        ..devices = (_) => http.Response('{"error":"conflict"}', 409);
      await _pump(tester, rig);
      await _start(tester);
      await _enroll(tester);
      expect(find.byKey(const Key('enrollment-problem-alreadyEnrolled')),
          findsOneWidget);
      expect(find.text('This device is already enrolled'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Check again'), findsOneWidget);
      // The server has the device but this app may not, so connecting again stays possible.
      expect(find.byKey(const Key('enroll-again-button')), findsOneWidget);

      // The enrollment is in the store after all: checking again opens it.
      await rig.store.write(EnrollmentRecord(
          kid: 'dev-1',
          deviceToken: 'tok',
          activeAt: _t0.subtract(const Duration(days: 1)),
          recoveryCodeShown: true));
      await tester.tap(find.widgetWithText(FilledButton, 'Check again'));
      await _settle(tester);
      expect(find.byKey(const Key('enrollment-problem-alreadyEnrolled')),
          findsNothing);
      await _end(tester);
    });

    testWidgets(
        'a device that cannot keep the key in hardware is refused with the reason',
        (tester) async {
      final rig = _Rig(level: KeySecurityLevel.software);
      await _pump(tester, rig);
      await _start(tester);
      await _enroll(tester);
      expect(find.byKey(const Key('enrollment-problem-keyNotHardware')),
          findsOneWidget);
      expect(find.text('This device cannot protect the key'), findsOneWidget);
      expect(find.textContaining('software only'), findsWidgets);
      await _end(tester);
    });

    testWidgets('a recovery code that cannot be loaded can be tried again',
        (tester) async {
      final rig = _Rig()..recoveryStatus = 503;
      await _pump(tester, rig);
      await _start(tester);
      await _enroll(tester);
      await _activate(tester, rig);
      expect(
          find.text('The recovery code could not be loaded'), findsOneWidget);
      expect(find.text('AAAA-BBBB-CCCC'), findsNothing);

      rig.recoveryStatus = 200;
      await tester.tap(find.text('Try again'));
      await _settle(tester);
      expect(find.text('AAAA-BBBB-CCCC'), findsOneWidget);
      await _end(tester);
    });

    testWidgets('a confirmation that fails keeps the code and says so',
        (tester) async {
      final rig = _Rig()..ackStatus = 500;
      await _pump(tester, rig);
      await _start(tester);
      await _enroll(tester);
      await _activate(tester, rig);
      await tester.tap(find.byKey(const Key('recovery-confirm')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('recovery-saved')));
      await _settle(tester);
      expect(find.text('Could not confirm'), findsOneWidget);
      expect(find.text('AAAA-BBBB-CCCC'), findsOneWidget);
      expect(find.text(_stepText(OnboardingStep.recovery)), findsOneWidget);
      await _end(tester);
    });
  });

  group('the recovery step', () {
    testWidgets('needs an explicit confirmation before it moves on',
        (tester) async {
      final rig = _Rig();
      await _pump(tester, rig);
      await _start(tester);
      await _enroll(tester);
      await _activate(tester, rig);
      FilledButton button() =>
          tester.widget<FilledButton>(find.byKey(const Key('recovery-saved')));
      expect(button().onPressed, isNull);
      await tester.tap(find.byKey(const Key('recovery-confirm')));
      await tester.pump();
      expect(button().onPressed, isNotNull);
      await tester.tap(find.byKey(const Key('recovery-confirm')));
      await tester.pump();
      expect(button().onPressed, isNull,
          reason: 'unticking turns it off again');
      expect(find.text('Shown only once'), findsOneWidget);
      await _end(tester);
    });

    testWidgets('is read out character by character, and is not selectable',
        (tester) async {
      final handle = tester.ensureSemantics();
      final rig = _Rig();
      await _pump(tester, rig);
      await _start(tester);
      await _enroll(tester);
      await _activate(tester, rig);
      expect(find.byType(SelectableText), findsNothing);
      expect(
          find.bySemanticsLabel(
              'Recovery code: A A A A dash B B B B dash C C C C'),
          findsOneWidget);
      handle.dispose();
      await _end(tester);
    });
  });

  group('accessibility', () {
    Future<void> guidelines(WidgetTester tester) async {
      await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
      await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
    }

    testWidgets('every control is at least 48 dp and labelled, on each step',
        (tester) async {
      final handle = tester.ensureSemantics();
      final rig = _Rig()..networkError = http.ClientException('offline');
      await _pump(tester, rig);
      await guidelines(tester); // welcome
      await _start(tester);
      await guidelines(tester); // connect
      await _enroll(tester);
      await guidelines(tester); // connect with a problem
      rig.networkError = null;
      await tester.tap(find.widgetWithText(FilledButton, 'Try again'));
      await _settle(tester);
      await guidelines(tester); // inactive
      await _activate(tester, rig);
      await guidelines(tester); // recovery
      handle.dispose();
      await _end(tester);
    });

    for (final brightness in Brightness.values) {
      testWidgets(
          'every step holds at 3x text on a small screen (${brightness.name})',
          (tester) async {
        final rig = _Rig()..networkError = http.ClientException('offline');
        await _pump(tester, rig,
            size: const Size(320, 568), brightness: brightness, scale: 3);
        expect(tester.takeException(), isNull, reason: 'welcome');
        await _start(tester);
        expect(tester.takeException(), isNull, reason: 'connect');
        await _enroll(tester);
        expect(tester.takeException(), isNull, reason: 'a problem');
        // The button under a long message is still reachable by scrolling.
        await tester.ensureVisible(find.byKey(const Key('enroll-button')));
        rig.networkError = null;
        await tester.tap(find.byKey(const Key('enroll-button')));
        await _settle(tester);
        expect(tester.takeException(), isNull, reason: 'inactive');
        await _activate(tester, rig);
        expect(tester.takeException(), isNull, reason: 'recovery');
        // The tile is taller than the screen at this size: the checkbox itself is what is reached and tapped.
        final box = find.descendant(
            of: find.byKey(const Key('recovery-confirm')),
            matching: find.byType(Checkbox));
        await tester.ensureVisible(box);
        await tester.tap(box);
        await tester.pump();
        await tester.ensureVisible(find.byKey(const Key('recovery-saved')));
        await tester.tap(find.byKey(const Key('recovery-saved')));
        await _settle(tester);
        expect(tester.takeException(), isNull, reason: 'done');
        expect(
            find.text('This device is enrolled and active.'), findsOneWidget);
        await _end(tester);
      });
    }
  });
}
