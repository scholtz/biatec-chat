// T-247: the relying-party protocol is switched off by configuration against a real Biatec OIDC (repository rule: features that need Biatec OIDC work not yet deployed).
import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/links/link_services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/testing.dart';

import '../approvals/approval_test.dart' show FakeGate;
import '../approvals/support.dart';

ApprovalServices _approvals(AppConfig config) => ApprovalServices(
      client: ApprovalClient(
          baseUrl: config.oidcBaseUrl,
          client: MockClient((_) async => throw StateError('no call')),
          relyingParties: config.relyingPartiesEnabled),
      keys: SoftwareKeyStore(deviceTestKey()),
      enrollment: ObservableEnrollmentStore(MemoryEnrollmentStore()),
      gate: FakeGate(),
      history: MemoryHistoryStore(),
    );

void main() {
  test('on for the mock', () {
    final c = AppConfig.resolve('');
    expect(c.relyingParties, isTrue);
    expect(c.relyingPartiesEnabled, isTrue);
    expect(LinkServices.fromConfig(c, _approvals(c)), isNotNull);
  });

  test('off for a real URL, and with nothing defined', () {
    final c = AppConfig.resolve('https://oidc.biatec.io', release: true);
    expect(c.relyingParties, isFalse);
    expect(c.relyingPartiesEnabled, isFalse);
    expect(LinkServices.fromConfig(c, _approvals(c)), isNull);
  });

  test('on for a real URL only when defined, and only together with the two keys and the trust settings', () {
    AppConfig of({bool rp = true, bool two = true, bool trust = true}) =>
        AppConfig.resolve('https://oidc.biatec.io',
            release: true,
            relyingParties: rp,
            twoKeys: two,
            trustSettings: trust);
    expect(of().relyingPartiesEnabled, isTrue);
    expect(of(rp: false).relyingPartiesEnabled, isFalse);
    expect(of(two: false).relyingPartiesEnabled, isFalse);
    expect(of(trust: false).relyingPartiesEnabled, isFalse);
    expect(of(two: false).relyingParties, isTrue);
  });

  test('the mock issuer is accepted only by a build against the mock or a plain http server, never by one against https', () {
    LinkServices? of(String url, {bool release = false}) {
      final c = AppConfig.resolve(url,
          release: release,
          relyingParties: true,
          twoKeys: true,
          trustSettings: true);
      return LinkServices.fromConfig(c, _approvals(c));
    }

    expect(of('')!.extraIssuers, {LinkServices.mockIssuer});
    expect(of('http://127.0.0.1:8089')!.extraIssuers, {LinkServices.mockIssuer});
    expect(of('http://127.0.0.1:8089')!.enrolledOrigin, 'http://127.0.0.1:8089');
    expect(of('https://oidc.biatec.io', release: true)!.extraIssuers, isEmpty);
    expect(of('https://oidc.biatec.io', release: true)!.enrolledOrigin,
        'https://oidc.biatec.io');
  });

  test('the approval client of a build without the protocol does not show a payment', () {
    final c = AppConfig.resolve('https://oidc.biatec.io', release: true);
    expect(_approvals(c).client.relyingParties, isFalse);
  });
}
