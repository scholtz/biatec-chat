import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:crypto/crypto.dart';
import 'package:http/http.dart' as http;

import '../codes/server_clock.dart';
import '../config.dart';
import '../security/pinning/pinned_http.dart' show PinningException;
import 'auth_client.dart';

/// Opens [url] in the user's default browser. False: it could not be started.
typedef BrowserOpener = Future<bool> Function(Uri url);

/// The sign-in of a desktop build (T-248): OAuth 2.0 for native apps (RFC 8252) with a loopback redirect.
///
/// 1. The endpoints come from the provider's discovery document, which must name the issuer the app was built for and keep both endpoints on that origin.
/// 2. A listener is opened on the loopback interface only, on the port of the registered redirect; nothing outside this machine can reach it.
/// 3. The system browser is opened on the authorization endpoint with a PKCE challenge (S256), a `state` and a `nonce`, all fresh random values.
/// 4. The listener takes the one answer that carries this `state` on the redirect's path. Anything else is answered with an error and ignored, however often
///    it comes, so another page or program on this machine can neither inject a code nor end the sign-in.
/// 5. The code is redeemed at the token endpoint with the PKCE verifier (a code read by anything else is useless without it), redirects are not followed, and the
///    ID token must carry this `nonce`, the issuer and this client.
///
/// No client secret exists (a public client), no refresh token is asked for, and the access token is returned to the caller and kept nowhere.
class LoopbackAuthClient extends AuthClient {
  LoopbackAuthClient({
    required http.Client Function() http,
    BrowserOpener? openBrowser,
    this.timeout = const Duration(minutes: 5),
    Random? random,
    DateTime Function()? now,
  })  : _http = http,
        _openBrowser = openBrowser ?? openSystemBrowser,
        _random = random ?? Random.secure(),
        _now = now ?? _serverNow;

  final http.Client Function() _http;
  final BrowserOpener _openBrowser;
  final Random _random;

  /// The time `auth_time` is compared with: this computer's clock corrected by what the provider's answers said (the pinned client reports their `Date`), so a
  /// clock that is wrong does not turn a login of just now into an old one.
  final DateTime Function() _now;

  static DateTime _serverNow() => DateTime.now().add(ServerClock.instance.offset ?? Duration.zero);

  /// How long the browser has to come back. Past it the listener is closed and the sign-in fails.
  final Duration timeout;

  /// How old the authentication behind an ID token may be when the token says (`auth_time`): the time a login in the browser can take.
  static const maxLoginAgeSeconds = 600;

  /// The longest discovery or token answer that is read.
  static const maxBodyBytes = 256 * 1024;

  Completer<void>? _cancel;

  @override
  bool get canCancel => true;

  @override
  void cancel() {
    final c = _cancel;
    if (c != null && !c.isCompleted) c.complete();
  }

  @override
  Future<String> signIn(AuthRequest r) async {
    if (_cancel != null) throw const LoginFailed('A sign-in is already open.');
    final cancel = _cancel = Completer<void>();
    final client = _http();
    try {
      final redirect = Uri.parse(r.redirectUri);
      if (!AppConfig.isLoopbackRedirect(r.redirectUri)) {
        throw const LoginFailed('This build has no loopback redirect to sign in with.');
      }
      final endpoints = await _orCancelled(_endpoints(client, r), cancel.future);
      final verifier = _token(32);
      final state = _token(32);
      final nonce = _token(32);
      final servers = await _listen(redirect);
      final String code;
      try {
        final url = endpoints.authorization.replace(queryParameters: {
          ...endpoints.authorization.queryParameters,
          'response_type': 'code',
          'client_id': r.clientId,
          'redirect_uri': r.redirectUri,
          'scope': r.scope,
          'state': state,
          'nonce': nonce,
          'code_challenge': _challenge(verifier),
          'code_challenge_method': 'S256',
          'prompt': r.prompt,
          'max_age': '${r.maxAge}',
        });
        if (cancel.isCompleted) throw const LoginCancelled();
        if (!await _openBrowser(url)) {
          throw const LoginFailed('The browser could not be opened.');
        }
        code = await _answer(servers, redirect, state, cancel.future);
      } finally {
        for (final s in servers) {
          await s.close(force: true);
        }
      }
      // A sign-in that was stopped gives no token, also when the answer had already arrived: closing the client (below) ends the call that is still open.
      return await _orCancelled(_redeem(client, endpoints, r, code, verifier, nonce), cancel.future);
    } on SocketException catch (e) {
      throw LoginFailed('The provider could not be reached (${e.message}).');
    } on http.ClientException catch (e) {
      throw LoginFailed('The provider could not be reached (${e.message}).');
    } on TimeoutException {
      throw const LoginFailed('The provider did not answer in time.');
    } on PinningException catch (e) {
      throw LoginFailed(e.message);
    } on TlsException catch (e) {
      throw LoginFailed('The connection to the provider could not be verified (${e.message}).');
    } finally {
      client.close();
      _cancel = null;
    }
  }

