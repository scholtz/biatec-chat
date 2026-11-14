// T-245: the approval card's "New service" banner and "don't ask again" boxes, and what an approval signs into `remember`.
import 'dart:convert';
import 'dart:io';

import 'package:biatec_authenticator/approvals/approval_card.dart';
import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_controller.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/consent_request.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/receipt.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'approval_test.dart' show FakeGate, FakeOidc, vectorNow, vectorRequest;
import 'support.dart';

const _remember = 'stepup-untrusted-application-approve-remember';

Map<String, Object?> _vector(String name) =>
    vectors('receipts.json').firstWhere((v) => v['name'] == name);

Map<String, Object?> _withLinked(Map<String, Object?> request, bool linked) => {
      ...request,
      'client': {
        ...(request['client'] as Map).cast<String, Object?>(),
        'linked': linked
      },
    };

Future<void> _card(WidgetTester tester, Map<String, Object?> json,
    {bool trust = true,
    Set<String> remembered = const {},
    void Function(String, bool)? onRemember}) async {
  tester.view.physicalSize = const Size(500, 4000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(Brightness.light),
    home: Scaffold(
      body: SingleChildScrollView(
          child: ApprovalCard(
              request: ConsentRequest.fromJson(json),
              trust: trust,
              remembered: remembered,
              onRemember: onRemember)),
    ),
  ));
}

class _Rig {
  _Rig(Map<String, Object?> request, {bool trust = true}) {
    oidc = FakeOidc(request, code: '864209');
    keys = SoftwareKeyStore(deviceTestKey());
    final store = MemoryEnrollmentStore()
      ..write(EnrollmentRecord(
        kid: 'alice-device',
        deviceToken: 'dev-token',
        activeAt: DateTime.utc(2020),
        attestation: 'h.p.s',
        presenceKid: 'alice-device-presence',
        presenceAttestation: 'h.p.presence',
      ));
    services = ApprovalServices(
      client: ApprovalClient(baseUrl: 'http://oidc', client: oidc.client),
      keys: keys,
      enrollment: ObservableEnrollmentStore(store),
      gate: FakeGate(),
      history: MemoryHistoryStore(),
      trustSettings: trust,
      clock: vectorNow,
    );
  }

  late final FakeOidc oidc;
  late final SoftwareKeyStore keys;
  late final ApprovalServices services;
}

/// Whether `node` runs here (the CI authenticator job has none): the verification with the protocol kit needs it.
final String? _nodeSkip = () {
  try {
    return Process.runSync('node', ['--version']).exitCode == 0
        ? null
        : 'node is not runnable, so lib.mjs verifyApproval cannot run';
  } on Object {
    return 'node is not installed, so lib.mjs verifyApproval cannot run';
  }
}();

