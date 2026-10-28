import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;
import 'package:http/io_client.dart';

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
  static http.Client clientFor(String baseUrl) =>
      _LazyClient(() => create(_policy));

  /// A client that enforces [policy]. Plain `http` is only for a local developer machine in a debug build ([allowLocalHttp]).
  static http.Client create(PinPolicy policy, {bool? allowLocalHttp}) =>
      _PinnedClient(policy, allowLocalHttp ?? kDebugMode);

  /// Hosts that mean "this machine" to a developer.
  static const localHosts = {'localhost', '127.0.0.1', '::1', '10.0.2.2'};
}

class _LazyClient extends http.BaseClient {
  _LazyClient(this._make);

  final http.Client Function() _make;
  http.Client? _inner;

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) =>
      (_inner ??= _make()).send(request);

  @override
  void close() => _inner?.close();
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

  /// Why the last handshake with a host was refused; read when the request then fails.
  final Map<String, PinDecision> _refused = {};

  bool _check(X509Certificate cert, String host, int port) {
    final decision =
        _policy.check(host, cert.der, cert.startValidity, cert.endValidity);
    if (decision == PinDecision.accepted) {
      _refused.remove(host.toLowerCase());
      return true;
    }
    _refused[host.toLowerCase()] = decision;
    return false;
  }

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final host = request.url.host.toLowerCase();
    if (request.url.scheme == 'http') {
      if (_allowLocalHttp && PinnedHttp.localHosts.contains(host)) {
        return (_plain ??= http.Client()).send(request);
      }
      throw PinningException(PinDecision.noPinsForHost, host);
    }
    if (request.url.scheme != 'https') {
      throw PinningException(PinDecision.noPinsForHost, host);
    }
    _refused.remove(host);
    try {
      return await _https.send(request);
    } on Object {
      final decision = _refused.remove(host);
      if (decision != null) throw PinningException(decision, host);
      rethrow;
    }
  }

  @override
  void close() {
    _https.close();
    _plain?.close();
  }
}