  /// [work], unless the sign-in is stopped first: then [LoginCancelled], and what [work] ends with is ignored.
  static Future<T> _orCancelled<T>(Future<T> work, Future<void> cancelled) {
    final done = Completer<T>();
    work.then((v) {
      if (!done.isCompleted) done.complete(v);
    }, onError: (Object e, StackTrace s) {
      if (!done.isCompleted) done.completeError(e, s);
    });
    cancelled.then((_) {
      if (!done.isCompleted) done.completeError(const LoginCancelled());
    });
    return done.future;
  }

  // ---- endpoints ----

  Future<_Endpoints> _endpoints(http.Client client, AuthRequest r) async {
    final discovery = r.discoveryUrl;
    if (discovery == null) {
      // The mock is addressed by its endpoints (see AuthRequest.forConfig); plain http is for it only.
      final a = r.authorizationEndpoint, t = r.tokenEndpoint;
      if (a == null || t == null) throw const LoginFailed('This build names no login endpoints.');
      final authorization = Uri.parse(a), token = Uri.parse(t);
      if (!r.allowInsecure && (authorization.scheme != 'https' || token.scheme != 'https')) {
        throw const LoginFailed('The login endpoints must be https.');
      }
      return _Endpoints(authorization, token, null);
    }
    const suffix = '/.well-known/openid-configuration';
    final uri = Uri.parse(discovery);
    if (uri.scheme != 'https' || !discovery.endsWith(suffix)) {
      throw const LoginFailed('The discovery address must be https.');
    }
    final issuer = discovery.substring(0, discovery.length - suffix.length);
    final json = await _json(client, http.Request('GET', uri), 'discovery document');
    // OpenID Connect Discovery 4.3: the issuer in the document is exactly the one it was asked from. A document that names another issuer is not this provider's.
    if (!_sameIssuer(json['issuer'], issuer)) {
      throw const LoginFailed('The discovery document names another issuer.');
    }
    final authorization = _endpoint(json['authorization_endpoint'], uri, 'authorization_endpoint');
    final token = _endpoint(json['token_endpoint'], uri, 'token_endpoint');
    final methods = json['code_challenge_methods_supported'];
    if (methods is List && !methods.contains('S256')) {
      throw const LoginFailed('The provider does not support PKCE with S256.');
    }
    return _Endpoints(authorization, token, issuer);
  }

  /// An endpoint of the discovery document: https, on the origin the document came from (the host the app is pinned to), no credentials and no fragment.
  static Uri _endpoint(Object? value, Uri discovery, String name) {
    final uri = value is String ? Uri.tryParse(value) : null;
    if (uri == null ||
        uri.scheme != 'https' ||
        uri.host.toLowerCase() != discovery.host.toLowerCase() ||
        uri.port != discovery.port ||
        uri.userInfo.isNotEmpty ||
        uri.hasFragment) {
      throw LoginFailed('The $name of the provider is not an https address on ${discovery.host}.');
    }
    return uri;
  }

