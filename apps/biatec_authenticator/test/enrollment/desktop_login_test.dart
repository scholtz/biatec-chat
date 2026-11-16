// T-248: the configuration of a desktop build (browser login with a loopback redirect, stage defaults for Windows) and what the connect step shows for it.
import 'dart:async';

import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/auth_client.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/loopback_auth_client.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/widgets/enrollment_problem.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show MissingPluginException;
import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:flutter_test/flutter_test.dart';

import '../keys/fake_two_keys.dart';

/// A browser that never comes back until it is cancelled.
class WaitingAuth extends AuthClient {
  final _cancelled = Completer<void>();
  int cancels = 0;

  @override
  bool get canCancel => true;

  @override
  void cancel() {
    cancels++;
    if (!_cancelled.isCompleted) _cancelled.complete();
  }

  @override
  Future<String> signIn(AuthRequest request) async {
    await _cancelled.future;
    throw const LoginCancelled();
  }
}

/// A platform with no native key store: the channel has no implementation.
class NoKeyStore extends FakeTwoKeyStore {
  @override
  Future<DeviceKey> generatePresenceKey() => throw MissingPluginException('no keystore');
}

class TokenAuth extends AuthClient {
  @override
  Future<String> signIn(AuthRequest request) async => 'access-token';
}

