import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/security/pinning/pin_policy.dart';
import 'package:biatec_authenticator/security/pinning/pins.dart';
import 'package:biatec_authenticator/security/pinning/spki_pin.dart';
import 'package:flutter_test/flutter_test.dart';

/// The DER bytes of the certificate in a PEM fixture (lines starting with # are comments).
Uint8List derOf(String fixture) {
  final text = File('test/pinning/fixtures/$fixture.pem').readAsStringSync();
  final body = text
      .split(RegExp(r'\r?\n'))
      .where(
          (l) => l.isNotEmpty && !l.startsWith('#') && !l.startsWith('-----'))
      .join();
  return base64.decode(body);
}

// The keys of the public certificates in test/pinning/fixtures, computed with openssl (x509 -pubkey | pkey -pubin -outform DER | sha256).
const leafPin = 'z8N92PhmB13Pcufe5VrnS1/Z14cMYcjKqhaQYdVJ1IM=';
const intermediatePin = 'nWN7PSep5XDQdge5zK24CnCRXHr3KvzhKEGxsdqCX9E=';
const rootPin = 'fk6IOKit1ild5647BH06ujSIq5XbCgqlbYl6ANhhi88=';

AppConfig https(String url) => AppConfig(oidcBaseUrl: url, isMock: false);