  /// Whether [value] names the issuer [expected]: the same scheme, host (letter case does not matter in a host), port and path, a trailing slash aside.
  static bool _sameIssuer(Object? value, String expected) {
    if (value is! String) return false;
    final a = Uri.tryParse(value), b = Uri.tryParse(expected);
    String path(Uri u) => u.path.replaceAll(RegExp(r'/+$'), '');
    return a != null &&
        b != null &&
        a.scheme == b.scheme &&
        a.host.toLowerCase() == b.host.toLowerCase() &&
        a.port == b.port &&
        path(a) == path(b) &&
        !a.hasQuery &&
        !a.hasFragment &&
        a.userInfo.isEmpty;
  }

  // ---- the listener ----

  /// Loopback addresses only. `localhost` is both `127.0.0.1` and `::1` to a browser, so both are bound when the machine has them; one of them is enough.
  static Future<List<HttpServer>> _listen(Uri redirect) async {
    final host = redirect.host.toLowerCase();
    final addresses = [
      if (host != '::1') InternetAddress.loopbackIPv4,
      if (host != '127.0.0.1') InternetAddress.loopbackIPv6,
    ];
    final servers = <HttpServer>[];
    for (final a in addresses) {
      try {
        servers.add(await HttpServer.bind(a, redirect.port, shared: false));
      } on SocketException catch (e) {
        // A browser may use either address for `localhost`, so a port that something else holds on one of them could take the answer: the sign-in does not start.
        // The one failure that is let through is a machine that has no IPv6 loopback at all (next to an IPv4 listener that is up).
        if (servers.isNotEmpty && _noSuchAddress(e)) continue;
        for (final s in servers) {
          await s.close(force: true);
        }
        // "In use" is said only when that is what the system said (Windows 10048, Linux 98, macOS 48); a reserved port or a missing address is named as it is.
        throw LoginFailed(const {10048, 98, 48}.contains(e.osError?.errorCode)
            ? 'Port ${redirect.port} on this computer is in use. Close the program that uses it and sign in again.'
            : 'Port ${redirect.port} on this computer cannot be opened for the sign-in (${e.osError?.message ?? e.message}).');
      }
    }
    return servers;
  }

  /// The address family or the address does not exist on this machine (Windows: WSAEAFNOSUPPORT 10047, WSAEADDRNOTAVAIL 10049; Linux: 97, 99; macOS: 47, 49).
  static bool _noSuchAddress(SocketException e) =>
      const {10047, 10049, 97, 99, 47, 49}.contains(e.osError?.errorCode);

  Future<String> _answer(List<HttpServer> servers, Uri redirect, String state, Future<void> cancelled) {
    final done = Completer<String>();
    void fail(Object e) {
      if (!done.isCompleted) done.completeError(e);
    }

    Future<void> handle(HttpRequest q) async {
      try {
        final Map<String, String> p;
        try {
          p = q.uri.queryParameters;
        } on Object {
          // A query that cannot be decoded is not the answer; it is refused like any other stray request.
          await _page(q, HttpStatus.badRequest, _strayPage);
          return;
        }
        final path = q.uri.path.isEmpty ? '/' : q.uri.path;
        final expected = redirect.path.isEmpty ? '/' : redirect.path;
        final mine = q.method == 'GET' &&
            q.connectionInfo?.remoteAddress.isLoopback == true &&
            path == expected &&
            _same(p['state'] ?? '', state);
        if (!mine || done.isCompleted) {
          // Not the answer to this sign-in (a favicon, another page or program probing the port): said so, and the wait goes on however many there are. The wait
          // ends with the answer, the time limit or the user.
          await _page(q, HttpStatus.badRequest, _strayPage);
          return;
        }
        final code = p['code'];
        final error = p['error'];
        if (error != null || code == null || code.isEmpty) {
          await _page(q, HttpStatus.ok, _failedPage);
          fail(error == 'access_denied'
              ? const LoginCancelled()
              // The error code is the provider's (RFC 6749, 4.1.2.1), shown as a plain code; its free text is not shown.
              : LoginFailed('The provider refused the sign-in (${_code(error)}).'));
          return;
        }
        await _page(q, HttpStatus.ok, _donePage);
        if (!done.isCompleted) done.complete(code);
      } on Object {
        // A broken connection is not the answer.
      }
    }

    final subscriptions = [for (final s in servers) s.listen(handle, onError: (Object _) {})];
    final timer = Timer(timeout, () => fail(const LoginFailed('The sign-in was not finished in time.')));
    cancelled.then((_) => fail(const LoginCancelled()));
    return done.future.whenComplete(() {
      timer.cancel();
      for (final s in subscriptions) {
        s.cancel();
      }
    });
  }