void main() {
  test('a platform without a key store enrolls nothing and says why', () async {
    final config = AppConfig.resolve('', release: true, windows: true);
    final c = EnrollmentController(
      keys: NoKeyStore(),
      store: ObservableEnrollmentStore(MemoryEnrollmentStore()),
      client: OidcEnrollmentClient(baseUrl: 'http://oidc'),
      twoKeys: false,
      auth: TokenAuth(),
      loginRequest: AuthRequest.forConfig(config),
    );
    expect(await c.signInAndEnroll(), EnrollResult.failed);
    expect(c.state.error, EnrollmentController.noKeyStoreError);
    expect(c.state.error, isNot(contains('access-token')));
    final problem = classifyEnrollmentError(c.state.error!);
    expect(problem.kind, EnrollmentProblemKind.noKeyStore);
    expect(problem.title, 'This device cannot be enrolled');
  });

  test('a sign-in that waits for the browser is stopped when its controller is disposed', () async {
    final auth = WaitingAuth();
    final config = AppConfig.resolve('', release: true, windows: true);
    final c = EnrollmentController(
      keys: FakeTwoKeyStore(),
      store: ObservableEnrollmentStore(MemoryEnrollmentStore()),
      client: OidcEnrollmentClient(baseUrl: 'http://oidc'),
      twoKeys: false,
      auth: auth,
      loginRequest: AuthRequest.forConfig(config),
    );
    final result = c.signInAndEnroll();
    await Future<void>.delayed(Duration.zero);
    c.dispose();
    expect(auth.cancels, 1);
    expect(await result, EnrollResult.ignored);
  });

  group('configuration of a desktop build', () {
    test('a plain Windows release build is set up for stage: client, loopback redirect, browser login', () {
      final c = AppConfig.resolve('', release: true, windows: true);
      expect(c.oidcBaseUrl, 'https://stage.oidc.biatec.io');
      expect(c.oidcClientId, 'biatec-authenticator-stage-app');
      expect(c.redirectUri, 'http://localhost:42815');
      expect(c.browserLogin, isTrue);
      expect(c.signIn, isTrue);
      expect(c.pasteLoginToken, isFalse);
      // Nothing of the server side that real Biatec OIDC does not have is switched on with it.
      expect(c.inAppLogin, isFalse);
      expect(c.twoKeys, isFalse);
      expect(c.activationByDevice, isFalse);
      final r = AuthRequest.forConfig(c);
      expect(r.scope, 'openid');
      expect(r.discoveryUrl, 'https://stage.oidc.biatec.io/.well-known/openid-configuration');
      expect(r.allowInsecure, isFalse);
    });

    test('a debug Windows build against stage gets the same, and still no paste field', () {
      final c = AppConfig.resolve('https://stage.oidc.biatec.io', release: false, windows: true);
      expect(c.oidcClientId, AppConfig.stageDesktopClientId);
      expect(c.redirectUri, AppConfig.desktopRedirectUri);
      expect(c.signIn, isTrue);
      expect(c.pasteLoginToken, isFalse);
    });

    test('BROWSER_LOGIN=false turns the stage default off', () {
      final c = AppConfig.resolve('', release: true, windows: true, browserLogin: false);
      expect(c.signIn, isFalse);
    });

    test('a defined client id and redirect win over the stage defaults', () {
      final c = AppConfig.resolve('', release: true, windows: true,
          clientId: 'other-client', redirectUri: 'http://127.0.0.1:50123/cb');
      expect(c.oidcClientId, 'other-client');
      expect(c.redirectUri, 'http://127.0.0.1:50123/cb');
    });

    test('another provider on a desktop build: the login is opt-in, the client id is the general default', () {
      final off = AppConfig.resolve('https://id.bank.example', release: true, windows: true);
      expect(off.signIn, isFalse);
      expect(off.oidcClientId, AppConfig.defaultOidcClientId);
      final on = AppConfig.resolve('https://id.bank.example',
          release: true, windows: true, browserLogin: true, clientId: 'bank-authenticator');
      expect(on.signIn, isTrue);
      expect(on.oidcClientId, 'bank-authenticator');
      expect(on.redirectUri, AppConfig.desktopRedirectUri);
      expect(AuthRequest.forConfig(on).discoveryUrl,
          'https://id.bank.example/.well-known/openid-configuration');
    });

    test('a desktop redirect that the app cannot listen on is refused', () {
      for (final bad in [
        'https://stage.oidc.biatec.io/app/authenticator/callback',
        'io.biatec.authenticator:/oauth2redirect',
        'http://example.com:42815',
        'http://localhost',
        'http://localhost:80',
        'http://localhost:42815/?a=b',
        'http://localhost:42815/#f',
        'http://user@localhost:42815',
      ]) {
        expect(() => AppConfig.resolve('', release: true, windows: true, redirectUri: bad),
            throwsA(isA<ConfigException>()),
            reason: bad);
      }
    });

    test('the loopback redirect rule', () {
      for (final ok in ['http://localhost:42815', 'http://127.0.0.1:1024/cb', 'http://[::1]:5000']) {
        expect(AppConfig.isLoopbackRedirect(ok), isTrue, reason: ok);
      }
      for (final bad in [
        'https://localhost:42815',
        'http://localhost.evil.example:42815',
        'http://0.0.0.0:42815',
        'http://localhost:70000',
        'http://localhost:1023',
      ]) {
        expect(AppConfig.isLoopbackRedirect(bad), isFalse, reason: bad);
      }
    });

    test('a phone is not a desktop: no browser login without the in-app login, whose server proves the login fresh', () {
      final phone = AppConfig.resolve('https://stage.oidc.biatec.io', release: true, desktop: false, windows: false);
      expect(phone.signIn, isFalse);
      expect(phone.oidcClientId, AppConfig.defaultOidcClientId);
      for (final release in [true, false]) {
        expect(
            () => AppConfig.resolve('https://stage.oidc.biatec.io',
                release: release, desktop: false, windows: false, browserLogin: true),
            throwsA(isA<ConfigException>().having((e) => e.message, 'message', contains('desktop'))),
            reason: 'release: $release');
      }
      // With the in-app login a phone signs in as before (T-242): the app link, the enroll-device scope.
      final on = AppConfig.resolve('https://stage.oidc.biatec.io',
          release: true, desktop: false, windows: false, inAppLogin: true, browserLogin: true);
      expect(on.signIn, isTrue);
      expect(on.redirectUri, 'https://stage.oidc.biatec.io/app/authenticator/callback');
      expect(AuthRequest.forConfig(on).scope, 'openid enroll-device');
      expect(
          () => AppConfig.resolve('https://stage.oidc.biatec.io',
              release: true, desktop: false, windows: false, inAppLogin: true, redirectUri: 'http://localhost:42815'),
          throwsA(isA<ConfigException>()));
    });
  });

  group('the login client of a build', () {
    EnrollmentController make(AppConfig c) => EnrollmentController.forConfig(c,
        keys: FakeTwoKeyStore(), store: ObservableEnrollmentStore(MemoryEnrollmentStore()));

    test('a desktop build listens on loopback; a phone uses the platform browser session', () {
      final desktop = make(AppConfig.resolve('', release: true, windows: true));
      expect(desktop.auth, isA<LoopbackAuthClient>());
      expect(desktop.loginRequest!.scope, 'openid');
      expect(desktop.loginRequest!.clientId, 'biatec-authenticator-stage-app');
      expect(desktop.twoKeys, isFalse);
      final phone = make(AppConfig.resolve('https://stage.oidc.biatec.io',
          release: true, desktop: false, windows: false, inAppLogin: true));
      expect(phone.auth, isA<AppAuthClient>());
    });
  });

  group('the connect step of a desktop build', () {
    Future<EnrollmentController> open(WidgetTester tester, AppConfig config, AuthClient auth) async {
      final c = EnrollmentController(
        keys: FakeTwoKeyStore(),
        store: ObservableEnrollmentStore(MemoryEnrollmentStore()),
        client: OidcEnrollmentClient(baseUrl: 'http://oidc'),
        twoKeys: false,
        auth: auth,
        loginRequest: AuthRequest.forConfig(config),
      );
      tester.view.physicalSize = const Size(400, 1600);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
          theme: buildTheme(Brightness.light), home: EnrollmentScreen(config: config, controller: c)));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('onboarding-start')));
      await tester.pumpAndSettle();
      return c;
    }

    testWidgets('has the sign-in button, and no token field and no recovery code', (tester) async {
      final config = AppConfig.resolve('', release: true, windows: true);
      await open(tester, config, WaitingAuth());
      expect(find.byKey(const Key('sign-in-button')), findsOneWidget);
      // No promise of a fresh login where the server does not enforce one.
      expect(tester.widget<Text>(find.byKey(const Key('sign-in-intro'))).data, isNot(contains('always')));
      expect(find.byKey(const Key('login-token')), findsNothing);
      expect(find.byKey(const Key('enroll-button')), findsNothing);
      expect(find.byKey(const Key('recovery-code-field')), findsNothing);
      expect(find.byKey(const Key('sign-in-unavailable')), findsNothing);
      expect(find.byKey(const Key('sign-in-cancel')), findsNothing);
    });

    testWidgets('waiting for the browser can be stopped, and nothing is enrolled', (tester) async {
      final config = AppConfig.resolve('', release: true, windows: true);
      final auth = WaitingAuth();
      final c = await open(tester, config, auth);
      await tester.tap(find.byKey(const Key('sign-in-button')));
      await tester.pump();
      expect(c.signingIn, isTrue);
      // The button is off while the browser is open: a second tap starts no second sign-in.
      expect(tester.widget<FilledButton>(find.byKey(const Key('sign-in-button'))).onPressed, isNull);
      expect(find.byKey(const Key('sign-in-cancel')), findsOneWidget);
      await tester.tap(find.byKey(const Key('sign-in-cancel')));
      await tester.pumpAndSettle();
      expect(auth.cancels, 1);
      expect(c.signingIn, isFalse);
      expect(c.state.error, EnrollmentController.loginCancelledError);
      expect(find.byKey(const Key('sign-in-cancel')), findsNothing);
      expect(tester.widget<FilledButton>(find.byKey(const Key('sign-in-button'))).onPressed, isNotNull);
    });
  });
}