void main() {
  group('SpkiPin', () {
    test('computes the pin of each certificate the way openssl does', () {
      expect(SpkiPin.fromCertificate(derOf('stage_leaf')), leafPin);
      expect(SpkiPin.fromCertificate(derOf('stage_intermediate_yr2')),
          intermediatePin);
      expect(SpkiPin.fromCertificate(derOf('stage_root_yr')), rootPin);
    });

    test('the extracted key is a whole DER element that starts with a SEQUENCE',
        () {
      final spki = SpkiPin.extractSpki(derOf('stage_leaf'));
      expect(spki.first, 0x30);
      expect(spki.length, greaterThan(100));
    });

    test('garbage, truncated and empty input are refused, not crashed on', () {
      final good = derOf('stage_leaf');
      for (final bad in [
        Uint8List(0),
        Uint8List.fromList([1, 2, 3]),
        Uint8List.sublistView(good, 0, 40),
        Uint8List.sublistView(good, 0, good.length - 50),
        Uint8List.fromList(List.filled(100, 0xFF)),
        Uint8List.fromList([0x30, 0x84, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0]),
        Uint8List.fromList([0x30, 0x80]),
      ]) {
        expect(() => SpkiPin.fromCertificate(bad),
            throwsA(isA<CertificateFormatException>()));
      }
    });

    test('parses pins as written in configuration', () {
      expect(SpkiPin.parse('sha256/$leafPin'), leafPin);
      expect(SpkiPin.parse(leafPin), leafPin);
      expect(SpkiPin.parse('  sha256/$leafPin '), leafPin);
      // 32 bytes without the padding character.
      expect(SpkiPin.parse(leafPin.replaceAll('=', '')), leafPin);
      for (final bad in [
        '',
        'sha256/',
        'sha1/AAAA',
        'not base64!',
        'AAAA',
        base64.encode(List.filled(31, 1)),
        base64.encode(List.filled(33, 1))
      ]) {
        expect(SpkiPin.parse(bad), isNull, reason: bad);
      }
    });
  });

  group('PinPolicy', () {
    final leaf = derOf('stage_leaf');
    final notBefore = DateTime.utc(2026, 9, 26);
    final notAfter = DateTime.utc(2026, 12, 25);

    PinPolicy policy({DateTime? now, Map<String, List<String>>? pins}) =>
        PinPolicy(
            pins: pins ??
                {
                  'stage.oidc.biatec.io': [leafPin]
                },
            clock: () => now ?? DateTime.utc(2026, 10, 28));

    test('accepts the pinned key inside its validity', () {
      expect(policy().check('stage.oidc.biatec.io', leaf, notBefore, notAfter),
          PinDecision.accepted);
    });

    test('host names are not case sensitive', () {
      expect(policy().check('STAGE.oidc.Biatec.io', leaf, notBefore, notAfter),
          PinDecision.accepted);
    });

    test(
        'refuses a key that is not pinned, a host without pins, and other hosts',
        () {
      final p = policy(pins: {
        'stage.oidc.biatec.io': [intermediatePin]
      });
      expect(p.check('stage.oidc.biatec.io', leaf, notBefore, notAfter),
          PinDecision.pinMismatch);
      expect(policy().check('evil.example', leaf, notBefore, notAfter),
          PinDecision.noPinsForHost);
      expect(policy().check('oidc.biatec.io', leaf, notBefore, notAfter),
          PinDecision.noPinsForHost);
      expect(
          PinPolicy.none()
              .check('stage.oidc.biatec.io', leaf, notBefore, notAfter),
          PinDecision.noPinsForHost);
    });

    test('a backup pin is accepted as well as the current one', () {
      final p = policy(pins: {
        'stage.oidc.biatec.io': [rootPin, leafPin]
      });
      expect(p.check('stage.oidc.biatec.io', leaf, notBefore, notAfter),
          PinDecision.accepted);
    });

    test('a pinned key is refused before and after its certificate is valid',
        () {
      expect(
          policy(now: DateTime.utc(2026, 9, 1))
              .check('stage.oidc.biatec.io', leaf, notBefore, notAfter),
          PinDecision.notYetValid);
      expect(
          policy(now: DateTime.utc(2027, 1, 1))
              .check('stage.oidc.biatec.io', leaf, notBefore, notAfter),
          PinDecision.expired);
    });

    test('an unreadable certificate is refused', () {
      expect(
          policy().check(
              'stage.oidc.biatec.io', Uint8List(10), notBefore, notAfter),
          PinDecision.unreadable);
    });

    test('a pin that is not a pin is refused when the policy is made', () {
      expect(
          () => PinPolicy(pins: {
                'a': ['nope']
              }),
          throwsArgumentError);
    });
  });

  group('the pins of a build', () {
    PinPolicy build(String url,
            {String? defined, bool release = true, bool mobile = true}) =>
        pinPolicyFor(https(url),
            definedPins: defined ?? '', release: release, mobile: mobile);

    test(
        'the default table has the stage key, every pin is well formed, and no host is empty',
        () {
      expect(defaultOidcPins['stage.oidc.biatec.io'], isNotNull);
      for (final e in defaultOidcPins.entries) {
        expect(e.value, isNotEmpty, reason: e.key);
        for (final p in e.value) {
          expect(SpkiPin.parse(p), isNotNull, reason: '${e.key} $p');
        }
      }
    });

    test('the stage default is a leaf pin that matches the real certificate',
        () {
      expect(SpkiPin.parse(defaultOidcPins['stage.oidc.biatec.io']!.single),
          leafPin);
    });

    test('the mock and a plain-http developer URL need no pins', () {
      expect(
          pinPolicyFor(AppConfig.resolve(''), definedPins: '', release: false)
              .isEmpty,
          isTrue);
      expect(
          pinPolicyFor(AppConfig.resolve('http://localhost:5080'),
                  definedPins: '', release: false)
              .isEmpty,
          isTrue);
    });

    test(
        'a release build on a phone needs two pins; one is a start-up error that says so',
        () {
      expect(
          () => build('https://stage.oidc.biatec.io'),
          throwsA(isA<ConfigException>()
              .having((e) => e.message, 'message', contains('at least two'))));
      final ok = build('https://stage.oidc.biatec.io',
          defined: 'sha256/$leafPin,sha256/$rootPin');
      expect(ok.pinsFor('stage.oidc.biatec.io'), {leafPin, rootPin});
    });

    test(
        'the same two pins can be written with spaces and duplicates do not count twice',
        () {
      final ok = build('https://stage.oidc.biatec.io',
          defined: 'sha256/$leafPin  sha256/$rootPin');
      expect(ok.pinsFor('stage.oidc.biatec.io').length, 2);
      expect(
          () => build('https://stage.oidc.biatec.io',
              defined: 'sha256/$leafPin,sha256/$leafPin'),
          throwsA(isA<ConfigException>()));
    });

    test('desktop and debug builds need one', () {
      expect(
          build('https://stage.oidc.biatec.io', mobile: false)
              .pinsFor('stage.oidc.biatec.io'),
          hasLength(1));
      expect(
          build('https://stage.oidc.biatec.io', release: false)
              .pinsFor('stage.oidc.biatec.io'),
          hasLength(1));
    });

    test(
        'there is no unpinned HTTPS: an unknown host fails to start in every build',
        () {
      for (final release in [true, false]) {
        for (final mobile in [true, false]) {
          expect(
              () => build('https://oidc.biatec.io',
                  release: release, mobile: mobile),
              throwsA(isA<ConfigException>().having((e) => e.message, 'message',
                  contains('no certificate pins'))),
              reason: 'release $release mobile $mobile');
        }
      }
    });

    test(
        'a pin passed in the build replaces the default, and a bad one is an error',
        () {
      final p = build('https://oidc.biatec.io',
          defined: 'sha256/$leafPin,sha256/$rootPin');
      expect(p.pinsFor('oidc.biatec.io'), {leafPin, rootPin});
      expect(
          () => build('https://oidc.biatec.io',
              defined: 'sha256/$leafPin,garbage'),
          throwsA(isA<ConfigException>()));
    });
  });
}