  static String _code(String? error) =>
      error != null && RegExp(r'^[a-z_]{1,40}$').hasMatch(error) ? error : 'no code';

  static bool _same(String a, String b) {
    if (a.length != b.length) return false;
    var diff = 0;
    for (var i = 0; i < a.length; i++) {
      diff |= a.codeUnitAt(i) ^ b.codeUnitAt(i);
    }
    return diff == 0;
  }

  /// A fixed page: nothing of the request is put into it, and it may load and run nothing.
  static Future<void> _page(HttpRequest q, int status, String text) async {
    final res = q.response
      ..statusCode = status
      ..headers.contentType = ContentType('text', 'html', charset: 'utf-8')
      ..headers.set('Cache-Control', 'no-store')
      ..headers.set('Content-Security-Policy', "default-src 'none'; frame-ancestors 'none'")
      ..headers.set('X-Content-Type-Options', 'nosniff')
      ..headers.set('Referrer-Policy', 'no-referrer')
      ..headers.set('Connection', 'close');
    res.write('<!doctype html><html><head><meta charset="utf-8"><title>Biatec Authenticator</title></head>'
        '<body><p>$text</p></body></html>');
    await res.close();
  }

  // Not "you are signed in": the app still has to redeem the code and check the answer, and says the outcome itself.
  static const _donePage = 'The sign-in was received. Go back to Biatec Authenticator to continue. You can close this window.';
  static const _failedPage = 'The sign-in was not completed. Close this window and go back to Biatec Authenticator.';
  static const _strayPage = 'This address is used by Biatec Authenticator while it signs in. Nothing was done.';

  // ---- the token ----

  Future<String> _redeem(http.Client client, _Endpoints e, AuthRequest r, String code, String verifier, String nonce) async {
    final request = http.Request('POST', e.token)
      ..bodyFields = {
        'grant_type': 'authorization_code',
        'code': code,
        'redirect_uri': r.redirectUri,
        'client_id': r.clientId,
        'code_verifier': verifier,
      };
    final json = await _json(client, request, 'token answer');
    final token = json['access_token'];
    if (token is! String || token.isEmpty) {
      throw const LoginFailed('The provider gave no access token.');
    }
    final type = json['token_type'];
    if (type is String && type.toLowerCase() != 'bearer') {
      throw const LoginFailed('The provider gave a token of a kind this app cannot use.');
    }
    final idToken = json['id_token'];
    if (idToken is String && idToken.isNotEmpty) {
      _checkIdToken(idToken, issuer: e.issuer, clientId: r.clientId, nonce: nonce, now: _now());
    } else if (e.issuer != null) {
      // A real provider answers an `openid` login with an ID token; without one the answer cannot be tied to this sign-in's nonce.
      throw const LoginFailed('The provider gave no ID token.');
    }
    return token;
  }

