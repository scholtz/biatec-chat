import 'dart:io';

import 'package:biatec_authenticator/security/pinning/pins.dart';
import 'package:biatec_authenticator/security/pinning/spki_pin.dart';
import 'package:flutter_test/flutter_test.dart';

String read(String path) => File(path).readAsStringSync();

/// The root pins the native files carry (not the Dart client's leaf pins).
const rootPins = [
  'C5+lpZ7tcVwmwQIMcRtPbsQtWLABXhQzejna0wHFr8M=', // ISRG Root X1
  'fk6IOKit1ild5647BH06ujSIq5XbCgqlbYl6ANhhi88=', // ISRG Root YR
  'diGVwiVYbubAI3RW4hB9xU8e/CH2GnkuvVFZE8zmgzI=', // ISRG Root X2
];

void main() {
  group('Android network security config', () {
    final main =
        read('android/app/src/main/res/xml/network_security_config.xml');
    final debug =
        read('android/app/src/debug/res/xml/network_security_config.xml');
    final manifest = read('android/app/src/main/AndroidManifest.xml');

    test('is used by the app', () {
      expect(
          manifest,
          contains(
              'android:networkSecurityConfig="@xml/network_security_config"'));
    });

    test('release: no cleartext, system authorities only, no user certificates',
        () {
      expect(main, contains('<base-config cleartextTrafficPermitted="false">'));
      expect(main, contains('<certificates src="system" />'));
      expect(main, isNot(contains('src="user"')));
      expect(main, isNot(contains('cleartextTrafficPermitted="true"')));
      expect(main, isNot(contains('debug-overrides')));
    });

    test(
        'pins Biatec OIDC to the roots, with a backup, and the pins agree with the other files',
        () {
      expect('<pin digest="SHA-256">'.allMatches(main).length,
          greaterThanOrEqualTo(2));
      for (final pin in rootPins) {
        expect(main, contains('>$pin<'), reason: pin);
        expect(read('ios/Runner/Info.plist'), contains('<string>$pin</string>'),
            reason: pin);
        expect(read('../../docs/AUTHENTICATOR-PINNING.md'), contains(pin),
            reason: pin);
        expect(SpkiPin.parse(pin), pin);
      }
      for (final host in defaultOidcPins.keys) {
        expect(main, contains('>$host</domain>'));
        expect(read('ios/Runner/Info.plist'), contains('<key>$host</key>'));
      }
    });

    test(
        'the pins do not run out within 60 days (Android stops enforcing them after that date)',
        () {
      final m = RegExp(r'<pin-set expiration="(\d{4})-(\d{2})-(\d{2})">')
          .firstMatch(main);
      expect(m, isNotNull);
      final date = DateTime.utc(int.parse(m!.group(1)!), int.parse(m.group(2)!),
          int.parse(m.group(3)!));
      expect(date.isAfter(DateTime.now().toUtc().add(const Duration(days: 60))),
          isTrue,
          reason:
              'move the expiration forward (docs/AUTHENTICATOR-PINNING.md)');
    });

    test('debug: plain http for this machine and nowhere else', () {
      final allowed = RegExp(
              r'<domain-config cleartextTrafficPermitted="true">(.*?)</domain-config>',
              dotAll: true)
          .firstMatch(debug);
      expect(allowed, isNotNull);
      final domains = RegExp(r'>([^<]+)</domain>')
          .allMatches(allowed!.group(1)!)
          .map((x) => x.group(1))
          .toSet();
      expect(domains, {'10.0.2.2', 'localhost', '127.0.0.1'});
      expect(
          debug, contains('<base-config cleartextTrafficPermitted="false">'));
    });
  });

  group('iOS', () {
    final plist = read('ios/Runner/Info.plist');

    test('pins the domains and has no exceptions', () {
      expect(plist, contains('NSPinnedDomains'));
      expect(plist, contains('NSPinnedCAIdentities'));
      expect(plist, isNot(contains('NSAllowsArbitraryLoads')));
      expect(plist, isNot(contains('NSExceptionDomains')));
      expect(plist, isNot(contains('NSAllowsLocalNetworking')));
      expect('SPKI-SHA256-BASE64'.allMatches(plist).length,
          greaterThanOrEqualTo(2));
    });
  });

  test(
      'the documentation exists and says how to compute a pin and how to rotate',
      () {
    final doc = read('../../docs/AUTHENTICATOR-PINNING.md');
    expect(doc, contains('openssl pkey -pubin -outform DER'));
    expect(doc, contains('Rotating a key'));
    expect(doc, contains('backup'));
    expect(doc, contains('rotationPolicy: Never'));
  });
}
