// T-247: the link screen and its controller against a scripted Biatec OIDC: what is shown, that the code is never submitted by itself, what is signed and by which key, the extra confirmation for
// a service without a proven domain, wrong codes, a link that is gone.
import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/jcs.dart';
import 'package:biatec_authenticator/approvals/widgets/approval_actions.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:biatec_authenticator/links/link_code.dart';
import 'package:biatec_authenticator/links/link_controller.dart';
import 'package:biatec_authenticator/links/link_screen.dart';
import 'package:biatec_authenticator/links/link_services.dart';
import 'package:biatec_authenticator/text/rp_strings.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../approvals/rp_support.dart' show nodeSkip, pinnedDevices, runNode;
import '../approvals/support.dart';

const _origin = 'https://oidc.biatec.test';
const _linkId = 'lnk_6f24862262c182126431e3a6';
const _secret = 'c6cWUY11Jq3nvnZ0yLm821tPt4OJSKXr_j070m1UOL4';
const _code = '620418';
const _nonce = 'link-nonce-d3a69547bc93fba9dff105d9';
const _fingerprint = 'b70156d0932b8095';

/// 1792425630, the time of the link vectors.
DateTime _now() =>
    DateTime.fromMillisecondsSinceEpoch(1792425630 * 1000, isUtc: true);

class FakeLinkServer {
  FakeLinkServer({this.domainProven = true, this.userLabel = 'Alex M. (account ending 4421)'});

  bool domainProven;
  String? userLabel;

  /// Answers the next fetch with this status instead of the link (404, 409, 410, 403).
  int? failFetchWith;
  String status = 'pending';
  bool denyAnswersWrongCode = false;

  /// The answer to a posted statement is lost (the connection drops after it was sent).
  bool dropAnswer = false;
  int attempts = 0;
  int fetches = 0;
  final List<Map<String, Object?>> posted = [];

  Map<String, Object?> get linkRequest => {
        'link_id': _linkId,
        'client': {
          'fingerprint': _fingerprint,
          'domain_proven': domainProven,
          if (domainProven) 'domain': 'bank.example',
        },
        if (userLabel != null) 'user_label': userLabel,
        'nonce': _nonce,
        'expires_at': 1792425900,
      };

  http.Response _json(int status, Object body) => http.Response.bytes(
      utf8.encode(jsonEncode(body)), status,
      headers: {'content-type': 'application/json; charset=utf-8'});

  http.Client get client => MockClient((req) async {
        if (req.headers['Authorization'] != 'Device dev-token') {
          return _json(401, {'error': 'invalid_token'});
        }
        if (req.headers['X-Link-Secret'] != _secret) {
          return _json(404, {'error': 'not_found'});
        }
        if (req.method == 'GET' && req.url.path == '/consent/links/$_linkId') {
          fetches++;
          final f = failFetchWith;
          if (f != null) return _json(f, {'error': 'x'});
          return _json(200, linkRequest);
        }
        if (req.method == 'POST' &&
            req.url.path == '/consent/links/$_linkId/receipt') {
          final body = (jsonDecode(req.body) as Map).cast<String, Object?>();
          posted.add(body);
          if (dropAnswer) throw http.ClientException('connection reset');
          final signed = (body['signed'] as Map).cast<String, Object?>();
          if (status == 'closed') {
            return _json(403, {'error': 'access_denied', 'reason': 'code_failed'});
          }
          if (signed['decision'] == 'deny' && denyAnswersWrongCode) {
            return _json(400, {'error': 'wrong_code', 'attempts_remaining': 2});
          }
          if (signed['decision'] == 'deny') {
            status = 'denied';
            return _json(200, {'status': 'denied'});
          }
          if (signed['binding_code'] != _code) {
            attempts++;
            if (attempts >= 3) {
              status = 'closed';
              return _json(403, {'error': 'access_denied', 'reason': 'code_failed'});
            }
            return _json(400, {'error': 'wrong_code', 'attempts_remaining': 3 - attempts});
          }
          status = 'linked';
          return _json(200, {'status': 'linked'});
        }
        return _json(404, {'error': 'not_found'});
      });
}

