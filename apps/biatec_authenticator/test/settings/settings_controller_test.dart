import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support.dart';

ConsentSettings _settings({
  String threshold = '1000.00',
  bool biometric = true,
  bool block = false,
  List<ClientSetting>? clients,
}) =>
    ConsentSettings(
      sub: 'pw_alice',
      biometricRequired: biometric,
      thresholdEur: threshold,
      blockUnverifiedClients: block,
      clients: clients ??
          [
            const ClientSetting(
                clientId: 'chat',
                label: 'Chat',
                verified: true,
                alwaysAsk: true,
                perClientThresholdEur: '200.00',
                pausedUntil: null),
            ClientSetting(
                clientId: 'bank',
                label: 'Bank',
                verified: true,
                alwaysAsk: false,
                perClientThresholdEur: null,
                pausedUntil: DateTime.utc(2026, 10, 21)),
          ],
    );

void main() {
  group('isLoosening', () {
    final s = _settings();
    test('raising a threshold loosens, lowering or keeping does not', () {
      expect(isLoosening(s, {'threshold_eur': '1500'}), isTrue);
      expect(isLoosening(s, {'threshold_eur': '1000'}), isFalse);
      expect(isLoosening(s, {'threshold_eur': '10'}), isFalse);
    });

    test('switching a requirement off loosens, on does not', () {
      expect(isLoosening(s, {'biometric_required': false}), isTrue);
      expect(isLoosening(s, {'biometric_required': true}), isFalse);
      expect(isLoosening(_settings(block: true), {'block_unverified_clients': false}),
          isTrue);
      expect(isLoosening(s, {'block_unverified_clients': true}), isFalse);
      // Already off: switching it off again changes nothing.
      expect(isLoosening(s, {'block_unverified_clients': false}), isFalse);
    });

    test('per-client changes', () {
      Map<String, Object?> one(String id, Map<String, Object?> c) => {
            'clients': {id: c}
          };
      expect(isLoosening(s, one('chat', {'always_ask': false})), isTrue);
      expect(isLoosening(s, one('bank', {'always_ask': true})), isFalse);
      expect(isLoosening(s, one('chat', {'per_client_threshold_eur': '300'})),
          isTrue);
      expect(isLoosening(s, one('chat', {'per_client_threshold_eur': '100'})),
          isFalse);
      // Back to the account threshold (1000) raises 200.
      expect(isLoosening(s, one('chat', {'per_client_threshold_eur': null})),
          isTrue);
      // A client without its own threshold follows the account one (1000).
      expect(isLoosening(s, one('bank', {'per_client_threshold_eur': '50'})),
          isFalse);
      expect(isLoosening(s, one('bank', {'per_client_threshold_eur': '5000'})),
          isTrue);
      expect(isLoosening(s, one('bank', {'paused': false})), isTrue);
      expect(isLoosening(s, one('chat', {'paused': false})), isFalse);
    });

    test('anything unreadable or unknown counts as loosening', () {
      expect(isLoosening(s, {'threshold_eur': 'abc'}), isTrue);
      expect(isLoosening(s, {
        'clients': {
          'ghost': {'always_ask': true}
        }
      }), isTrue);
    });

    test('a mixed change loosens if any part does', () {
      expect(
          isLoosening(s, {'threshold_eur': '10', 'biometric_required': false}),
          isTrue);
    });
  });

  group('SettingsController', () {
    test('load fetches devices and settings', () async {
      final rig = SettingsRig();
      final c = rig.controller();
      await c.load();
      expect(c.phase, SettingsPhase.ready, reason: c.error);
      expect(c.devices.map((d) => d.kid), ['dev-1', 'dev-2']);
      expect(c.devices.last.pending, isTrue);
      expect(c.devices.first.current, isTrue);
      expect(c.settings!.thresholdEur, '1000.00');
      expect(c.settings!.clients, hasLength(3));
    });

    test('offline: load is unavailable and says it is offline', () async {
      final rig = SettingsRig(oidc: FakeSettingsOidc()..offline = true);
      final c = rig.controller();
      await c.load();
      expect(c.phase, SettingsPhase.unavailable);
      expect(c.offline, isTrue);
      expect(c.error, contains('not reachable'));
    });

    test('a device that is not enrolled cannot load', () async {
      final rig = SettingsRig();
      await rig.enrollment.clear();
      final c = rig.controller();
      await c.load();
      expect(c.phase, SettingsPhase.unavailable);
      expect(c.error, 'This device is not enrolled.');
    });

    test('cancelling a pending enrollment removes it without a biometric',
        () async {
      final rig = SettingsRig();
      final c = rig.controller();
      await c.load();
      await c.cancelDevice('dev-2');
      expect(rig.oidc.cancelled, ['dev-2']);
      expect(c.devices.map((d) => d.kid), ['dev-1']);
      expect(c.notice, 'Enrollment cancelled.');
      expect(c.error, isNull);
      expect(rig.gate.asked, 0);
    });

    test('a failed refresh after a successful cancel still reports success',
        () async {
      final rig = SettingsRig();
      final c = rig.controller();
      await c.load();
      rig.oidc.failDevicesList = true;
      await c.cancelDevice('dev-2');
      expect(rig.oidc.cancelled, ['dev-2']);
      expect(c.devices.map((d) => d.kid), ['dev-1']);
      expect(c.notice, 'Enrollment cancelled.');
      expect(c.error, isNull);
    });

    test('cancelling an active device is refused and explained', () async {
      final rig = SettingsRig();
      final c = rig.controller();
      await c.load();
      await c.cancelDevice('dev-1');
      expect(c.error, 'Only a pending enrollment can be cancelled.');
      expect(rig.oidc.cancelled, isEmpty);
      expect(c.devices, hasLength(2));
    });

    test('cancelling this device signs it out and deletes its enrollment',
        () async {
      final oidc = FakeSettingsOidc()
        ..devices = [
          {
            'kid': 'dev-1',
            'current': true,
            'active': false,
            'active_at': 1792425600 + 3600
          }
        ];
      final rig = SettingsRig(oidc: oidc);
      final c = rig.controller();
      await c.load();
      await c.cancelDevice('dev-1');
      expect(await rig.enrollment.read(), isNull);
      expect(c.phase, SettingsPhase.unavailable);
      expect(c.error, contains('cancelled'));
    });

    test('tightening applies at once: no biometric, no signature', () async {
      final rig = SettingsRig();
      final c = rig.controller();
      await c.load();
      expect(await c.apply({'threshold_eur': '500'}), isTrue, reason: c.error);
      expect(c.settings!.thresholdEur, '500');
      expect(rig.gate.asked, 0);
      expect(rig.keys.signed, isEmpty);
      expect(rig.oidc.puts.single.containsKey('consent'), isFalse);
      expect(c.notice, 'Applied.');
    });

    test('loosening is signed on this device and the server verifies it',
        () async {
      final rig = SettingsRig();
      final c = rig.controller();
      await c.load();
      expect(await c.apply({'threshold_eur': '2000'}), isTrue, reason: c.error);
      expect(c.settings!.thresholdEur, '2000');
      expect(rig.gate.asked, 1);
      expect(rig.keys.signed, hasLength(1));
      final consent =
          (rig.oidc.puts.single['consent'] as Map).cast<String, Object?>();
      final signed = (consent['signed'] as Map).cast<String, Object?>();
      expect(signed['type'], 'biatec_settings_change');
      expect(signed['sub'], 'pw_alice');
      expect(signed['kid'], 'dev-1');
      expect(signed['nonce'], 'nonce-1');
      expect(signed['issued_at'], rig.oidc.nowSeconds);
      expect(consent['alg'], 'ES256');
      expect(c.notice, 'Approved on this device and applied.');
    });

    test('each loosening uses a new nonce', () async {
      final rig = SettingsRig();
      final c = rig.controller();
      await c.load();
      expect(await c.apply({'threshold_eur': '2000'}), isTrue);
      expect(await c.apply({'biometric_required': false}), isTrue);
      final nonces = [
        for (final p in rig.oidc.puts)
          ((p['consent'] as Map)['signed'] as Map)['nonce']
      ];
      expect(nonces, ['nonce-1', 'nonce-2']);
    });

    test('a failed biometric check sends nothing', () async {
      final rig = SettingsRig();
      rig.gate.pass = false;
      final c = rig.controller();
      await c.load();
      expect(await c.apply({'threshold_eur': '2000'}), isFalse);
      expect(c.error, 'Biometric check did not pass.');
      expect(rig.oidc.puts, isEmpty);
      expect(c.settings!.thresholdEur, '1000.00');
    });

    test('an inactive device cannot loosen but can still tighten', () async {
      final rig = SettingsRig(activeAt: DateTime.utc(2030));
      final c = rig.controller();
      await c.load();
      expect(await c.apply({'threshold_eur': '2000'}), isFalse);
      expect(c.error, contains('not active yet'));
      expect(rig.oidc.puts, isEmpty);
      expect(rig.gate.asked, 0);
      expect(await c.apply({'threshold_eur': '100'}), isTrue, reason: c.error);
    });

    test('lifting a pause is a signed consent', () async {
      final oidc = FakeSettingsOidc();
      oidc.clientById('bank-example')['paused_until'] = oidc.nowSeconds + 3600;
      final rig = SettingsRig(oidc: oidc);
      final c = rig.controller();
      await c.load();
      expect(c.settings!.client('bank-example')!.pausedUntil, isNotNull);
      expect(
          await c.apply({
            'clients': {
              'bank-example': {'paused': false}
            }
          }),
          isTrue,
          reason: c.error);
      expect(c.settings!.client('bank-example')!.pausedUntil, isNull);
      expect(rig.gate.asked, 1);
      expect(oidc.puts.single['consent'], isNotNull);
    });

    test('turning always-ask off is signed; turning it on is not', () async {
      final oidc = FakeSettingsOidc();
      oidc.clientById('biatec-chat')['always_ask'] = true;
      final rig = SettingsRig(oidc: oidc);
      final c = rig.controller();
      await c.load();
      await c.apply({
        'clients': {
          'biatec-chat': {'always_ask': false}
        }
      });
      expect(rig.gate.asked, 1);
      await c.apply({
        'clients': {
          'biatec-chat': {'always_ask': true}
        }
      });
      expect(rig.gate.asked, 1, reason: 'tightening needs no biometric');
      expect(oidc.puts.last.containsKey('consent'), isFalse);
      expect(c.settings!.client('biatec-chat')!.alwaysAsk, isTrue);
    });

    test('when the server demands consent for a change judged tightening, the '
        'device signs and retries', () async {
      final oidc = FakeSettingsOidc()..alwaysRequireConsent = true;
      final rig = SettingsRig(oidc: oidc);
      final c = rig.controller();
      await c.load();
      expect(await c.apply({'threshold_eur': '500'}), isTrue, reason: c.error);
      expect(oidc.puts, hasLength(2));
      expect(oidc.puts.first.containsKey('consent'), isFalse);
      expect(oidc.puts.last['consent'], isNotNull);
      expect(rig.gate.asked, 1);
    });

    test('a refused change leaves the settings as they were', () async {
      final rig = SettingsRig();
      final c = rig.controller();
      await c.load();
      rig.oidc.offline = true;
      expect(await c.apply({'threshold_eur': '500'}), isFalse);
      expect(c.offline, isTrue);
      expect(c.settings!.thresholdEur, '1000.00');
    });
  });
}
