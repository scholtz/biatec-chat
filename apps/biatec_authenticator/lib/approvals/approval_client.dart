import 'dart:convert';

import 'package:http/http.dart' as http;

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
  final int attemptsRemaining;
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
  ApprovalClient({required this.baseUrl, http.Client? client})
      : _client = client ?? http.Client();

  final String baseUrl;
  final http.Client _client;

  static const _timeout = Duration(seconds: 15);

  Map<String, String> _auth(String deviceToken) =>
      {'Authorization': 'Device $deviceToken'};

  Future<http.Response> _send(Future<http.Response> Function() call) async {
    try {
      return await call().timeout(_timeout);
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
    final res = await _send(() => _client.get(
        Uri.parse('$baseUrl/devices/requests'),
        headers: _auth(deviceToken)));
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
    final res = await _send(() => _client.get(
        Uri.parse(
            '$baseUrl/consent/requests/${Uri.encodeComponent(authReqId)}'),
        headers: _auth(deviceToken)));
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

  /// `POST /consent/requests/{id}/receipt`.
  Future<ReceiptOutcome> postReceipt(String deviceToken, String authReqId,
      Map<String, Object?> receipt) async {
    final http.Response res;
    try {
      res = await _send(() => _client.post(
            Uri.parse('$baseUrl/consent/requests/'
                '${Uri.encodeComponent(authReqId)}/receipt'),
            headers: {
              ..._auth(deviceToken),
              'Content-Type': 'application/json'
            },
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
      return WrongCode(left is int ? left : 0);
    }
    return ReceiptRefused(
        res.statusCode, '${json['error'] ?? 'error'}', _detail(json));
  }
}
