import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/security/pinning/pin_policy.dart';
import 'package:biatec_authenticator/security/pinning/pinned_http.dart';
import 'package:biatec_authenticator/security/pinning/spki_pin.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

/// A self-signed certificate for localhost made with openssl for these tests only: no key is kept in the repository, and the folder is deleted afterwards.
class TestCert {
  TestCert(this.dir, this.name);
  final Directory dir;
  final String name;

  String get certPath => '${dir.path}/$name.crt';
  String get keyPath => '${dir.path}/$name.key';

  Uint8List get der {
    final body = File(certPath)
        .readAsStringSync()
        .split(RegExp(r'\r?\n'))
        .where((l) => l.isNotEmpty && !l.startsWith('-----'))
        .join();
    return base64.decode(body);
  }

  String get pin => SpkiPin.fromCertificate(der);

  static Future<TestCert> make(Directory dir, String name) async {
    final c = TestCert(dir, name);
    final r = await Process.run('openssl', [
      'req', '-x509', '-newkey', 'ec', '-pkeyopt',
      'ec_paramgen_curve:prime256v1', //
      '-nodes', '-keyout', c.keyPath, '-out', c.certPath, '-days', '30',
      '-subj', '/CN=localhost',
      '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1',
    ]);
    if (r.exitCode != 0) throw StateError('openssl failed: ${r.stderr}');
    return c;
  }
}

Future<bool> opensslAvailable() async {
  try {
    return (await Process.run('openssl', ['version'])).exitCode == 0;
  } on ProcessException {
    return false;
  }
}

Future<HttpServer> serve(TestCert cert, List<String> log) async {
  final context = SecurityContext()
    ..useCertificateChain(cert.certPath)
    ..usePrivateKey(cert.keyPath);
  final server =
      await HttpServer.bindSecure(InternetAddress.loopbackIPv4, 0, context);
  server.listen((request) {
    log.add(request.uri.path);
    request.response
      ..write('hello')
      ..close();
  });
  return server;
}

