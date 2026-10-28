import 'dart:convert';

import 'package:http/http.dart' as http;

import '../security/pinning/pinned_http.dart';

import '../security/device_proof.dart';
import 'consent_request.dart';

/// The request could not be reached or read. [offline] is true when the
/// server was not reachable at all.
class ApprovalException implements Exception {
  const ApprovalException(this.message, {this.offline = false});
  final String message;
  final bool offline;
  @override
  String toString() => message;
}

/// What Biatec OIDC said about a posted receipt.
sealed class ReceiptOutcome {
  const ReceiptOutcome();
}

/// Accepted; [status] is `approved`, `denied` or `code_failed`.
class ReceiptAccepted extends ReceiptOutcome {
  const ReceiptAccepted(this.status);
  final String status;
}

/// The binding code did not match; the user may try again.
class WrongCode extends ReceiptOutcome {
  const WrongCode(this.attemptsRemaining);

  /// Null when the server did not say.
  final int? attemptsRemaining;
}

/// The third wrong code ended the request (rule 2). The device then posts a
/// signed `code_failed` receipt.
class CodeFailed extends ReceiptOutcome {
  const CodeFailed();
}

/// Refused for another reason (expired, already decided, invalid).
class ReceiptRefused extends ReceiptOutcome {
  const ReceiptRefused(this.status, this.error, this.description);
  final int status;
  final String error;
  final String description;
}

/// The authenticator side of Biatec OIDC (the mock in development), using
/// `Authorization: Device <token>`.
class ApprovalClient {
  ApprovalClient({required this.baseUrl, http.Client? client, this.proof})
      : _client = client ?? PinnedHttp.clientFor(baseUrl);

  final String baseUrl;
  final http.Client _client;

  /// Adds the proof of possession of the device key to every call (BCHAT-030); null sends the bearer token alone.
  final DeviceProofSigner? proof;

  static const _timeout = Duration(seconds: 15);

  Future<Map<String, String>> _auth(String method, Uri url, String deviceToken,
          [Map<String, String> extra = const {}]) async =>
      {
        'Authorization': 'Device $deviceToken',
        ...extra,
        ...?await _proof(method, url, deviceToken),
      };

  /// The proof of possession, or null when none is configured. A proof that cannot be made is a refusal of the call, not an unexplained crash
  /// and not a silent fall back to the bearer token alone.
  Future<Map<String, String>?> _proof(String method, Uri url, String token) async {
    final signer = proof;
    if (signer == null) return null;
    try {
      return await signer.headers(method, url, token);
    } on Object catch (e) {
      throw ApprovalException('The device proof could not be made: $e');
    }
  }

  Future<http.Response> _send(Future<http.Response> Function() call) async {
    try {
      return await call().timeout(_timeout);
    } on PinningException catch (e) {
      // Not "offline": retrying will not help, and the user is told to update.
      throw ApprovalException(e.message);
    } on Object catch (e) {
      throw ApprovalException('Biatec OIDC is not reachable: $e',
          offline: true);
    }
  }

  static Map<String, Object?> _object(String body, int status) {
    try {
      final v = jsonDecode(body);
      if (v is Map) return v.cast<String, Object?>();
    } on Object {
      // Falls through.
    }
    throw ApprovalException('Unexpected response ($status).');
  }

  static String _detail(Map<String, Object?> json) =>
      '${json['error_description'] ?? json['error'] ?? 'error'}';

  /// `GET /devices/requests`: the pending requests of this device's user.
  /// Malformed entries are skipped rather than shown.
  Future<List<ConsentRequest>> pending(String deviceToken) async {
    final url = Uri.parse('$baseUrl/devices/requests');
    final res = await _send(() async =>
        _client.get(url, headers: await _auth('GET', url, deviceToken)));
    final json = _object(res.body, res.statusCode);
    if (res.statusCode != 200) {
      throw ApprovalException(
          'Could not load requests (${res.statusCode}): ${_detail(json)}');
    }
    final list = json['requests'];
    if (list is! List) throw const ApprovalException('Malformed request list.');
    final out = <ConsentRequest>[];
    for (final item in list) {
      if (item is! Map) continue;
      try {
        out.add(ConsentRequest.fromJson(item.cast<String, Object?>()));
      } on FormatException {
        // A request the card cannot show faithfully is never offered.
      }
    }
    return out;
  }

  /// `GET /consent/requests/{id}`: the request as Biatec OIDC stores it.
  Future<ConsentRequest> fetch(String deviceToken, String authReqId) async {
    final url = Uri.parse(
        '$baseUrl/consent/requests/${Uri.encodeComponent(authReqId)}');
    final res = await _send(() async =>
        _client.get(url, headers: await _auth('GET', url, deviceToken)));
    final json = _object(res.body, res.statusCode);
    if (res.statusCode != 200) {
      throw ApprovalException(
          'Could not load the request (${res.statusCode}): ${_detail(json)}');
    }
    try {
      final r = ConsentRequest.fromJson(json);
      if (r.authReqId != authReqId) {
        throw const FormatException('a different request came back');
      }
      return r;
    } on FormatException catch (e) {
      throw ApprovalException('The request cannot be shown: ${e.message}');
    }
  }

  /// `PUT /devices/push`: where Biatec OIDC sends this device's
  /// notifications (T-053).
  Future<void> registerPush(
      String deviceToken, String provider, String pushToken) async {
    final url = Uri.parse('$baseUrl/devices/push');
    final res = await _send(() async => _client.put(
          url,
          headers: await _auth('PUT', url, deviceToken,
              {'Content-Type': 'application/json'}),
          body: jsonEncode({'provider': provider, 'token': pushToken}),
        ));
    if (res.statusCode != 200) {
      throw ApprovalException('Push registration refused (${res.statusCode}).');
    }
  }

  /// `POST /consent/requests/{id}/receipt`.
  Future<ReceiptOutcome> postReceipt(String deviceToken, String authReqId,
      Map<String, Object?> receipt) async {
    final http.Response res;
    try {
      final url = Uri.parse('$baseUrl/consent/requests/'
          '${Uri.encodeComponent(authReqId)}/receipt');
      res = await _send(() async => _client.post(
            url,
            headers: await _auth('POST', url, deviceToken,
                {'Content-Type': 'application/json'}),
            body: jsonEncode(receipt),
          ));
    } on ApprovalException catch (e) {
      // The receipt may have arrived even though no answer did, so this is
      // not reported as "offline, nothing happened".
      throw ApprovalException('No answer from Biatec OIDC, so the decision '
          'may or may not be recorded. Reopen the request to check. '
          '(${e.message})');
    }
    final json = _object(res.body, res.statusCode);
    if (res.statusCode == 200) {
      final status = json['status'];
      if (status is! String) {
        throw const ApprovalException('Malformed receipt response.');
      }
      return ReceiptAccepted(status);
    }
    if (json['reason'] == 'code_failed') return const CodeFailed();
    if (json['error'] == 'wrong_code') {
      final left = json['attempts_remaining'];
      return WrongCode(left is int ? left : null);
    }
    return ReceiptRefused(
        res.statusCode, '${json['error'] ?? 'error'}', _detail(json));
  }
}
