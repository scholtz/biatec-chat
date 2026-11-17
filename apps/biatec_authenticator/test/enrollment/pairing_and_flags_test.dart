// T-242: the pairing code reproduces the worked example of the consent protocol (spec 8.3, vectors/device-signed.json), the strings exist in both languages,
// and the configuration that gates the in-app login and the paste field.
import 'dart:convert';
import 'dart:io';

import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/enrollment_strings.dart';
import 'package:biatec_authenticator/enrollment/auth_client.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/enrollment/pairing_code.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:material_ui/material_ui.dart';
import 'package:flutter_test/flutter_test.dart';

import '../keys/fake_two_keys.dart';
import 'in_app_login_test.dart' show FakeAuth;

void main() {
  group('pairing code', () {
    test('reproduces the worked example of the consent protocol', () {
      expect(
          pairingCode(
              'cpXj3K7c4_bBd9lXxKoO8aZPwTwuPMGzwnYgg3rG_jY',
              'hOlyodNi_jjK_piu5kSIQkvt2q5zwnCUtO8iVYQtwlw',
              'enroll-nonce-53bcbb91525547bf679e9d4f'),
          '102716');
    });

    test('reproduces the vector file (the same inputs, the same code)', () {
      final file = File(
          '../../packages/consent-protocol/vectors/device-signed.json');
      expect(file.existsSync(), isTrue, reason: 'run from apps/biatec_authenticator');
      final v = (jsonDecode(file.readAsStringSync()) as Map)['pairing_code']
          as Map;
      expect(
          pairingCode(
              v['new_approval_jwk_thumbprint'] as String,
              v['new_presence_jwk_thumbprint'] as String,
              v['nonce'] as String),
          v['code']);
      expect(v['code'], '102716');
    });

    test('is zero padded and shown in two groups', () {
      expect(spacedPairingCode('102716'), '102 716');
      expect(spacedPairingCode('000042'), '000 042');
      // A code that is below 100000 keeps its leading zeros (the vector input gives 102716; find one with zeros by brute force over nonces).
      String? padded;
      for (var i = 0; i < 20000 && padded == null; i++) {
        final code = pairingCode(
            'cpXj3K7c4_bBd9lXxKoO8aZPwTwuPMGzwnYgg3rG_jY',
            'hOlyodNi_jjK_piu5kSIQkvt2q5zwnCUtO8iVYQtwlw',
            'nonce-${i.toString().padLeft(12, '0')}');
        if (code.startsWith('0')) padded = code;
      }
      expect(padded, isNotNull);
      expect(padded, hasLength(6));
    });

    test('refuses input that could be read two ways', () {
      const a = 'cpXj3K7c4_bBd9lXxKoO8aZPwTwuPMGzwnYgg3rG_jY';
      const b = 'hOlyodNi_jjK_piu5kSIQkvt2q5zwnCUtO8iVYQtwlw';
      expect(() => pairingCode(a, b, 'short'), throwsArgumentError);
      expect(() => pairingCode(a, b, 'has.a.dot-in-it-123456'),
          throwsArgumentError);
      expect(() => pairingCode('x', b, 'enroll-nonce-0123456789'),
          throwsArgumentError);
    });
  });

  group('strings', () {
    test('English and Slovak have the same keys, and none is empty', () {
      expect(EnrollStrings.sk.keys.toSet(), EnrollStrings.en.keys.toSet());
      for (final s in [EnrollStrings.en, EnrollStrings.sk]) {
        expect(s.activateTitle('X'), contains('X'));
        expect(s.activated('Y'), contains('Y'));
        expect(s.loginFailed('Z'), contains('Z'));
        for (final k in s.keys) {
          expect(s.text(k), isNotEmpty);
        }
      }
      expect(EnrollStrings.of(const Locale('sk')).signInButton,
          isNot(EnrollStrings.en.signInButton));
      expect(EnrollStrings.of(const Locale('de')).signInButton,
          EnrollStrings.en.signInButton);
    });
  });

  group('configuration', () {
    test('the in-app login is on for the mock, off for a real URL until defined',
        () {
      expect(AppConfig.resolve('').inAppLogin, isTrue);
      expect(AppConfig.resolve(AppConfig.mockOidcBaseUrl).inAppLogin, isTrue);
      expect(
          AppConfig.resolve('https://oidc.biatec.io', release: true).inAppLogin,
          isFalse);
      expect(
          AppConfig.resolve('https://oidc.biatec.io',
                  release: true, inAppLogin: true)
              .inAppLogin,
          isTrue);
      expect(AppConfig.resolve('').activationByDevice, isTrue);
      expect(
          AppConfig.resolve('https://oidc.biatec.io',
                  release: true, inAppLogin: true)
              .activationByDevice,
          isFalse,
          reason: 'activation by a device needs the two keys as well');
      expect(
          AppConfig.resolve('https://oidc.biatec.io',
                  release: true, inAppLogin: true, twoKeys: true)
              .activationByDevice,
          isTrue);
    });

    test('the paste field is for debug builds only, never in a release build',
        () {
      // Debug against the mock: on.
      expect(AppConfig.resolve('').pasteLoginToken, isTrue);
      // Debug against a real URL: on until the in-app login is there.
      expect(AppConfig.resolve('https://oidc.biatec.io', release: false).pasteLoginToken,
          isTrue);
      expect(
          AppConfig.resolve('https://oidc.biatec.io',
                  release: false, inAppLogin: true)
              .pasteLoginToken,
          isFalse);
      // Release: off, with or without the in-app login. (The mock cannot be configured in a release build at all.)
      expect(AppConfig.resolve('https://oidc.biatec.io', release: true).pasteLoginToken,
          isFalse);
      expect(
          AppConfig.resolve('https://oidc.biatec.io',
                  release: true, inAppLogin: true)
              .pasteLoginToken,
          isFalse);
      expect(
          () => AppConfig.resolve(AppConfig.mockOidcBaseUrl, release: true),
          throwsA(isA<ConfigException>()));
      // The flag itself, on a configuration that says release.
      expect(
          const AppConfig(
                  oidcBaseUrl: AppConfig.mockOidcBaseUrl,
                  isMock: true,
                  release: true)
              .pasteLoginToken,
          isFalse);
    });

    test('the redirect is an app link in a release build, the custom scheme only in a debug build',
        () {
      expect(AppConfig.resolve('').redirectUri, AppConfig.debugRedirectUri);
      expect(
          AppConfig.resolve('https://oidc.biatec.io', release: true).redirectUri,
          AppConfig.appLinkRedirectUri);
      expect(
          () => AppConfig.resolve('https://oidc.biatec.io',
              release: true, inAppLogin: true, redirectUri: 'io.biatec.authenticator:/cb'),
          throwsA(isA<ConfigException>()));
      expect(
          AppConfig.resolve('https://oidc.biatec.io',
                  release: true, redirectUri: 'https://stage.oidc.biatec.io/app/cb')
              .redirectUri,
          'https://stage.oidc.biatec.io/app/cb');
      expect(
          AppConfig.resolve('https://oidc.biatec.io',
                  release: false, redirectUri: 'io.biatec.authenticator:/cb')
              .redirectUri,
          'io.biatec.authenticator:/cb');
    });

    test('the release redirect is derived from the host of OIDC_BASE_URL: stage and production only',
        () {
      expect(
          AppConfig.resolve('https://oidc.biatec.io',
                  release: true, inAppLogin: true)
              .redirectUri,
          'https://oidc.biatec.io/app/authenticator/callback');
      expect(
          AppConfig.resolve('https://stage.oidc.biatec.io',
                  release: true, inAppLogin: true)
              .redirectUri,
          'https://stage.oidc.biatec.io/app/authenticator/callback');
      // A host with no app link cannot have the in-app login in a release build, and the message says so.
      expect(
          () => AppConfig.resolve('https://oidc.other.example',
              release: true, inAppLogin: true),
          throwsA(isA<ConfigException>().having(
              (e) => e.message, 'message', contains('stage.oidc.biatec.io'))));
      expect(
          () => AppConfig.resolve('https://oidc.biatec.io',
              release: true,
              inAppLogin: true,
              redirectUri: 'https://evil.example/cb'),
          throwsA(isA<ConfigException>()));
      // With the login off the redirect is never used: no error for any host.
      expect(
          AppConfig.resolve('https://oidc.other.example', release: true)
              .inAppLogin,
          isFalse);
    });

    test('an https callback needs iOS 17.4 or later, Android has no minimum',
        () {
      expect(httpsCallbackSupported(TargetPlatform.android, 'Linux 5.10'), isTrue);
      expect(httpsCallbackSupported(TargetPlatform.iOS, 'Version 17.4 (Build 21E213)'), isTrue);
      expect(httpsCallbackSupported(TargetPlatform.iOS, 'Version 18.0 (Build 22A)'), isTrue);
      expect(httpsCallbackSupported(TargetPlatform.iOS, 'Version 17.3.1 (Build 21D61)'), isFalse);
      expect(httpsCallbackSupported(TargetPlatform.iOS, 'Version 16.7 (Build 20H19)'), isFalse);
      // A version that cannot be read fails closed.
      expect(httpsCallbackSupported(TargetPlatform.iOS, 'unknown'), isFalse);
    });

    test('an unused redirect define does not stop the app when the login is off', () {
      expect(
          AppConfig.resolve('https://oidc.biatec.io',
                  release: true, redirectUri: 'io.biatec.authenticator:/cb')
              .inAppLogin,
          isFalse);
      expect(
          AppConfig.resolve('https://oidc.other.example',
                  release: true, redirectUri: 'https://evil.example/cb')
              .inAppLogin,
          isFalse);
      expect(
          () => AppConfig.resolve('https://oidc.biatec.io',
              release: true,
              inAppLogin: true,
              redirectUri: 'io.biatec.authenticator:/cb'),
          throwsA(isA<ConfigException>()));
    });

    test('the client id has a default and a safe shape', () {
      expect(AppConfig.resolve('').oidcClientId, 'biatec-authenticator');
      expect(AppConfig.resolve('', clientId: 'my.client-1').oidcClientId,
          'my.client-1');
      expect(() => AppConfig.resolve('', clientId: 'bad client'),
          throwsA(isA<ConfigException>()));
    });
  });

  group('the controller factory', () {
    EnrollmentController make(AppConfig c) => EnrollmentController.forConfig(c,
        keys: FakeTwoKeyStore(),
        store: ObservableEnrollmentStore(MemoryEnrollmentStore()));

    test('wires the login and the device calls only where the login exists', () {
      final mock = make(AppConfig.resolve(''));
      expect(mock.auth, isNotNull);
      expect(mock.loginRequest!.scope, 'openid enroll-device');
      expect(mock.devices, isNotNull);
      expect(mock.twoKeys, isTrue);
      final real = make(AppConfig.resolve('https://oidc.biatec.io', release: true));
      expect(real.auth, isNull);
      expect(real.loginRequest, isNull);
      expect(real.devices, isNotNull, reason: 'a waiting device can still poll and cancel');
      expect(real.twoKeys, isFalse);
    });
  });

  group('the enrollment screen', () {
    EnrollmentController controller({bool withAuth = true}) =>
        EnrollmentController(
          keys: FakeTwoKeyStore(),
          store: ObservableEnrollmentStore(MemoryEnrollmentStore()),
          client: OidcEnrollmentClient(baseUrl: 'http://oidc'),
          auth: withAuth ? FakeAuth(['t']) : null,
          loginRequest: withAuth ? AuthRequest.forConfig(AppConfig.resolve('')) : null,
        );

    Future<void> open(WidgetTester tester, AppConfig config,
        EnrollmentController c) async {
      tester.view.physicalSize = const Size(400, 1600);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
          theme: buildTheme(Brightness.light),
          home: EnrollmentScreen(config: config, controller: c)));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('onboarding-start')));
      await tester.pumpAndSettle();
    }

    testWidgets('a debug build on the mock has the sign-in button and the paste field',
        (tester) async {
      await open(tester, AppConfig.resolve(''), controller());
      expect(find.byKey(const Key('sign-in-button')), findsOneWidget);
      expect(find.byKey(const Key('login-token')), findsOneWidget);
      expect(find.byKey(const Key('recovery-code-field')), findsOneWidget);
    });

    testWidgets('a release configuration has no paste field at all',
        (tester) async {
      final release = AppConfig.resolve('https://oidc.biatec.io',
          release: true, inAppLogin: true);
      await open(tester, release, controller());
      expect(find.byKey(const Key('login-token')), findsNothing);
      expect(find.byKey(const Key('enroll-button')), findsNothing);
      expect(find.byKey(const Key('sign-in-button')), findsOneWidget);
    });

    testWidgets('a release build against a Biatec OIDC without the login says so and offers no way to paste',
        (tester) async {
      final release = AppConfig.resolve('https://oidc.biatec.io', release: true);
      await open(tester, release, controller(withAuth: false));
      expect(find.byKey(const Key('login-token')), findsNothing);
      expect(find.byKey(const Key('enroll-button')), findsNothing);
      expect(find.byKey(const Key('sign-in-button')), findsNothing);
      expect(find.byKey(const Key('sign-in-unavailable')), findsOneWidget);
    });

    testWidgets('a debug build against a Biatec OIDC without the login keeps the paste enrollment',
        (tester) async {
      final config = AppConfig.resolve('https://oidc.biatec.io', release: false);
      await open(tester, config, controller(withAuth: false));
      expect(find.byKey(const Key('login-token')), findsOneWidget);
      expect(find.byKey(const Key('enroll-button')), findsOneWidget);
      expect(find.byKey(const Key('sign-in-button')), findsNothing);
    });
  });
}
