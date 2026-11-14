import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/channel/channel_wiring.dart';
import 'package:biatec_authenticator/channel/device_channel.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/security/pinning/pin_policy.dart';
import 'package:biatec_authenticator/security/pinning/pinned_http.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

class _Offline implements NetworkStatus {
  @override
  Future<bool> get online async => true;
  @override
  Stream<bool> get changes => const Stream.empty();
}

void main() {
  group('the device channel is switched by configuration', () {
    test('on for the mock, off for a real Biatec OIDC unless asked for', () {
      expect(AppConfig.resolve('', release: false).deviceChannel, isTrue);
      expect(
          AppConfig.resolve('https://oidc.biatec.io', release: true)
              .deviceChannel,
          isFalse);
      expect(
          AppConfig.resolve('https://oidc.biatec.io',
                  release: true, deviceChannel: true)
              .deviceChannel,
          isTrue);
      expect(const AppConfig(oidcBaseUrl: 'https://x', isMock: false)
          .deviceChannel, isFalse);
    });

    test('the idle source never connects and never asks for a ticket', () async {
      final source = IdleEventSource();
      await source.start();
      expect(await source.events.isEmpty, isTrue);
      await source.stop();
    });
  });

  group('a refused ticket', () {
    Future<int> calls(int status) async {
      final store = MemoryEnrollmentStore();
      await store.write(EnrollmentRecord(presenceKid: 'p-kid', presenceAttestation: 'p.p.s', 
          kid: 'k', deviceToken: 't', activeAt: DateTime.utc(2020)));
      var n = 0;
      final channel = buildDeviceChannel(
        config: AppConfig.resolve('', release: false),
        client: ApprovalClient(
            baseUrl: 'http://oidc',
            client: MockClient((_) async {
              n++;
              return http.Response('{}', status);
            })),
        enrollment: store,
        network: _Offline(),
      );
      await channel.start();
      await Future<void>.delayed(const Duration(milliseconds: 1700));
      await channel.dispose();
      return n;
    }

    test('401 and 403 are permanent: one request, no retries', () async {
      expect(await calls(401), 1);
      expect(await calls(403), 1);
    });

    test('a server error is retried with backoff', () async {
      expect(await calls(503), greaterThanOrEqualTo(2));
    });
  });

  test('a bare 401 or 403 (no body) is still a refusal with its status',
      () async {
    for (final status in [401, 403]) {
      for (final body in ['', 'Forbidden']) {
        final client = ApprovalClient(
            baseUrl: 'http://oidc',
            client: MockClient((_) async => http.Response(body, status)));
        await expectLater(
            client.hubTicket('t'),
            throwsA(isA<ApprovalException>()
                .having((e) => e.status, 'status', status)));
      }
    }
  });

  test('one pinned WebSocket client is reused until the policy changes', () {
    PinnedHttp.configure(PinPolicy.none());
    final a = PinnedHttp.webSocketClient();
    expect(identical(a, PinnedHttp.webSocketClient()), isTrue);
    PinnedHttp.configure(PinPolicy.none());
    expect(identical(a, PinnedHttp.webSocketClient()), isFalse);
  });
}
