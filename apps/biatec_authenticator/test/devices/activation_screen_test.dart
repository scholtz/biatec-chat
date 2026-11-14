// T-242: an active device offers "Activate <name>?" for a pending device of the same account, with the pairing code worked out from the keys Biatec OIDC lists;
// confirming signs `biatec_device_approval` with the approval key (one prompt) and posts it to `POST /devices/{kid}/activate`.
import 'dart:convert';

import 'package:biatec_authenticator/approvals/jcs.dart';
import 'package:biatec_authenticator/devices/devices_screen.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../approvals/support.dart';
import '../settings/support.dart';

const _approvalThumb = 'cpXj3K7c4_bBd9lXxKoO8aZPwTwuPMGzwnYgg3rG_jY';
const _presenceThumb = 'hOlyodNi_jjK_piu5kSIQkvt2q5zwnCUtO8iVYQtwlw';
const _nonce = 'enroll-nonce-53bcbb91525547bf679e9d4f';

Map<String, Object?> _pending({String? serverCode = '102716', String? name = 'Test phone'}) => {
      'kid': 'dev-2',
      'current': false,
      'active': false,
      'active_at': 1792425600 + 86400,
      if (name != null) 'name': name,
      'pairing': {
        'new_approval_jwk_thumbprint': _approvalThumb,
        'new_presence_jwk_thumbprint': _presenceThumb,
        'nonce': _nonce,
        if (serverCode != null) 'pairing_code': serverCode,
      },
    };

/// The settings rig with `POST /devices/dev-2/activate` answered like the mock (the device becomes active) and recorded.
class _Rig {
  _Rig({Map<String, Object?>? pending}) : rig = SettingsRig() {
    rig.oidc.devices = [
      {'kid': 'dev-1', 'current': true, 'active': true, 'active_at': 0},
      pending ?? _pending(),
    ];
    final inner = rig.oidc.client;
    final wrapped = MockClient((req) async {
      if (req.method == 'POST' && req.url.path == '/devices/dev-2/activate') {
        activations.add((jsonDecode(req.body) as Map).cast<String, Object?>());
        if (refuse) {
          return http.Response(jsonEncode({'error': 'invalid_approval'}), 403);
        }
        final i = rig.oidc.devices.indexWhere((d) => d['kid'] == 'dev-2');
        rig.oidc.devices[i] = {
          ...rig.oidc.devices[i],
          'active': true,
          'active_at': rig.oidc.nowSeconds
        }..remove('pairing');
        return http.Response(jsonEncode({'kid': 'dev-2', 'active': true}), 200);
      }
      // A request can be sent once: the same call is made again on the rig's own fake.
      final again = http.Request(req.method, req.url)
        ..headers.addAll(req.headers)
        ..body = req.body;
      return http.Response.fromStream(await inner.send(again));
    });
    services = SettingsServices(
      client: SettingsClient(baseUrl: 'http://oidc.test', client: wrapped),
      keys: rig.keys,
      enrollment: rig.enrollment,
      gate: rig.gate,
      clock: () => DateTime.fromMillisecondsSinceEpoch(rig.oidc.nowSeconds * 1000,
          isUtc: true),
      nonce: () => 'n',
    );
  }

  final SettingsRig rig;
  late final SettingsServices services;
  final List<Map<String, Object?>> activations = [];
  bool refuse = false;
}