  /// The ID token came straight from the token endpoint over the pinned connection, so its signature adds nothing (OpenID Connect Core 3.1.3.7, rule 6); what ties it to
  /// THIS sign-in is checked: the nonce, the issuer and the audience.
  static void _checkIdToken(String jwt,
      {required String? issuer, required String clientId, required String nonce, required DateTime now}) {
    final Map<String, Object?> claims;
    try {
      final parts = jwt.split('.');
      if (parts.length != 3) throw const FormatException();
      claims = (jsonDecode(utf8.decode(base64Url.decode(base64Url.normalize(parts[1])))) as Map).cast<String, Object?>();
    } on Object {
      throw const LoginFailed('The ID token of the provider cannot be read.');
    }
    final n = claims['nonce'];
    if (n is! String || !_same(n, nonce)) {
      throw const LoginFailed('The ID token is not for this sign-in.');
    }
    if (issuer != null && !_sameIssuer(claims['iss'], issuer)) {
      throw const LoginFailed('The ID token is from another issuer.');
    }
    // A server that says when the user authenticated must say "just now": the login asks for one (`max_age=0`). One that does not say cannot be checked here.
    final authTime = claims['auth_time'];
    if (authTime is num &&
        now.millisecondsSinceEpoch / 1000 - authTime > maxLoginAgeSeconds) {
      throw const LoginFailed('The provider used an earlier sign-in. Sign out in the browser and sign in again.');
    }
    final aud = claims['aud'];
    final forMe = aud is String ? aud == clientId : aud is List && aud.contains(clientId);
    if (!forMe) throw const LoginFailed('The ID token is for another application.');
  }

  /// Sends [request] without following a redirect (the code and the verifier go to the token endpoint and nowhere else) and reads a JSON object of a bounded size.
  static Future<Map<String, Object?>> _json(http.Client client, http.Request request, String what) async {
    request
      ..followRedirects = false
      ..headers['Accept'] = 'application/json';
    final response = await client.send(request).timeout(const Duration(seconds: 20),
        onTimeout: () => throw LoginFailed('The provider did not answer in time ($what).'));
    final bytes = <int>[];
    await for (final chunk in response.stream.timeout(const Duration(seconds: 20))) {
      bytes.addAll(chunk);
      if (bytes.length > maxBodyBytes) throw LoginFailed('The $what of the provider is too large.');
    }
    Object? body;
    try {
      body = jsonDecode(utf8.decode(bytes));
    } on Object {
      body = null;
    }
    if (response.statusCode != 200) {
      final error = body is Map ? body['error'] : null;
      throw LoginFailed('The provider refused the $what (${response.statusCode}, ${_code(error is String ? error : null)}).');
    }
    if (body is! Map) throw LoginFailed('The $what of the provider is not JSON.');
    return body.cast<String, Object?>();
  }

  // ---- random values ----

  /// [bytes] random bytes, base64url without padding: 32 bytes give the 43 characters RFC 7636 asks of a verifier.
  String _token(int bytes) =>
      base64Url.encode(List<int>.generate(bytes, (_) => _random.nextInt(256))).replaceAll('=', '');

  static String _challenge(String verifier) =>
      base64Url.encode(sha256.convert(ascii.encode(verifier)).bytes).replaceAll('=', '');
}

class _Endpoints {
  const _Endpoints(this.authorization, this.token, this.issuer);
  final Uri authorization;
  final Uri token;

  /// Null for the mock (no discovery): its answers are not checked against an issuer.
  final String? issuer;
}

/// Opens [url] in the default browser of a desktop system. The program is named by its full path and the address is one argument, never a shell command line.
Future<bool> openSystemBrowser(Uri url) async {
  if (url.scheme != 'https' && url.scheme != 'http') return false;
  final String program;
  final List<String> arguments;
  if (Platform.isWindows) {
    final root = Platform.environment['SystemRoot'] ?? r'C:\Windows';
    program = '$root\\System32\\rundll32.exe';
    arguments = ['url.dll,FileProtocolHandler', '$url'];
  } else if (Platform.isMacOS) {
    program = '/usr/bin/open';
    arguments = ['$url'];
  } else if (Platform.isLinux) {
    program = 'xdg-open';
    arguments = ['$url'];
  } else {
    return false;
  }
  try {
    // Started and left alone: some launchers return only when the browser closes, and the sign-in must be listening by then.
    await Process.start(program, arguments, runInShell: false, mode: ProcessStartMode.detached);
    return true;
  } on ProcessException {
    return false;
  }
}
