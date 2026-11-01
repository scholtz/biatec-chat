import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';

import '../core/otp_account.dart';
import 'vault_key_store.dart';
import 'vault_storage.dart';

/// What the vault lists for an account: everything but the secret. Needs no biometric.
class VaultAccountInfo {
  const VaultAccountInfo({
    required this.id,
    required this.issuer,
    required this.label,
    required this.algorithm,
    required this.digits,
    required this.period,
    required this.type,
    required this.counter,
  });

  final String id;
  final String issuer;
  final String label;
  final OtpAlgorithm algorithm;
  final int digits;
  final int period;
  final OtpType type;
  final int counter;

  /// Names no issuer or label: they are usually an email address.
  @override
  String toString() => 'VaultAccountInfo(id $id, ${type.name})';
}

class _Entry {
  _Entry(this.info, this.record);
  final VaultAccountInfo info;

  /// The sealed secret, as stored.
  final Uint8List record;
}

class _State {
  _State(this.version, this.entries);
  final int version;
  final List<_Entry> entries;
}

/// The encrypted store of two-factor accounts.
///
/// Each secret is sealed on its own under the hardware secret key (a biometric to add or read one) with the account id as associated data, so a record cannot be moved to another account. Issuer, label and parameters are sealed together in an index under the metadata key (no biometric), which also carries a digest of every record, and is bound to the file's version number. The version is mirrored in [VaultVersionStore], so a file that is changed, cut short, swapped or replaced by an older one is refused, never decrypted. Writes are atomic.
///
/// Nothing here logs, prints or puts a secret in an exception. Secrets that pass through are wiped when the call is done.
class TotpVault {
  TotpVault({
    required this.keys,
    required this.storage,
    required this.versions,
    Random? random,
  }) : _random = random ?? Random.secure();

  final VaultKeyStore keys;
  final VaultStorage storage;
  final VaultVersionStore versions;
  final Random _random;

  static const _format = 1;

  // One operation at a time: each reads the file and writes the next version.
  Future<void> _tail = Future.value();

  Future<T> _serial<T>(Future<T> Function() op) {
    final done = _tail.then((_) => op());
    _tail = done.then<void>((_) {}, onError: (Object _) {});
    return done;
  }

  Uint8List _indexAad(int version) =>
      Uint8List.fromList(utf8.encode('biatec.vault.index.v1|$version'));

  Uint8List _recordAad(String id) =>
      Uint8List.fromList(utf8.encode('biatec.vault.secret.v1|$id'));

  String _newId() {
    final bytes = List<int>.generate(16, (_) => _random.nextInt(256));
    return bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
  }

  String _digest(Uint8List record) =>
      base64.encode(sha256.convert(record).bytes);

  // ---- reading and writing the file ----

  Future<_State> _load() async {
    final bytes = await storage.read();
    final seen = await versions.read();
    if (bytes == null) {
      if (seen > 0) throw const VaultCorrupt('The vault file is missing.');
      return _State(0, []);
    }
    final int version;
    final Uint8List sealedIndex;
    final Map<String, Object?> records;
    try {
      final file =
          (jsonDecode(utf8.decode(bytes)) as Map).cast<String, Object?>();
      if (file['format'] != _format) throw const FormatException();
      version = file['version'] as int;
      sealedIndex = base64.decode(file['index'] as String);
      records = (file['records'] as Map).cast<String, Object?>();
    } on Object {
      throw const VaultCorrupt('The vault file is damaged.');
    }
    if (version < seen) throw const VaultRolledBack();
    final Uint8List plain;
    try {
      plain = await keys.open(
          VaultKeyPurpose.metadata, sealedIndex, _indexAad(version));
    } on VaultCorrupt {
      throw const VaultCorrupt('The vault index did not verify.');
    }
    try {
      final index =
          (jsonDecode(utf8.decode(plain)) as Map).cast<String, Object?>();
      final entries = <_Entry>[];
      final ids = <String>{};
      for (final raw in (index['accounts'] as List)) {
        final a = (raw as Map).cast<String, Object?>();
        final id = a['id'] as String;
        if (!ids.add(id)) throw const FormatException();
        final record = Uint8List.fromList(base64.decode(records[id] as String));
        if (_digest(record) != a['digest']) throw const FormatException();
        entries.add(_Entry(
            VaultAccountInfo(
              id: id,
              issuer: a['issuer'] as String,
              label: a['label'] as String,
              algorithm: OtpAlgorithm.values.byName(a['algorithm'] as String),
              digits: a['digits'] as int,
              period: a['period'] as int,
              type: OtpType.values.byName(a['type'] as String),
              counter: a['counter'] as int,
            ),
            record));
      }
      // A record the index does not list was added from outside.
      if (records.keys.any((k) => !ids.contains(k))) {
        throw const FormatException();
      }
      return _State(version, entries);
    } on Object {
      throw const VaultCorrupt('The vault is damaged.');
    }
  }