void main() {
  group('New service banner', () {
    testWidgets('shown only when the service is not linked', (tester) async {
      final json = vectorRequest(_remember);
      await _card(tester, _withLinked(json, false));
      expect(find.byKey(const Key('new-service')), findsOneWidget);
      expect(find.text('New service'), findsOneWidget);
      expect(
          find.textContaining(
              'Approving links this service to your authenticator. It can then send you requests. You can unlink it in Settings.'),
          findsOneWidget);
      await _card(tester, _withLinked(json, true));
      expect(find.byKey(const Key('new-service')), findsNothing);
    });

    testWidgets('not shown with the trust settings off', (tester) async {
      await _card(tester, _withLinked(vectorRequest(_remember), false),
          trust: false);
      expect(find.byKey(const Key('new-service')), findsNothing);
      expect(find.byType(CheckboxListTile), findsNothing);
    });
  });

  group('"Don\'t ask again"', () {
    testWidgets('an unticked box for each untrusted application only',
        (tester) async {
      await _card(tester, vectorRequest(_remember), onRemember: (_, __) {});
      final box = find.byKey(const Key('remember-7654321'));
      expect(box, findsOneWidget);
      expect(tester.widget<CheckboxListTile>(box).value, isFalse);
      expect(
          find.textContaining(
              "Don't ask again for application 7654321 from chat.biatec.io while the amount is under my threshold"),
          findsOneWidget);
      // A trusted application has no box.
      await _card(tester,
          vectorRequest('stepup-transfer-plus-trusted-application-approve'),
          onRemember: (_, __) {});
      expect(find.byType(CheckboxListTile), findsNothing);
    });

    test(
        'ticking puts exactly that id in remember.applications, and the receipt verifies with lib.mjs',
        skip: _nodeSkip, () async {
      final v = _vector(_remember);
      final json = (v['request'] as Map).cast<String, Object?>();
      final rig = _Rig(json);
      final c = rig.services.controllerFor(json['auth_req_id'] as String);
      await c.load();
      expect(c.phase, ApprovalPhase.ready, reason: c.error);
      expect(c.remembered, isEmpty);
      c.setRemember('7654321', true);
      c.setRemember('999', true); // not offered: ignored
      expect(c.remembered, {'7654321'});
      await c.approve('864209');
      expect(c.phase, ApprovalPhase.approved, reason: c.error);
      final receipt = rig.oidc.receipts.single;
      expect((receipt['signed'] as Map)['remember'], {
        'applications': [7654321]
      });
      // The one prompt of the approval key covered it.
      expect(rig.keys.prompts, hasLength(1));
      // The protocol kit verifies the receipt this app signed (the device block and attestation of the vector's device, our own signature).
      final merged = {
        ...(v['receipt'] as Map).cast<String, Object?>(),
        'signed': receipt['signed'],
        'signature': receipt['signature'],
      };
      final proc =
          await Process.start('node', ['test/approvals/verify_receipt.mjs']);
      proc.stdin.write(
          jsonEncode({'receipt': merged, 'request': json, 'code': '864209'}));
      await proc.stdin.close();
      final out = await utf8.decodeStream(proc.stdout);
      expect(out.trim(), 'true', reason: await utf8.decodeStream(proc.stderr));
    });

    test('nothing ticked: no remember member', () async {
      final json = vectorRequest(_remember);
      final rig = _Rig(json);
      final c = rig.services.controllerFor(json['auth_req_id'] as String);
      await c.load();
      await c.approve('864209');
      expect(
          (rig.oidc.receipts.single['signed'] as Map).containsKey('remember'),
          isFalse);
    });

    test('Deny never carries remember, even with a box ticked', () async {
      final json = vectorRequest(_remember);
      final rig = _Rig(json);
      final c = rig.services.controllerFor(json['auth_req_id'] as String);
      await c.load();
      c.setRemember('7654321', true);
      await c.deny();
      expect(c.phase, ApprovalPhase.denied, reason: c.error);
      expect(
          (rig.oidc.receipts.single['signed'] as Map).containsKey('remember'),
          isFalse);
      // The builder itself refuses to put it on a deny.
      final request = ConsentRequest.fromJson(json);
      expect(
          signedObject(request,
                  code: '', decision: Decision.deny, remember: [7654321])
              .containsKey('remember'),
          isFalse);
    });

    test('with the trust settings off nothing is remembered', () async {
      final json = vectorRequest(_remember);
      final rig = _Rig(json, trust: false);
      final c = rig.services.controllerFor(json['auth_req_id'] as String);
      await c.load();
      c.setRemember('7654321', true);
      expect(c.remembered, isEmpty);
      await c.approve('864209');
      expect(
          (rig.oidc.receipts.single['signed'] as Map).containsKey('remember'),
          isFalse);
    });

    test('rememberable is computed once and holds at most 16', () {
      final json = (jsonDecode(jsonEncode(vectorRequest(_remember))) as Map)
          .cast<String, Object?>();
      final d = (json['authorization_details'] as List).single as Map;
      d['applications'] = [
        for (var i = 1; i <= 16; i++) {'id': i, 'trusted': false}
      ];
      final r = ConsentRequest.fromJson(json);
      expect(identical(r.rememberable, r.rememberable), isTrue);
      expect(r.rememberable, hasLength(ConsentRequest.maxRemembered));
      // A request lists at most 16 applications (the schema), so nothing is cut off here; the cap is the receipt's own limit.
      expect(r.rememberableTruncated, isFalse);
      expect(ConsentRequest.maxRemembered, 16);
      expect(r.canRemember(r.rememberable.first), isTrue);
      expect(r.canRemember(const ApplicationEntry(id: '20', trusted: false)), isFalse);
    });

    test('a request that changed before approving clears what was ticked', () async {
      final json = vectorRequest(_remember);
      final rig = _Rig(json);
      final c = rig.services.controllerFor(json['auth_req_id'] as String);
      await c.load();
      c.setRemember('7654321', true);
      expect(c.remembered, {'7654321'});
      rig.oidc.request = {...json, 'binding_message': 'changed text'};
      await c.approve('864209');
      expect(c.error, contains('changed'));
      expect(c.remembered, isEmpty);
      expect(rig.oidc.receipts, isEmpty);
    });

    test('the prompt label and the card agree: a non-ASCII proven domain is unproven', () {
      final json = (jsonDecode(jsonEncode(vectorRequest(_remember))) as Map).cast<String, Object?>();
      json['client'] = {
        'fingerprint': '68dcf7ce89a943b0',
        'domain_proven': true,
        'domain': 'bücher.example',
        'linked': true
      };
      final c = ConsentRequest.fromJson(json).client;
      expect(clientLabel(c), 'Unproven application 68dc-f7ce-89a9-43b0');
    });

    test('an application creation (id 0) is never offered', () {
      final json = (jsonDecode(jsonEncode(vectorRequest(_remember))) as Map)
          .cast<String, Object?>();
      final d = (json['authorization_details'] as List).single as Map;
      d['applications'] = [
        {'id': 0, 'trusted': false}
      ];
      expect(ConsentRequest.fromJson(json).rememberable, isEmpty);
    });
  });
}