void main() {
  late Directory dir;
  late TestCert a;
  late TestCert b;
  var haveOpenssl = false;

  setUpAll(() async {
    haveOpenssl = await opensslAvailable();
    if (!haveOpenssl) return;
    dir = Directory.systemTemp.createTempSync('pin_test_');
    a = await TestCert.make(dir, 'a');
    b = await TestCert.make(dir, 'b');
  });

  tearDownAll(() {
    if (haveOpenssl) dir.deleteSync(recursive: true);
  });

  /// Runs [body] with a pinned client, or reports that openssl is missing.
  Future<void> withServer(
      TestCert cert,
      PinPolicy Function(TestCert) policy,
      Future<void> Function(http.Client client, int port, List<String> log)
          body) async {
    final log = <String>[];
    final server = await serve(cert, log);
    final client = PinnedHttp.create(policy(cert));
    try {
      await body(client, server.port, log);
    } finally {
      client.close();
      await server.close(force: true);
    }
  }

  PinPolicy pinsA(TestCert _, {DateTime? now}) => PinPolicy(pins: {
        'localhost': [a.pin]
      }, clock: () => now ?? DateTime.now());

  group('over TLS', () {
    test('a server with the pinned key is trusted, whatever the system trusts',
        () async {
      await withServer(a, (_) => pinsA(a), (client, port, log) async {
        final res = await client.get(Uri.parse('https://localhost:$port/ping'));
        expect(res.statusCode, 200);
        expect(res.body, 'hello');
        expect(log, ['/ping']);
      });
    });

    test('a server with another key is refused before any request is sent',
        () async {
      await withServer(b, (_) => pinsA(a), (client, port, log) async {
        await expectLater(
            client.get(Uri.parse('https://localhost:$port/secret')),
            throwsA(isA<PinningException>().having(
                (e) => e.decision, 'decision', PinDecision.pinMismatch)));
        expect(log, isEmpty,
            reason: 'nothing may reach a server that failed the pin');
      });
    });

    test('a pinned backup key is accepted as well', () async {
      await withServer(
          b,
          (_) => PinPolicy(pins: {
                'localhost': [a.pin, b.pin]
              }), (client, port, log) async {
        expect(
            (await client.get(Uri.parse('https://localhost:$port/')))
                .statusCode,
            200);
      });
    });

    test('the same key under a host name that has no pins is refused',
        () async {
      await withServer(a, (_) => pinsA(a), (client, port, log) async {
        await expectLater(
            client.get(Uri.parse('https://127.0.0.1:$port/')),
            throwsA(isA<PinningException>().having(
                (e) => e.decision, 'decision', PinDecision.noPinsForHost)));
        expect(log, isEmpty);
      });
    });

    test('a certificate past its date is refused even with the right key',
        () async {
      await withServer(a, (_) => pinsA(a, now: DateTime.utc(2100)),
          (client, port, log) async {
        await expectLater(
            client.get(Uri.parse('https://localhost:$port/')),
            throwsA(isA<PinningException>()
                .having((e) => e.decision, 'decision', PinDecision.expired)));
      });
    });

    test('a certificate not yet valid is refused', () async {
      await withServer(a, (_) => pinsA(a, now: DateTime.utc(2000)),
          (client, port, log) async {
        await expectLater(
            client.get(Uri.parse('https://localhost:$port/')),
            throwsA(isA<PinningException>().having(
                (e) => e.decision, 'decision', PinDecision.notYetValid)));
      });
    });

    test('after a refusal the next request to a good server still works',
        () async {
      final log = <String>[];
      final bad = await serve(b, log);
      final good = await serve(a, log);
      final client = PinnedHttp.create(pinsA(a));
      try {
        await expectLater(
            client.get(Uri.parse('https://localhost:${bad.port}/')),
            throwsA(isA<PinningException>()));
        expect(
            (await client.get(Uri.parse('https://localhost:${good.port}/')))
                .statusCode,
            200);
      } finally {
        client.close();
        await bad.close(force: true);
        await good.close(force: true);
      }
    });

    test('requests to the same bad server at once each get the pin failure',
        () async {
      await withServer(b, (_) => pinsA(a), (client, port, log) async {
        final results = await Future.wait([
          for (var i = 0; i < 6; i++)
            client
                .get(Uri.parse('https://localhost:$port/$i'))
                .then<Object?>((_) => 'sent', onError: (Object e) => e),
        ]);
        for (final r in results) {
          expect(r, isA<PinningException>());
        }
        expect(log, isEmpty);
      });
    });

    test('a redirect from the pinned host is not followed', () async {
      final context = SecurityContext()
        ..useCertificateChain(a.certPath)
        ..usePrivateKey(a.keyPath);
      final server =
          await HttpServer.bindSecure(InternetAddress.loopbackIPv4, 0, context);
      var followed = false;
      final plain = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      plain.listen((r) {
        followed = true;
        r.response.close();
      });
      server.listen((r) {
        r.response
          ..statusCode = 302
          ..headers.set('location', 'http://127.0.0.1:${plain.port}/')
          ..close();
      });
      final client = PinnedHttp.create(pinsA(a), allowLocalHttp: true);
      try {
        final res =
            await client.get(Uri.parse('https://localhost:${server.port}/'));
        expect(res.statusCode, 302);
        expect(followed, isFalse);
      } finally {
        client.close();
        await server.close(force: true);
        await plain.close(force: true);
      }
    });

    test('the three OIDC clients report a pin failure as such, not as offline',
        () async {
      await withServer(b, (_) => pinsA(a), (client, port, log) async {
        final url = 'https://localhost:$port';
        await expectLater(
            ApprovalClient(baseUrl: url, client: client).pending('token'),
            throwsA(isA<ApprovalException>()
                .having((e) => e.offline, 'offline', isFalse)
                .having((e) => e.message, 'message',
                    contains('could not be verified'))));
        await expectLater(
            SettingsClient(baseUrl: url, client: client).devices('token'),
            throwsA(isA<ApprovalException>()
                .having((e) => e.offline, 'offline', isFalse)
                .having((e) => e.message, 'message',
                    contains('could not be verified'))));
        await expectLater(
            OidcEnrollmentClient(baseUrl: url, client: client)
                .requestRecoveryCode('token'),
            throwsA(isA<EnrollmentException>().having((e) => e.message,
                'message', contains('could not be verified'))));
        expect(log, isEmpty);
      });
    });

    test(
        'a server that is not there is a connection failure, not a pin failure',
        () async {
      final socket = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
      final port = socket.port;
      await socket.close();
      final client = PinnedHttp.create(pinsA(a));
      try {
        await expectLater(client.get(Uri.parse('https://localhost:$port/')),
            throwsA(isNot(isA<PinningException>())));
      } finally {
        client.close();
      }
    });
  }, skip: opensslAvailableSync() ? false : 'openssl is not on the PATH');

  group('plain http', () {
    test('is refused for a real host, even in a debug build', () async {
      final client = PinnedHttp.create(PinPolicy.none(), allowLocalHttp: true);
      await expectLater(client.get(Uri.parse('http://example.com/')),
          throwsA(isA<PinningException>()));
      client.close();
    });

    test(
        'is refused everywhere when local http is not allowed (a release build)',
        () async {
      final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      server.listen((r) => r.response
        ..write('x')
        ..close());
      final client = PinnedHttp.create(PinPolicy.none(), allowLocalHttp: false);
      try {
        await expectLater(
            client.get(Uri.parse('http://127.0.0.1:${server.port}/')),
            throwsA(isA<PinningException>()));
      } finally {
        client.close();
        await server.close(force: true);
      }
    });

    test('works for the mock on this machine in a debug build', () async {
      final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      server.listen((r) => r.response
        ..write('mock')
        ..close());
      final client = PinnedHttp.create(PinPolicy.none(), allowLocalHttp: true);
      try {
        expect(
            (await client.get(Uri.parse('http://127.0.0.1:${server.port}/')))
                .body,
            'mock');
      } finally {
        client.close();
        await server.close(force: true);
      }
    });

    test('other schemes are refused', () async {
      final client = PinnedHttp.create(PinPolicy.none(), allowLocalHttp: true);
      await expectLater(client.get(Uri.parse('ftp://localhost/')),
          throwsA(isA<PinningException>()));
      client.close();
    });
  });

  group('the shared client', () {
    tearDown(() => PinnedHttp.configure(PinPolicy.none()));

    test(
        'building one does not touch the policy or the network, and it uses the policy set later',
        () async {
      final client = PinnedHttp.clientFor('https://stage.oidc.biatec.io');
      // Nothing configured: no host is trusted.
      await expectLater(
          client.get(Uri.parse('https://stage.oidc.biatec.io/')),
          throwsA(isA<PinningException>().having(
              (e) => e.decision, 'decision', PinDecision.noPinsForHost)));
      client.close();
    });

    test('the pinning message names the host and no pin', () {
      const e =
          PinningException(PinDecision.pinMismatch, 'stage.oidc.biatec.io');
      expect(e.message, contains('stage.oidc.biatec.io'));
      expect(e.message, isNot(contains('sha256')));
      expect(e.toString(), isNot(contains('=')));
    });
  });

  group('every connection to Biatec OIDC goes through the pinned client', () {
    test('no other HTTP client is created in lib/', () {
      final offenders = <String>[];
      for (final f
          in Directory('lib').listSync(recursive: true).whereType<File>()) {
        final path = f.path.replaceAll('\\', '/');
        if (!path.endsWith('.dart') || path.contains('lib/security/pinning/')) {
          continue;
        }
        final code = f.readAsStringSync();
        for (final pattern in [
          RegExp(r'\bhttp\.Client\('),
          RegExp(r'\bIOClient\('),
          RegExp(r'\bHttpClient\('),
          RegExp(r'\bClient\(\)')
        ]) {
          if (pattern.hasMatch(code)) offenders.add('$path ${pattern.pattern}');
        }
      }
      expect(offenders, isEmpty);
    });

    test('the three OIDC clients take theirs from the factory', () {
      for (final f in [
        'lib/approvals/approval_client.dart',
        'lib/enrollment/oidc_enrollment_client.dart',
        'lib/security/settings_client.dart',
      ]) {
        expect(File(f).readAsStringSync(),
            contains('PinnedHttp.clientFor(baseUrl)'),
            reason: f);
      }
    });

    test('main configures the policy before anything is sent', () {
      final main = File('lib/main.dart').readAsStringSync();
      final configure =
          main.indexOf('PinnedHttp.configure(pinPolicyFor(config))');
      expect(configure, greaterThan(-1));
      expect(configure,
          lessThan(main.indexOf('ApprovalServices.fromConfig(config)')));
    });
  });
}

bool opensslAvailableSync() {
  try {
    return Process.runSync('openssl', ['version']).exitCode == 0;
  } on ProcessException {
    return false;
  }
}