class Rig {
  Rig(this.server, {this.pass = true}) {
    keys = SoftwareKeyStore(deviceTestKey());
    store = MemoryEnrollmentStore()
      ..write(EnrollmentRecord(
        kid: 'alice-device',
        deviceToken: 'dev-token',
        activeAt: DateTime.utc(2020),
        attestation: 'h.p.s',
        presenceKid: 'alice-device-presence',
        presenceAttestation: 'h.p.presence',
      ));
    history = MemoryHistoryStore();
    services = LinkServices(
      client: ApprovalClient(
          baseUrl: 'http://oidc', client: server.client, relyingParties: true),
      keys: keys,
      enrollment: store,
      history: history,
      enrolledOrigin: _origin,
      clock: _now,
    );
  }

  final FakeLinkServer server;
  final bool pass;
  late final SoftwareKeyStore keys;
  late final MemoryEnrollmentStore store;
  late final MemoryHistoryStore history;
  late final LinkServices services;

  LinkCode get code => LinkCode(issuer: _origin, linkId: _linkId, secret: _secret);
}

Future<void> _open(WidgetTester tester, Rig rig, {VoidCallback? onLinked}) async {
  tester.view.physicalSize = const Size(500, 2400);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(Brightness.light),
    home: LinkScreen(services: rig.services, code: rig.code, onLinked: onLinked),
  ));
  await tester.pump();
  await tester.pump();
}

Future<void> _type(WidgetTester tester, String code) async =>
    tester.enterText(find.byKey(const Key('binding-code')), code);

Future<void> _tap(WidgetTester tester, String key) async {
  await tester.tap(find.byKey(Key(key)));
  await tester.pump();
  await tester.pump();
  await tester.pump();
}

bool _enabled(WidgetTester tester, String key) {
  final w = tester.widget(find.byKey(Key(key)));
  return switch (w) {
    FilledButton(:final onPressed) => onPressed != null,
    OutlinedButton(:final onPressed) => onPressed != null,
    _ => throw StateError('not a button: $key'),
  };
}

