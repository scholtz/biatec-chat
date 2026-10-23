import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('AppConfig.resolve (BCHAT-029)', () {
    test('https URLs resolve, and a trailing slash is dropped', () {
      final c = AppConfig.resolve('https://oidc.biatec.io/', release: true);
      expect(c.oidcBaseUrl, 'https://oidc.biatec.io');
      expect(c.isMock, isFalse);
    });

    test('the built-in mock URL and the empty default are the mock in a debug build only', () {
      expect(AppConfig.resolve('', release: false).isMock, isTrue);
      expect(
          AppConfig.resolve(AppConfig.mockOidcBaseUrl, release: false).isMock,
          isTrue);
      // However the mock URL is spelled, it is the mock (and not an "other" local URL).
      final shouty = AppConfig.resolve('HTTP://10.0.2.2:5080/mock-oidc/', release: false);
      expect(shouty.isMock, isTrue);
      expect(shouty.oidcBaseUrl, AppConfig.mockOidcBaseUrl);
      // A release (or profile) build has no mock and no default.
      expect(() => AppConfig.resolve('', release: true), throwsA(isA<ConfigException>()));
      expect(() => AppConfig.resolve(AppConfig.mockOidcBaseUrl, release: true),
          throwsA(isA<ConfigException>()));
    });

    test('the default (no release argument) follows the build mode: a debug test run is not a release', () {
      expect(AppConfig.resolve('').isMock, isTrue);
      expect(AppConfig.resolve('http://localhost:5080').oidcBaseUrl, 'http://localhost:5080');
      expect(AppConfig.fromEnvironment().isMock, isTrue);
    });

    test('plain http is refused in a release build, whatever the host', () {
      for (final u in [
        'http://oidc.biatec.io',
        'http://localhost:5080/x',
        'http://127.0.0.1:5080',
        'http://10.0.2.2:5080/other',
      ]) {
        expect(() => AppConfig.resolve(u, release: true),
            throwsA(isA<ConfigException>()),
            reason: u);
      }
    });

    test('in a debug build http is accepted for local hosts only', () {
      for (final u in [
        'http://localhost:5080',
        'http://127.0.0.1:5080/mock',
        'http://10.0.2.2:5080/other-mock',
      ]) {
        expect(AppConfig.resolve(u, release: false).oidcBaseUrl, u);
      }
      for (final u in ['http://oidc.biatec.io', 'http://192.168.1.20:5080']) {
        expect(() => AppConfig.resolve(u, release: false),
            throwsA(isA<ConfigException>()),
            reason: u);
      }
    });

    test('other schemes, credentials, queries, fragments and non-URLs are refused', () {
      for (final u in [
        'ftp://oidc.biatec.io',
        'file:///etc/passwd',
        'javascript:alert(1)',
        'https://user:pass@oidc.biatec.io',
        'https://oidc.biatec.io?x=1',
        'https://oidc.biatec.io#frag',
        'oidc.biatec.io',
        'https://',
      ]) {
        expect(() => AppConfig.resolve(u, release: false),
            throwsA(isA<ConfigException>()),
            reason: u);
      }
    });
  });

  testWidgets('a rejected URL shows why the build cannot start', (tester) async {
    await tester.pumpWidget(const ConfigErrorApp(message: 'OIDC_BASE_URL must be https.'));
    expect(find.byKey(const Key('config-error-title')), findsOneWidget);
    expect(find.text('OIDC_BASE_URL must be https.'), findsOneWidget);
  });
}