  Future<void> _save(_State state, List<_Entry> entries) async {
    final version = state.version + 1;
    final index = utf8.encode(jsonEncode({
      'accounts': [
        for (final e in entries)
          {
            'id': e.info.id,
            'issuer': e.info.issuer,
            'label': e.info.label,
            'algorithm': e.info.algorithm.name,
            'digits': e.info.digits,
            'period': e.info.period,
            'type': e.info.type.name,
            'counter': e.info.counter,
            'digest': _digest(e.record),
          }
      ]
    }));
    final sealedIndex = await keys.seal(VaultKeyPurpose.metadata,
        Uint8List.fromList(index), _indexAad(version));
    final file = utf8.encode(jsonEncode({
      'format': _format,
      'version': version,
      'index': base64.encode(sealedIndex),
      'records': {for (final e in entries) e.info.id: base64.encode(e.record)},
    }));
    // The file first, then the counter: a crash between them leaves a file newer than the counter, which is fine; the other order would call the previous file a rollback.
    await storage.write(Uint8List.fromList(file));
    await versions.write(version);
  }

  // ---- the API ----

  /// The accounts in their order, without secrets. Needs no biometric.
  Future<List<VaultAccountInfo>> list() =>
      _serial(() async => [for (final e in (await _load()).entries) e.info]);

  /// Stores [account] (a biometric is asked) and returns what was stored, with its new id. The caller wipes its own account.
  Future<VaultAccountInfo> add(OtpAccount account) => _serial(() async {
        final state = await _load();
        final id = _newId();
        final secret = account.copySecret();
        final Uint8List record;
        try {
          record =
              await keys.seal(VaultKeyPurpose.secret, secret, _recordAad(id));
        } finally {
          secret.fillRange(0, secret.length, 0);
        }
        final info = VaultAccountInfo(
          id: id,
          issuer: account.issuer,
          label: account.label,
          algorithm: account.algorithm,
          digits: account.digits,
          period: account.period,
          type: account.type,
          counter: account.counter,
        );
        await _save(state, [...state.entries, _Entry(info, record)]);
        return info;
      });

  /// The account with its secret (a biometric is asked). The caller calls [OtpAccount.wipe] when done. Throws [VaultBiometricsChanged] when the biometric key was invalidated.
  Future<OtpAccount> readSecret(String id) => _serial(() async {
        final state = await _load();
        final entry = _find(state, id);
        final secret = await keys.open(
            VaultKeyPurpose.secret, entry.record, _recordAad(id));
        try {
          final i = entry.info;
          return OtpAccount(
            issuer: i.issuer,
            label: i.label,
            secret: secret,
            algorithm: i.algorithm,
            digits: i.digits,
            period: i.period,
            type: i.type,
            counter: i.counter,
            id: i.id,
          );
        } finally {
          secret.fillRange(0, secret.length, 0);
        }
      });

  _Entry _find(_State state, String id) {
    for (final e in state.entries) {
      if (e.info.id == id) return e;
    }
    throw ArgumentError('No such account.');
  }

  VaultAccountInfo _with(VaultAccountInfo i,
          {String? issuer, String? label, int? counter}) =>
      VaultAccountInfo(
        id: i.id,
        issuer: issuer ?? i.issuer,
        label: label ?? i.label,
        algorithm: i.algorithm,
        digits: i.digits,
        period: i.period,
        type: i.type,
        counter: counter ?? i.counter,
      );

  /// Renames an account. Needs no biometric.
  Future<VaultAccountInfo> update(String id, {String? issuer, String? label}) =>
      _serial(() async {
        final state = await _load();
        final entry = _find(state, id);
        final i = OtpLimits.normalizeName(issuer ?? entry.info.issuer);
        final l = OtpLimits.normalizeName(label ?? entry.info.label);
        if (l.isEmpty ||
            i.length > OtpLimits.maxTextLength ||
            l.length > OtpLimits.maxTextLength ||
            OtpLimits.hasUnsafeCharacters(i) ||
            OtpLimits.hasUnsafeCharacters(l)) {
          throw OtpAccountException(
              OtpAccountProblem.name, 'This name is not allowed.');
        }
        final next = _with(entry.info, issuer: i, label: l);
        await _save(state, [
          for (final e in state.entries)
            if (e.info.id == id) _Entry(next, e.record) else e
        ]);
        return next;
      });

  /// Removes an account. Needs no biometric.
  Future<void> delete(String id) => _serial(() async {
        final state = await _load();
        _find(state, id);
        await _save(state, [
          for (final e in state.entries)
            if (e.info.id != id) e
        ]);
      });

  /// Puts the accounts in [ids] order, which must list every account once.
  Future<void> reorder(List<String> ids) => _serial(() async {
        final state = await _load();
        final byId = {for (final e in state.entries) e.info.id: e};
        if (ids.length != byId.length ||
            ids.toSet().length != ids.length ||
            ids.any((i) => !byId.containsKey(i))) {
          throw ArgumentError('The order must list every account once.');
        }
        await _save(state, [for (final i in ids) byId[i]!]);
      });

  /// Moves a counter-based account to its next counter and returns it. Needs no biometric: the counter is not secret.
  Future<int> incrementCounter(String id) => _serial(() async {
        final state = await _load();
        final entry = _find(state, id);
        if (entry.info.type != OtpType.hotp) {
          throw ArgumentError('Only a counter-based account has a counter.');
        }
        final next = entry.info.counter + 1;
        if (!OtpLimits.validCounter(next)) {
          throw OtpAccountException(
              OtpAccountProblem.counter, OtpLimits.counterMessage);
        }
        final info = _with(entry.info, counter: next);
        await _save(state, [
          for (final e in state.entries)
            if (e.info.id == id) _Entry(info, e.record) else e
        ]);
        return next;
      });
}
