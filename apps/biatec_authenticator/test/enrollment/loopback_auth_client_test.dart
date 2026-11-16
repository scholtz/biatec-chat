import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:biatec_authenticator/enrollment/auth_client.dart';
import 'package:biatec_authenticator/enrollment/loopback_auth_client.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

const issuer = 'https://oidc.example';
const clientId = 'authenticator-desktop';
/// A port that is free on this machine (found once; a fixed number could be taken or reserved).
late final int port;
String get redirect => 'http://localhost:$port';

String idToken(Map<String, Object?> claims) {
  String part(Object o) => base64Url.encode(utf8.encode(jsonEncode(o))).replaceAll('=', '');
  return '${part({'alg': 'RS256'})}.${part(claims)}.c2ln';
}

/// A provider (discovery and token endpoint, as an in-memory HTTP client) and a browser (what [LoopbackAuthClient] is told to open).
class Provider {
  Provider({this.discovery, this.tokenAnswer, this.browser});

  Map<String, Object?>? discovery;

  /// The token endpoint's answer for the form it was sent; null: the honest one.
  http.Response Function(Map<String, String> form, Uri authorize)? tokenAnswer;

  /// What the browser does with the authorization address; null: it comes back with a code and the state.
  Future<void> Function(Uri authorize)? browser;

  Uri? authorize;
  Map<String, String>? tokenForm;
  final requests = <String>[];
  final answers = <int>[];

  http.Client client() => MockClient((q) async {
        requests.add('${q.method} ${q.url}');
        if (q.url.path == '/.well-known/openid-configuration') {
          return http.Response(
              jsonEncode(discovery ??
                  {
                    'issuer': issuer,
                    'authorization_endpoint': '$issuer/authorize',
                    'token_endpoint': '$issuer/token',
                    'code_challenge_methods_supported': ['S256', 'plain'],
                  }),
              200);
        }
        if (q.url.path.endsWith('/token') && q.method == 'POST') {
          tokenForm = q.bodyFields;
          final custom = tokenAnswer;
          if (custom != null) return custom(q.bodyFields, authorize!);
          return http.Response(
              jsonEncode({
                'access_token': 'access-1',
                'token_type': 'Bearer',
                'id_token': idToken({
                  'iss': issuer,
                  'aud': clientId,
                  'nonce': authorize!.queryParameters['nonce'],
                }),
              }),
              200);
        }
        return http.Response('{}', 404);
      });

  Future<bool> open(Uri url) async {
    authorize = url;
    final custom = browser;
    // The browser works on its own: the client is already listening when it comes back.
    unawaited(Future(() async {
      if (custom != null) return custom(url);
      await get('/?code=code-1&state=${url.queryParameters['state']}');
    }));
    return true;
  }

  /// One request of "the browser" to the app's listener.
  Future<int> get(String pathAndQuery, {String method = 'GET', String host = '127.0.0.1'}) async {
    final c = HttpClient()..connectionTimeout = const Duration(seconds: 2);
    try {
      final q = await c.openUrl(method, Uri.parse('http://$host:$port$pathAndQuery'));
      final r = await q.close();
      await r.drain<void>();
      answers.add(r.statusCode);
      return r.statusCode;
    } finally {
      c.close(force: true);
    }
  }
}

AuthRequest request({String? redirectUri}) => AuthRequest(
      clientId: clientId,
      redirectUri: redirectUri ?? redirect,
      scope: AuthRequest.plainScope,
      discoveryUrl: '$issuer/.well-known/openid-configuration',
    );

LoopbackAuthClient clientOf(Provider p, {Duration timeout = const Duration(seconds: 5)}) =>
    LoopbackAuthClient(http: p.client, openBrowser: p.open, timeout: timeout);

Matcher failsWith(String text) =>
    throwsA(isA<LoginFailed>().having((e) => e.message, 'message', contains(text)));

/// Nothing listens on the port after a sign-in, however it ended.
Future<void> expectPortFree() async {
  final s = await ServerSocket.bind(InternetAddress.loopbackIPv4, port);
  await s.close();
}