void main() {
  group('the link screen', () {
    testWidgets('shows who it is, what the service calls the user in the area that says it was written by the service, and what linking means',
        (tester) async {
      final rig = Rig(FakeLinkServer());
      await _open(tester, rig);
      expect(find.byKey(const Key('client-verified')), findsOneWidget);
      expect(find.text('bank.example'), findsOneWidget);
      expect(find.byKey(const Key('link-user-label')), findsOneWidget);
      expect(
          tester.widget<Text>(find.byKey(const Key('link-user-label'))).data,
          'The service calls you: Alex M. (account ending 4421)');
      expect(
          find.descendant(
              of: find.byKey(const Key('written-by-service')),
              matching: find.byKey(const Key('link-user-label'))),
          findsOneWidget);
      expect(find.text('Written by the service'), findsOneWidget);
      expect(
          tester.widget<Text>(find.byKey(const Key('link-what'))).data,
          'This service will be able to send you approval requests. It cannot move anything without your approval.');
      expect(find.byKey(const Key('link-expiry')), findsOneWidget);
      expect(find.byKey(const Key('binding-code')), findsOneWidget);
      expect(_enabled(tester, 'link'), isTrue);
      expect(_enabled(tester, 'dont-link'), isTrue);
      // Only the fetch has happened.
      expect(rig.server.fetches, 1);
      expect(rig.server.posted, isEmpty);
    });

    testWidgets('no label, no area: a service that gave none', (tester) async {
      final rig = Rig(FakeLinkServer(userLabel: null));
      await _open(tester, rig);
      expect(find.byKey(const Key('link-user-label')), findsNothing);
      expect(find.byKey(const Key('written-by-service')), findsNothing);
    });

    testWidgets('a label that breaks the text rules: the link cannot be shown and nothing is sent',
        (tester) async {
      final rig = Rig(FakeLinkServer(userLabel: 'Alex \u202Emoc.live'));
      await _open(tester, rig);
      expect(find.byKey(const Key('link-unavailable')), findsOneWidget);
      expect(find.textContaining('Alex'), findsNothing);
      expect(find.byKey(const Key('link')), findsNothing);
      expect(rig.server.posted, isEmpty);
    });

    testWidgets('the code is never submitted by itself: six digits typed send nothing',
        (tester) async {
      final rig = Rig(FakeLinkServer());
      await _open(tester, rig);
      await _type(tester, _code);
      await tester.pump(const Duration(seconds: 5));
      expect(rig.server.posted, isEmpty);
      expect(rig.keys.prompts, isEmpty);
      // Submitting from the keyboard does not link either.
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pump();
      expect(rig.server.posted, isEmpty);
    });

    testWidgets('Link signs biatec_link with the APPROVAL key, with one prompt, and the service is linked',
        (tester) async {
      var linked = 0;
      final rig = Rig(FakeLinkServer());
      await _open(tester, rig, onLinked: () => linked++);
      await _type(tester, _code);
      await _tap(tester, 'link');
      expect(find.text('Linked.'), findsOneWidget);
      expect(linked, 1);
      // One biometric prompt, by the approval key; the presence key signed nothing.
      expect(rig.keys.prompts, hasLength(1));
      expect(rig.keys.prompts.single.$1, 'Link this service');
      expect(rig.keys.prompts.single.$2, 'bank.example');
      expect(rig.keys.signed, hasLength(1));
      expect(rig.keys.presenceSigned, isEmpty);
      final body = rig.server.posted.single;
      final signed = (body['signed'] as Map).cast<String, Object?>();
      expect(signed.keys.toSet(), {
        'type', 'link_id', 'client_fingerprint', 'domain', 'decision', 'binding_code', 'nonce', 'issued_at', 'kid'
      });
      expect(signed['type'], 'biatec_link');
      expect(signed['link_id'], _linkId);
      expect(signed['client_fingerprint'], _fingerprint);
      expect(signed['domain'], 'bank.example');
      expect(signed['decision'], 'approve');
      expect(signed['binding_code'], _code);
      expect(signed['nonce'], _nonce);
      expect(signed['issued_at'], 1792425630);
      expect(signed['kid'], 'alice-device');
      // What was signed is the RFC 8785 form of that object, under the approval key.
      expect(
          verifyEs256(
              (deviceTestKey()),
              utf8.encode(canonicalize(signed)),
              base64Url.decode(base64Url.normalize(body['signature'] as String))),
          isTrue);
      // The link secret went in a header and nowhere else.
      expect(jsonEncode(body), isNot(contains(_secret)));
      // History: the link, the service, the decision, no code.
      final entry = (await rig.history.read()).single;
      expect(entry.summary, 'Link a service');
      expect(entry.clientLabel, 'bank.example');
      expect(entry.decision, 'approve');
      expect(entry.authReqId, _linkId);
      expect(jsonEncode(entry.receipt), isNot(contains(_code)));
      expect(jsonEncode(entry.receipt), isNot(contains(_secret)));
    });

    testWidgets('the statement verifies with the protocol kit (lib.mjs verifyLinkPinned)',
        skip: nodeSkip != null, (tester) async {
      final rig = Rig(FakeLinkServer());
      await _open(tester, rig);
      await _type(tester, _code);
      await _tap(tester, 'link');
      final body = rig.server.posted.single;
      // The kit's check, with the kids the test device is enrolled under.
      final devices = [
        for (final d in pinnedDevices().cast<Map>())
          {...d, 'kid': d['key_use'] == 'approve' ? 'alice-device' : 'alice-device-presence'}
      ];
      final out = await tester.runAsync(() => runNode('test/links/verify_link.mjs', {
            'approval': body,
            'devices': devices,
            'expect': {
              'link_id': _linkId,
              'client_fingerprint': _fingerprint,
              'domain': 'bank.example',
              'nonce': _nonce,
              'binding_code': _code,
            },
            'now': 1792425640,
          }));
      expect(out, 'true');
      // The same statement presented under a presence pin only (an approval needs an approval key) does not.
      final presenceOnly = [devices.last];
      final refused = await tester.runAsync(() => runNode('test/links/verify_link.mjs', {
            'approval': body,
            'devices': presenceOnly,
            'expect': {
              'link_id': _linkId,
              'client_fingerprint': _fingerprint,
              'domain': 'bank.example',
              'nonce': _nonce,
              'binding_code': _code,
            },
            'now': 1792425640,
          }));
      expect(refused, 'false');
    });

    testWidgets('Don\'t link signs a deny with the PRESENCE key, no prompt, and nothing is linked',
        (tester) async {
      var linked = 0;
      final rig = Rig(FakeLinkServer());
      await _open(tester, rig, onLinked: () => linked++);
      await _tap(tester, 'dont-link');
      expect(find.text('Not linked.'), findsOneWidget);
      expect(linked, 0);
      expect(rig.keys.prompts, isEmpty);
      expect(rig.keys.signed, isEmpty);
      expect(rig.keys.presenceSigned, hasLength(1));
      final signed = (rig.server.posted.single['signed'] as Map).cast<String, Object?>();
      expect(signed['decision'], 'deny');
      expect(signed['binding_code'], '', reason: 'no code was typed');
      expect(signed['kid'], 'alice-device-presence');
      expect(rig.server.status, 'denied');
      expect((await rig.history.read()).single.decision, 'deny');
    });

    testWidgets('a callback that throws does not turn an accepted link into a failure', (tester) async {
      final rig = Rig(FakeLinkServer());
      await _open(tester, rig, onLinked: () => throw StateError('boom'));
      await _type(tester, _code);
      await _tap(tester, 'link');
      expect(find.text('Linked.'), findsOneWidget);
      expect(find.textContaining('Could not link'), findsNothing);
    });

    testWidgets('Check again: a link that is still open can be tried again', (tester) async {
      final server = FakeLinkServer()..dropAnswer = true;
      final rig = Rig(server);
      await _open(tester, rig);
      await _type(tester, _code);
      await _tap(tester, 'link');
      expect(find.text('Check again'), findsOneWidget);
      server.dropAnswer = false;
      server.status = 'pending';
      await tester.tap(find.text('Check again'));
      await tester.pump();
      await tester.pump();
      await tester.pump();
      expect(find.text('Nothing was recorded. The link is still open.'), findsOneWidget);
      expect(_enabled(tester, 'link'), isTrue);
    });

    testWidgets('Check again: a link that is gone is reported as no longer open, and Linked services reload', (tester) async {
      final server = FakeLinkServer()..dropAnswer = true;
      final rig = Rig(server);
      var reloaded = 0;
      await _open(tester, rig, onLinked: () => reloaded++);
      await _type(tester, _code);
      await _tap(tester, 'link');
      // It did arrive and was recorded: the server now answers 409 for the link.
      server.dropAnswer = false;
      server.failFetchWith = 409;
      await tester.tap(find.text('Check again'));
      await tester.pump();
      await tester.pump();
      await tester.pump();
      expect(find.text('The link is no longer open'), findsOneWidget);
      expect(reloaded, 1);
    });

    testWidgets('a deny never carries the code the user typed (it may be a wrong one)', (tester) async {
      final rig = Rig(FakeLinkServer());
      await _open(tester, rig);
      await _type(tester, '123456');
      await _tap(tester, 'dont-link');
      final signed = (rig.server.posted.single['signed'] as Map).cast<String, Object?>();
      expect(signed['binding_code'], '');
      expect(find.text('Not linked.'), findsOneWidget);
    });

    testWidgets('a deny the server answers with wrong_code is said, and nothing is recorded', (tester) async {
      final server = FakeLinkServer()..denyAnswersWrongCode = true;
      final rig = Rig(server);
      await _open(tester, rig);
      await _tap(tester, 'dont-link');
      expect(find.text('Wrong code.'), findsOneWidget);
      expect(find.byKey(const Key('link')), findsOneWidget);
      expect((await rig.history.read()), isEmpty);
    });

    testWidgets('the link screen uses the shared approval action bar', (tester) async {
      final rig = Rig(FakeLinkServer());
      await _open(tester, rig);
      expect(find.byType(ApprovalActionBar), findsOneWidget);
    });

    testWidgets('Link without the confirmation for an unproven service says why instead of doing nothing', (tester) async {
      final rig = Rig(FakeLinkServer(domainProven: false));
      final c = rig.services.controllerFor(rig.code);
      await c.load();
      await c.approve(_code);
      expect(c.error, contains('has not proven who it is'));
      expect(rig.server.posted, isEmpty);
    });

    testWidgets('a code that is not six digits is told so and nothing is sent', (tester) async {
      final rig = Rig(FakeLinkServer());
      await _open(tester, rig);
      await _type(tester, '123');
      await _tap(tester, 'link');
      expect(find.text('Enter the six-digit code.'), findsOneWidget);
      expect(rig.server.posted, isEmpty);
      expect(rig.keys.prompts, isEmpty);
    });

    testWidgets('a service without a proven domain: Link first asks "This service has not proven who it is"; cancelling sends nothing',
        (tester) async {
      final rig = Rig(FakeLinkServer(domainProven: false));
      await _open(tester, rig);
      expect(find.byKey(const Key('client-unverified')), findsOneWidget);
      await _type(tester, _code);
      await _tap(tester, 'link');
      expect(find.text('This service has not proven who it is'), findsOneWidget);
      expect(rig.server.posted, isEmpty);
      expect(rig.keys.prompts, isEmpty);
      await _tap(tester, 'unproven-cancel');
      expect(rig.server.posted, isEmpty);
      expect(rig.keys.prompts, isEmpty);
      expect(find.byKey(const Key('link')), findsOneWidget);
    });

    testWidgets('confirming links it, and the statement says the service had no proven domain (domain null)',
        (tester) async {
      final rig = Rig(FakeLinkServer(domainProven: false));
      await _open(tester, rig);
      await _type(tester, _code);
      await _tap(tester, 'link');
      await _tap(tester, 'unproven-link');
      expect(find.text('Linked.'), findsOneWidget);
      final signed = (rig.server.posted.single['signed'] as Map).cast<String, Object?>();
      expect(signed['domain'], isNull);
      expect(rig.keys.prompts.single.$2, startsWith('Unproven application'));
    });

    testWidgets('a controller never links an unproven service without the confirmation', (tester) async {
      final rig = Rig(FakeLinkServer(domainProven: false));
      final c = rig.services.controllerFor(rig.code);
      await c.load();
      await c.approve(_code);
      expect(rig.server.posted, isEmpty);
      expect(c.phase, LinkPhase.ready);
    });

    testWidgets('a wrong code says how many attempts the server has left; the third closes the link',
        (tester) async {
      final rig = Rig(FakeLinkServer());
      await _open(tester, rig);
      await _type(tester, '000000');
      await _tap(tester, 'link');
      expect(find.text('Wrong code. 2 attempts left.'), findsOneWidget);
      await _tap(tester, 'link');
      expect(find.text('Wrong code. 1 attempt left.'), findsOneWidget);
      await _tap(tester, 'link');
      expect(find.text('Three wrong codes. The link was closed.'), findsOneWidget);
      expect(find.byKey(const Key('link')), findsNothing);
      expect(rig.server.posted, hasLength(3));
      expect((await rig.history.read()), isEmpty, reason: 'nothing was linked or declined');
    });

    for (final status in [404, 409, 410]) {
      testWidgets('a link the server says is gone ($status) is one neutral message', (tester) async {
        final server = FakeLinkServer()..failFetchWith = status;
        final rig = Rig(server);
        await _open(tester, rig);
        expect(find.text('This link is not available'), findsOneWidget);
        expect(
            find.text('It has expired or was already used. Start again in the service to get a new code.'),
            findsOneWidget);
        expect(find.byKey(const Key('link')), findsNothing);
      });
    }

    testWidgets('a device that is not active yet cannot link', (tester) async {
      final server = FakeLinkServer()..failFetchWith = 403;
      final rig = Rig(server);
      await _open(tester, rig);
      expect(find.byKey(const Key('link-unavailable')), findsOneWidget);
      expect(find.textContaining('not active yet'), findsOneWidget);
    });

    testWidgets('a cancelled prompt sends nothing and keeps the link open', (tester) async {
      final rig = Rig(FakeLinkServer());
      rig.keys.cancelNextPrompt = true;
      await _open(tester, rig);
      await _type(tester, _code);
      await _tap(tester, 'link');
      expect(find.text('Linking cancelled. Nothing was sent.'), findsOneWidget);
      expect(rig.server.posted, isEmpty);
      expect(find.byKey(const Key('link')), findsOneWidget);
    });

    testWidgets('an invalidated approval key: Link says why, Don\'t link still works', (tester) async {
      final rig = Rig(FakeLinkServer());
      rig.keys.state = KeysState.approvalKeyInvalidated;
      await _open(tester, rig);
      await _type(tester, _code);
      await _tap(tester, 'link');
      expect(find.textContaining('fingerprint or face settings'), findsOneWidget);
      expect(rig.server.posted, isEmpty);
      await _tap(tester, 'dont-link');
      expect(find.text('Not linked.'), findsOneWidget);
    });

    test('Slovak: the language of the phone picks the texts', () {
      expect(RpStrings.of(const Locale('sk')).t('dontLink'), 'Neprepájať');
      expect(RpStrings.of(const Locale('sk')).t('writtenByService'), 'Napísala služba');
      expect(RpStrings.of(const Locale('de')).t('dontLink'), "Don't link");
    });
  });
}
