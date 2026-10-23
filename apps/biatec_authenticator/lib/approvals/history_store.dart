import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../security/secure_storage.dart';

/// The receipt as the history keeps it: the code the user typed (`signed.binding_code`) is a one-time secret of that request and has no
/// use afterwards, so it is not stored; the entry says so (`redacted`). Everything else, the signature included, is kept. The stored copy is for
/// reading: it no longer verifies against its signature (the signed bytes held the code), and the `redacted` note is not signed. Biatec OIDC
/// holds the receipt that verifies.
Map<String, Object?> redactReceipt(Map<String, Object?> receipt) {
  final signed = receipt['signed'];
  if (signed is! Map || !signed.containsKey('binding_code')) return receipt;
  return {
    ...receipt,
    'signed': {...signed.cast<String, Object?>()}..remove('binding_code'),
    'redacted': const ['signed.binding_code'],
  };
}

/// One decision this device signed and Biatec OIDC accepted, with the signed
/// receipt (without the one-time binding code, see [redactReceipt]) so the
/// user can look at what was signed.
class HistoryEntry {
  const HistoryEntry({
    required this.authReqId,
    required this.decision,
    required this.summary,
    required this.clientLabel,
    required this.decidedAt,
    required this.receipt,
  });

  final String authReqId;

  /// `approve`, `deny` or `code_failed`.
  final String decision;

  /// One line describing the action, as shown on the card.
  final String summary;
  final String clientLabel;
  final DateTime decidedAt;
  final Map<String, Object?> receipt;

  Map<String, Object?> toJson() => {
        'auth_req_id': authReqId,
        'decision': decision,
        'summary': summary,
        'client': clientLabel,
        'decided_at': decidedAt.toUtc().millisecondsSinceEpoch,
        'receipt': receipt,
      };

  factory HistoryEntry.fromJson(Map<String, Object?> json) => HistoryEntry(
        authReqId: json['auth_req_id'] as String,
        decision: json['decision'] as String,
        summary: json['summary'] as String,
        clientLabel: json['client'] as String,
        decidedAt: DateTime.fromMillisecondsSinceEpoch(
            json['decided_at'] as int,
            isUtc: true),
        // Entries written before the code was dropped lose it here, and on disk at the next write.
        receipt: redactReceipt((json['receipt'] as Map).cast<String, Object?>()),
      );

  /// This entry with the binding code removed from its receipt.
  HistoryEntry redacted() => HistoryEntry(
        authReqId: authReqId,
        decision: decision,
        summary: summary,
        clientLabel: clientLabel,
        decidedAt: decidedAt,
        receipt: redactReceipt(receipt),
      );
}

/// Local approval history, newest first, capped at [maxEntries].
abstract class HistoryStore {
  static const maxEntries = 100;

  Future<List<HistoryEntry>> read();
  Future<void> add(HistoryEntry entry);
}

List<HistoryEntry> _prepend(List<HistoryEntry> list, HistoryEntry e) => [
      e.redacted(),
      ...list.where((x) => x.authReqId != e.authReqId),
    ].take(HistoryStore.maxEntries).toList();

class SecureHistoryStore implements HistoryStore {
  SecureHistoryStore([FlutterSecureStorage? storage])
      : _storage = storage ?? deviceBoundStorage;

  static const _key = historyStorageKey;
  final FlutterSecureStorage _storage;

  @override
  Future<List<HistoryEntry>> read() async {
    final raw = await _storage.read(key: _key);
    if (raw == null) return const [];
    final out = <HistoryEntry>[];
    try {
      for (final item in jsonDecode(raw) as List) {
        try {
          out.add(HistoryEntry.fromJson((item as Map).cast<String, Object?>()));
        } on Object {
          // Skip a corrupt entry.
        }
      }
    } on Object {
      return const []; // corrupt history reads as empty
    }
    // Entries stored before the binding code was dropped: rewrite the storage once, so the code does not stay on disk until the next approval.
    if (raw.contains('binding_code')) {
      await _storage.write(
          key: _key, value: jsonEncode([for (final e in out) e.toJson()]));
    }
    return out;
  }

  @override
  Future<void> add(HistoryEntry entry) async {
    final list = _prepend(await read(), entry);
    await _storage.write(
        key: _key, value: jsonEncode([for (final e in list) e.toJson()]));
  }
}

class MemoryHistoryStore implements HistoryStore {
  List<HistoryEntry> _list = const [];

  @override
  Future<List<HistoryEntry>> read() async => _list;

  @override
  Future<void> add(HistoryEntry entry) async => _list = _prepend(_list, entry);
}
