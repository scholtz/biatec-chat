// Test support for the relying-party features (T-247): the 1.2 vectors, a rig around a scripted Biatec OIDC, and the protocol kit (node) as the verifier.
import 'dart:convert';
import 'dart:io';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';

import 'approval_test.dart' show FakeGate, FakeOidc, vectorNow;
import 'support.dart';

/// A vector of `receipts-1.2.json`.
Map<String, Object?> rpVector(String name) =>
    vectors('receipts-1.2.json').firstWhere((v) => v['name'] == name);

Map<String, Object?> rpRequest(String name) =>
    (rpVector(name)['request'] as Map).cast<String, Object?>();

/// A deep copy, so a test can change one member.
Map<String, Object?> copyOf(Map<String, Object?> json) =>
    (jsonDecode(jsonEncode(json)) as Map).cast<String, Object?>();

/// [request] with its single detail changed by [change].
Map<String, Object?> withDetail(Map<String, Object?> request,
    void Function(Map<String, Object?> detail) change) {
  final r = copyOf(request);
  change(((r['authorization_details'] as List).single as Map)
      .cast<String, Object?>());
  return r;
}

/// A rig around a scripted Biatec OIDC that serves [request], with the relying-party protocol on (or off, to see that it is not shown).
class RpRig {
  RpRig(Map<String, Object?> request,
      {bool relyingParties = true, String code = '620418'}) {
    oidc = FakeOidc(request, code: code);
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
    services = ApprovalServices(
      client: ApprovalClient(
          baseUrl: 'http://oidc',
          client: oidc.client,
          relyingParties: relyingParties),
      keys: keys,
      enrollment: ObservableEnrollmentStore(store),
      gate: FakeGate(),
      history: history,
      clock: vectorNow,
    );
  }

  late final FakeOidc oidc;
  late final SoftwareKeyStore keys;
  late final MemoryEnrollmentStore store;
  late final MemoryHistoryStore history;
  late final ApprovalServices services;
}

/// Whether `node` runs here (the CI authenticator job may have none): the verification with the protocol kit needs it.
final String? nodeSkip = () {
  try {
    return Process.runSync('node', ['--version']).exitCode == 0
        ? null
        : 'node is not runnable, so lib.mjs cannot run';
  } on Object {
    return 'node is not installed, so lib.mjs cannot run';
  }
}();

/// Runs [script] (a path from the package root) with [input] on stdin and returns what it printed.
Future<String> runNode(String script, Object input) async {
  final proc = await Process.start('node', [script]);
  proc.stdin.write(jsonEncode(input));
  await proc.stdin.close();
  final out = await utf8.decodeStream(proc.stdout);
  final err = await utf8.decodeStream(proc.stderr);
  final code = await proc.exitCode;
  if (code != 0) throw StateError('node $script failed ($code): $err');
  return out.trim();
}

/// The keys a relying party pins at linking: the vector file's own list (the test-only approval and presence keys, which the tests sign with).
List<Object?> pinnedDevices() =>
    ((jsonDecode(File('../../packages/consent-protocol/vectors/receipts-1.2.json')
            .readAsStringSync()) as Map)['pinned_devices'] as List)
        .toList();

/// `verifyReceiptPinned` of the protocol kit on [receipt], as the relying party that sent [request] would run it.
Future<bool> verifiedByKit(
    Map<String, Object?> receipt, Map<String, Object?> request, String code,
    {String? decision, List<Object?>? pinned}) async {
  final details = copyOf(((request['authorization_details'] as List).single as Map)
      .cast<String, Object?>());
  final origin = details.remove('origin') as String?;
  final out = await runNode('test/approvals/verify_rp_receipt.mjs', {
    'receipt': receipt,
    'pinned': pinned ?? pinnedDevices(),
    'expectedDetails': details,
    'opts': {
      'origin': origin,
      'auth_req_id': request['auth_req_id'],
      'sub': request['sub'],
      'clientFingerprint': (request['client'] as Map)['fingerprint'],
      'bindingCode': code,
      if (decision != null) 'decision': decision,
    },
  });
  return out == 'true';
}
