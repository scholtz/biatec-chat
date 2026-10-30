import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;
import 'package:http/io_client.dart';

import '../../config.dart';
import 'pin_policy.dart';

/// The server's certificate did not pass the pin policy. This is not "offline": retrying will not help, and there is nothing the user can tap to continue.
class PinningException implements Exception {
  const PinningException(this.decision, this.host);
  final PinDecision decision;
  final String host;

  /// For the user. Names no pin.
  String get message => 'The connection to $host could not be verified, so '
      'nothing was sent. If this keeps happening, update the app.';

  @override
  String toString() => 'PinningException(${decision.name}, $host)';
}

/// The one place the app makes HTTPS connections to Biatec OIDC. Every connection is checked against the [PinPolicy], and the system's trusted
/// certificate authorities are not consulted: a certificate that a user, a device administrator or a rogue authority installed or issued is
/// refused unless its public key is pinned.
abstract final class PinnedHttp {
  static PinPolicy _policy = PinPolicy.none();

  /// Set once at startup, before the first request.
  static void configure(PinPolicy policy) => _policy = policy;

  static PinPolicy get policy => _policy;

  /// A client for [baseUrl] that reads the configured policy when it first sends, so building one costs nothing (and tests that never send never touch the network).
  ///
  /// [baseUrl] is only the address the caller will use; the policy decides per host at send time, so a client built for one host cannot be pointed at another.
  static http.Client clientFor(String baseUrl) =>
      _LazyClient(() => create(_policy));

  /// A client that enforces [policy]. Plain `http` is only for a local developer machine in a debug build ([allowLocalHttp]).
  static http.Client create(PinPolicy policy, {bool? allowLocalHttp}) =>
      _PinnedClient(policy, allowLocalHttp ?? kDebugMode);
}

class _LazyClient extends http.BaseClient {
  _LazyClient(this._make);

  final http.Client Function() _make;
  http.Client? _inner;

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) =>
      (_inner ??= _make()).send(request);

  @override
  void close() {
    _inner?.close();
    _inner = null;
  }
}

class _PinnedClient extends http.BaseClient {
  _PinnedClient(this._policy, this._allowLocalHttp) {
    final context = SecurityContext(withTrustedRoots: false)
      ..minimumTlsProtocolVersion = TlsProtocolVersion.tls1_2;
    final io = HttpClient(context: context)
      ..badCertificateCallback = _check
      ..connectionTimeout = const Duration(seconds: 15);
    _https = IOClient(io);
  }

  final PinPolicy _policy;
  final bool _allowLocalHttp;
  late final IOClient _https;
  http.Client? _plain;

  /// Why the latest handshake with a host was refused, and when (a counter). A request only believes a refusal that happened after it started, so
  /// requests to the same host running at once cannot take each other's.
  final Map<String, (int, PinDecision)> _refused = {};
  int _clock = 0;

  bool _check(X509Certificate cert, String host, int port) {
    final decision =
        _policy.check(host, cert.der, cert.startValidity, cert.endValidity);
    if (decision == PinDecision.accepted) return true;
    _refused[host.toLowerCase()] = (++_clock, decision);
    return false;
  }

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final host = request.url.host.toLowerCase();
    if (request.url.scheme == 'http') {
      if (_allowLocalHttp && AppConfig.localHosts.contains(host)) {
        // Same rules as the pinned path where they apply: no redirect (a local mock must not lead to another host), and a timeout.
        request.followRedirects = false;
        return (_plain ??= http.Client())
            .send(request)
            .timeout(const Duration(seconds: 15));
      }
      throw PinningException(PinDecision.noPinsForHost, host);
    }
    if (request.url.scheme != 'https') {
      throw PinningException(PinDecision.noPinsForHost, host);
    }
    final started = _clock;
    // A redirect is never followed: it could lead to plain http or to a host that has no pin, and Biatec OIDC's API does not redirect.
    request.followRedirects = false;
    try {
      return await _https.send(request);
    } on Object {
      final refusal = _refused[host];
      if (refusal != null && refusal.$1 > started) {
        throw PinningException(refusal.$2, host);
      }
      rethrow;
    }
  }

  @override
  void close() {
    _https.close();
    _plain?.close();
  }
}