Future<void> _open(WidgetTester tester, _Rig r) async {
  tester.view.physicalSize = const Size(400, 2000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(MaterialApp(
      theme: buildTheme(Brightness.light),
      home: DevicesScreen(services: r.services)));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('a pending device of this account is offered for activation, with the pairing code',
      (tester) async {
    final r = _Rig();
    await _open(tester, r);
    expect(find.text('Activate Test phone?'), findsOneWidget);
    expect(find.byKey(const Key('pairing-code-dev-2')), findsOneWidget);
    expect(find.text('102 716'), findsOneWidget);
    expect(find.byKey(const Key('activate-dev-2')), findsOneWidget);
    // This device and an active device have nothing to activate.
    expect(find.byKey(const Key('activate-dev-1')), findsNothing);
    // The cancel button is there, worded for this case.
    expect(find.text('Cancel this device'), findsOneWidget);
  });

  testWidgets('a device with no name is called "a new device"', (tester) async {
    final r = _Rig(pending: _pending(name: null));
    await _open(tester, r);
    expect(find.text('Activate a new device?'), findsOneWidget);
  });

  testWidgets('confirming signs the approval with the approval key, once, and posts it',
      (tester) async {
    final r = _Rig();
    await _open(tester, r);
    await tester.tap(find.byKey(const Key('activate-dev-2')));
    await tester.pumpAndSettle();
    expect(find.text('Do the codes match?'), findsOneWidget);
    expect(r.activations, isEmpty, reason: 'nothing is sent before the user confirms');
    await tester.tap(find.byKey(const Key('confirm-activate')));
    await tester.pumpAndSettle();

    // One approval prompt, none for the presence key.
    expect(r.rig.keys.prompts, hasLength(1));
    expect(r.rig.keys.presenceSigned, isEmpty);
    final body = r.activations.single['approval'] as Map;
    final signed = (body['signed'] as Map).cast<String, Object?>();
    expect(signed['type'], 'biatec_device_approval');
    expect(signed['approver_kid'], 'dev-1');
    expect(signed['new_kid'], 'dev-2');
    expect(signed['new_approval_jwk_thumbprint'], _approvalThumb);
    expect(signed['new_presence_jwk_thumbprint'], _presenceThumb);
    expect(signed['pairing_code'], '102716');
    expect(signed['nonce'], _nonce);
    expect(signed['issued_at'], r.rig.oidc.nowSeconds);
    expect((body['device'] as Map)['key_use'], 'approve');
    expect((body['device'] as Map)['attestation'], 'att');
    expect(
        verifyEs256(deviceTestKey(), canonicalBytes(signed),
            base64Url.decode(base64Url.normalize(body['signature'] as String))),
        isTrue,
        reason: 'the signature is the approval key\'s over the canonical statement');
    expect(body['alg'], 'ES256');
    // The screen shows the outcome and the list now has it active.
    expect(find.text('Test phone is active.'), findsOneWidget);
    expect(find.byKey(const Key('activate-dev-2')), findsNothing);
  });

  testWidgets('keeping the device pending sends nothing and asks for no biometric',
      (tester) async {
    final r = _Rig();
    await _open(tester, r);
    await tester.tap(find.byKey(const Key('activate-dev-2')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('keep-activate')));
    await tester.pumpAndSettle();
    expect(r.activations, isEmpty);
    expect(r.rig.keys.prompts, isEmpty);
    expect(find.byKey(const Key('activate-dev-2')), findsOneWidget);
  });

  testWidgets('a cancelled biometric prompt activates nothing', (tester) async {
    final r = _Rig();
    r.rig.keys.cancelNextPrompt = true;
    await _open(tester, r);
    await tester.tap(find.byKey(const Key('activate-dev-2')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('confirm-activate')));
    await tester.pumpAndSettle();
    expect(r.activations, isEmpty);
    expect(find.text('Cancelled. Nothing was activated.'), findsOneWidget);
  });

  testWidgets('a pairing code that Biatec OIDC states wrongly is not offered',
      (tester) async {
    final r = _Rig(pending: _pending(serverCode: '000000'));
    await _open(tester, r);
    expect(find.byKey(const Key('activate-dev-2')), findsNothing);
    expect(find.byKey(const Key('pairing-code-dev-2')), findsNothing);
    // The enrollment can still be cancelled.
    expect(find.byKey(const Key('cancel-dev-2')), findsOneWidget);
  });

  testWidgets('a refusal by Biatec OIDC is shown, not swallowed', (tester) async {
    final r = _Rig()..refuse = true;
    await _open(tester, r);
    await tester.tap(find.byKey(const Key('activate-dev-2')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('confirm-activate')));
    await tester.pumpAndSettle();
    expect(r.activations, hasLength(1));
    expect(find.textContaining('Could not activate it (403)'), findsOneWidget);
    expect(find.byKey(const Key('activate-dev-2')), findsOneWidget);
  });

  test('the code comes from the listed keys, whatever the server says it is', () {
    String? code(Map<String, Object?> d) =>
        SettingsController.pairingCodeOf(DeviceInfo.fromJson(d));
    expect(code(_pending()), '102716');
    expect(code(_pending(serverCode: null)), '102716');
    expect(code(_pending(serverCode: '999999')), isNull);
    expect(
        code({
          'kid': 'x',
          'active': false,
          'active_at': 0,
          'pairing': {
            'new_approval_jwk_thumbprint': 'short',
            'new_presence_jwk_thumbprint': _presenceThumb,
            'nonce': _nonce
          }
        }),
        isNull);
    expect(code({'kid': 'x', 'active': false, 'active_at': 0}), isNull);
  });
}