void main() {
  setUpAll(() async {
    final probe = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    port = probe.port;
    await probe.close();
  });
  tearDown(expectPortFree);

  test('an error the client does not know still ends as a failed sign-in, and the client can be used again', () async {
    final p = Provider();
    var broken = true;
    final c = LoopbackAuthClient(
        http: () => broken ? throw StateError('no client') : p.client(), openBrowser: p.open);
    await expectLater(c.signIn(request()), failsWith('StateError'));
    broken = false;
    expect(await c.signIn(request()), 'access-1');
  });

  test('signs in: PKCE S256, state, nonce, the code redeemed with the verifier', () async {
    final p = Provider();
    expect(await clientOf(p).signIn(request()), 'access-1');

    final a = p.authorize!;
    expect('${a.origin}${a.path}', '$issuer/authorize');
    final q = a.queryParameters;
    expect(q['response_type'], 'code');
    expect(q['client_id'], clientId);
    expect(q['redirect_uri'], redirect);
    expect(q['scope'], 'openid');
    expect(q['code_challenge_method'], 'S256');
    expect(q['prompt'], 'login');
    expect(q['max_age'], '0');
    expect(q['state']!.length, greaterThanOrEqualTo(43));
    expect(q['nonce']!.length, greaterThanOrEqualTo(43));
    expect(q['state'], isNot(q['nonce']));

    final f = p.tokenForm!;
    expect(f['grant_type'], 'authorization_code');
    expect(f['code'], 'code-1');
    expect(f['redirect_uri'], redirect);
    expect(f['client_id'], clientId);
    expect(f.containsKey('client_secret'), isFalse);
    final verifier = f['code_verifier']!;
    expect(verifier.length, inInclusiveRange(43, 128));
    expect(base64Url.encode(sha256.convert(ascii.encode(verifier)).bytes).replaceAll('=', ''),
        q['code_challenge']);
    expect(p.answers, [200]);
  });

  test('every sign-in has new random values', () async {
    final p = Provider();
    final c = clientOf(p);
    await c.signIn(request());
    final first = p.authorize!.queryParameters;
    await c.signIn(request());
    final second = p.authorize!.queryParameters;
    for (final k in ['state', 'nonce', 'code_challenge']) {
      expect(second[k], isNot(first[k]), reason: k);
    }
  });

  test('a code with a wrong state, on another path or by another method is refused, and the sign-in goes on', () async {
    late Provider p;
    p = Provider(browser: (url) async {
      final state = url.queryParameters['state'];
      expect(await p.get('/?code=evil&state=guess'), 400);
      expect(await p.get('/?code=evil'), 400);
      expect(await p.get('/other?code=evil&state=$state'), 400);
      expect(await p.get('/?code=evil&state=$state', method: 'POST'), 400);
      expect(await p.get('/favicon.ico'), 400);
      // A query that cannot be decoded is answered, not left hanging.
      expect(await p.get('/?state=%ZZ'), 400);
      expect(await p.get('/?code=good&state=$state'), 200);
      // The answer is taken once.
      await p.get('/?code=late&state=$state').then((s) => expect(s, 400), onError: (_) {});
    });
    expect(await clientOf(p).signIn(request()), 'access-1');
    expect(p.tokenForm!['code'], 'good');
  });

  test('a flood of stray requests does not end the sign-in', () async {
    late Provider p;
    p = Provider(browser: (url) async {
      for (var i = 0; i < 200; i++) {
        expect(await p.get('/?code=x&state=nope'), 400);
      }
      await p.get('/?code=code-1&state=${url.queryParameters['state']}');
    });
    expect(await clientOf(p, timeout: const Duration(seconds: 30)).signIn(request()), 'access-1');
    expect(p.tokenForm!['code'], 'code-1');
  });

  test('a port that is taken on the IPv6 loopback address alone stops the sign-in too', () async {
    final ServerSocket busy;
    try {
      busy = await ServerSocket.bind(InternetAddress.loopbackIPv6, port);
    } on SocketException {
      return; // this machine has no IPv6 loopback
    }
    try {
      final p = Provider();
      await expectLater(clientOf(p).signIn(request()), failsWith('Port $port'));
      expect(p.authorize, isNull);
    } finally {
      await busy.close();
    }
  });

  test('stopping while the code is being redeemed gives no token', () async {
    final hold = Completer<void>();
    late LoopbackAuthClient c;
    final p = Provider();
    final slow = MockClient((q) async {
      if (q.url.path == '/token') {
        // The answer of the browser has arrived; the user stops now.
        c.cancel();
        await hold.future;
        return http.Response(jsonEncode({'access_token': 'late'}), 200);
      }
      return http.Response(
          jsonEncode({
            'issuer': issuer,
            'authorization_endpoint': '$issuer/authorize',
            'token_endpoint': '$issuer/token',
          }),
          200);
    });
    c = LoopbackAuthClient(http: () => slow, openBrowser: p.open, timeout: const Duration(seconds: 5));
    await expectLater(c.signIn(request()), throwsA(isA<LoginCancelled>()));
    hold.complete();
  });

  test('stopping while the provider is being asked for its endpoints opens no browser', () async {
    final hold = Completer<void>();
    late LoopbackAuthClient c;
    final p = Provider();
    final slow = MockClient((q) async {
      c.cancel();
      await hold.future;
      return http.Response('{}', 200);
    });
    c = LoopbackAuthClient(http: () => slow, openBrowser: p.open);
    await expectLater(c.signIn(request()), throwsA(isA<LoginCancelled>()));
    expect(p.authorize, isNull);
    hold.complete();
  });

  test('the issuer is compared as an address: host case, default port and a trailing slash do not matter', () async {
    for (final written in ['https://OIDC.Example', 'https://oidc.example:443', 'https://oidc.example/']) {
      final p = Provider();
      final token = await clientOf(p).signIn(AuthRequest(
          clientId: clientId,
          redirectUri: redirect,
          scope: AuthRequest.plainScope,
          discoveryUrl: '$written/.well-known/openid-configuration'.replaceFirst('//.well', '/.well')));
      expect(token, 'access-1', reason: written);
    }
  });

  test('the user refusing at the provider is a cancelled sign-in', () async {
    late Provider p;
    p = Provider(browser: (url) => p.get('/?error=access_denied&state=${url.queryParameters['state']}'));
    await expectLater(clientOf(p).signIn(request()), throwsA(isA<LoginCancelled>()));
    expect(p.tokenForm, isNull);
  });

  test('an error answer fails with the code only, never the provider\'s text', () async {
    late Provider p;
    p = Provider(
        browser: (url) => p.get(
            '/?error=invalid_scope&error_description=%3Cscript%3Esecret%3C%2Fscript%3E&state=${url.queryParameters['state']}'));
    await expectLater(
        clientOf(p).signIn(request()),
        throwsA(isA<LoginFailed>()
            .having((e) => e.message, 'message', contains('invalid_scope'))
            .having((e) => e.message, 'message', isNot(contains('secret')))));
  });

  test('no answer in time fails, and the listener is closed', () async {
    final p = Provider(browser: (_) async {});
    await expectLater(
        clientOf(p, timeout: const Duration(milliseconds: 200)).signIn(request()), failsWith('in time'));
  });

  test('cancel ends the wait as a cancelled sign-in', () async {
    final p = Provider(browser: (_) async {});
    final c = clientOf(p, timeout: const Duration(seconds: 30));
    expect(c.canCancel, isTrue);
    final result = c.signIn(request());
    unawaited(result.catchError((Object _) => ''));
    await Future<void>.delayed(const Duration(milliseconds: 200));
    c.cancel();
    await expectLater(result, throwsA(isA<LoginCancelled>()));
    // And the client can be used again.
    p.browser = null;
    expect(await c.signIn(request()), 'access-1');
  });

  test('a busy port fails before the browser opens', () async {
    final busy = await ServerSocket.bind(InternetAddress.loopbackIPv4, port);
    try {
      final p = Provider();
      await expectLater(clientOf(p).signIn(request()), failsWith('Port $port'));
      expect(p.authorize, isNull);
    } finally {
      await busy.close();
    }
  });

  test('the listener is on loopback addresses only', () async {
    Iterable<InternetAddress> others = const [];
    try {
      others = (await NetworkInterface.list(includeLoopback: false))
          .expand((i) => i.addresses)
          .where((a) => a.type == InternetAddressType.IPv4 && !a.isLoopback);
    } on Object {
      // No interface list on this machine: the loop below checks nothing.
    }
    late Provider p;
    p = Provider(browser: (url) async {
      for (final a in others) {
        // Nothing listens on the machine's other addresses.
        await expectLater(
            p.get('/?code=x&state=${url.queryParameters['state']}', host: a.address),
            throwsA(anything));
      }
      await p.get('/?code=code-1&state=${url.queryParameters['state']}');
    });
    expect(await clientOf(p, timeout: const Duration(seconds: 50)).signIn(request()), 'access-1');
  }, timeout: const Timeout(Duration(seconds: 60)));

  group('the discovery document', () {
    Future<void> refused(Map<String, Object?> discovery, String why) async {
      final p = Provider(discovery: discovery);
      await expectLater(clientOf(p).signIn(request()), failsWith(why));
      expect(p.authorize, isNull, reason: 'the browser is not opened');
    }

    test('that names another issuer is refused', () => refused({
          'issuer': 'https://evil.example',
          'authorization_endpoint': '$issuer/authorize',
          'token_endpoint': '$issuer/token',
        }, 'another issuer'));

    test('with an authorization endpoint on another host is refused', () => refused({
          'issuer': issuer,
          'authorization_endpoint': 'https://evil.example/authorize',
          'token_endpoint': '$issuer/token',
        }, 'authorization_endpoint'));

    test('with a token endpoint that is not https is refused', () => refused({
          'issuer': issuer,
          'authorization_endpoint': '$issuer/authorize',
          'token_endpoint': 'http://oidc.example/token',
        }, 'token_endpoint'));

    test('without S256 is refused', () => refused({
          'issuer': issuer,
          'authorization_endpoint': '$issuer/authorize',
          'token_endpoint': '$issuer/token',
          'code_challenge_methods_supported': ['plain'],
        }, 'S256'));

    test('that is not https is not fetched', () async {
      final p = Provider();
      await expectLater(
          clientOf(p).signIn(AuthRequest(
              clientId: clientId,
              redirectUri: redirect,
              discoveryUrl: 'http://oidc.example/.well-known/openid-configuration')),
          failsWith('https'));
      expect(p.requests, isEmpty);
    });
  });

  group('the token answer', () {
    Future<void> refused(http.Response Function(Map<String, String>, Uri) answer, String why) async {
      final p = Provider(tokenAnswer: answer);
      await expectLater(clientOf(p).signIn(request()), failsWith(why));
    }

    http.Response ok(Map<String, Object?> body) => http.Response(jsonEncode(body), 200);

    test('with the nonce of another sign-in is refused', () => refused(
        (f, a) => ok({
              'access_token': 't',
              'id_token': idToken({'iss': issuer, 'aud': clientId, 'nonce': 'other'}),
            }),
        'not for this sign-in'));

    test('from another issuer is refused', () => refused(
        (f, a) => ok({
              'access_token': 't',
              'id_token': idToken(
                  {'iss': 'https://evil.example', 'aud': clientId, 'nonce': a.queryParameters['nonce']}),
            }),
        'another issuer'));

    test('for another client is refused', () => refused(
        (f, a) => ok({
              'access_token': 't',
              'id_token':
                  idToken({'iss': issuer, 'aud': 'someone-else', 'nonce': a.queryParameters['nonce']}),
            }),
        'another application'));

    test('whose login is an old one is refused', () => refused(
        (f, a) => ok({
              'access_token': 't',
              'id_token': idToken({
                'iss': issuer,
                'aud': clientId,
                'nonce': a.queryParameters['nonce'],
                'auth_time': DateTime.now().millisecondsSinceEpoch ~/ 1000 - 3600,
              }),
            }),
        'earlier sign-in'));

    test('whose login is recent by the provider\'s clock is accepted on a computer whose clock is wrong', () async {
      final p = Provider(
          tokenAnswer: (f, a) => ok({
                'access_token': 'fresh',
                'id_token': idToken({
                  'iss': issuer,
                  'aud': clientId,
                  'nonce': a.queryParameters['nonce'],
                  'auth_time': DateTime.now().millisecondsSinceEpoch ~/ 1000 - 3600,
                }),
              }));
      // The corrected time is an hour back: this computer's clock runs an hour fast.
      final c = LoopbackAuthClient(
          http: p.client, openBrowser: p.open, now: () => DateTime.now().subtract(const Duration(hours: 1)));
      expect(await c.signIn(request()), 'fresh');
    });

    test('whose login is recent is accepted', () async {
      final p = Provider(
          tokenAnswer: (f, a) => ok({
                'access_token': 'fresh',
                'id_token': idToken({
                  'iss': issuer,
                  'aud': [clientId, 'other'],
                  'nonce': a.queryParameters['nonce'],
                  'auth_time': DateTime.now().millisecondsSinceEpoch ~/ 1000 - 20,
                }),
              }));
      expect(await clientOf(p).signIn(request()), 'fresh');
    });

    test('without an ID token is refused', () => refused((f, a) => ok({'access_token': 't'}), 'no ID token'));

    test('without an access token is refused',
        () => refused((f, a) => ok({'token_type': 'Bearer'}), 'no access token'));

    test('of another token type is refused',
        () => refused((f, a) => ok({'access_token': 't', 'token_type': 'DPoP'}), 'cannot use'));

    test('that is an error shows the code only', () async {
      final p = Provider(
          tokenAnswer: (f, a) => http.Response(
              jsonEncode({'error': 'invalid_grant', 'error_description': 'verifier abc'}), 400));
      await expectLater(
          clientOf(p).signIn(request()),
          throwsA(isA<LoginFailed>()
              .having((e) => e.message, 'message', contains('invalid_grant'))
              .having((e) => e.message, 'message', isNot(contains('abc')))));
    });

    test('that redirects is not followed', () async {
      final p = Provider(
          tokenAnswer: (f, a) =>
              http.Response('', 307, headers: {'location': 'https://evil.example/token'}));
      await expectLater(clientOf(p).signIn(request()), failsWith('307'));
      expect(p.requests.where((r) => r.contains('evil')), isEmpty);
    });
  });

  test('a redirect that is not a loopback address is refused', () async {
    for (final bad in [
      'https://oidc.example/callback',
      'http://example.com:42817',
      'http://localhost',
      'http://localhost:80',
      'http://localhost:42817/?x=1',
      'io.biatec.authenticator:/oauth2redirect',
    ]) {
      final p = Provider();
      await expectLater(clientOf(p).signIn(request(redirectUri: bad)), failsWith('loopback'),
          reason: bad);
      expect(p.requests, isEmpty, reason: bad);
    }
  });

  test('the mock is addressed by its endpoints and needs no ID token', () async {
    final p = Provider(tokenAnswer: (f, a) => http.Response(jsonEncode({'access_token': 'mock-token'}), 200));
    final token = await clientOf(p).signIn(AuthRequest(
        clientId: clientId,
        redirectUri: redirect,
        authorizationEndpoint: 'http://10.0.2.2:5080/mock-oidc/authorize',
        tokenEndpoint: 'http://10.0.2.2:5080/mock-oidc/token',
        allowInsecure: true));
    expect(token, 'mock-token');
    expect(p.authorize!.host, '10.0.2.2');
  });

  test('plain http endpoints are refused unless they are the mock\'s', () async {
    final p = Provider();
    await expectLater(
        clientOf(p).signIn(AuthRequest(
            clientId: clientId,
            redirectUri: redirect,
            authorizationEndpoint: 'http://oidc.example/authorize',
            tokenEndpoint: 'http://oidc.example/token')),
        failsWith('https'));
  });
}
